# Cross-Pod RO FUSE fan-out contract (P4 Stage B prep)

Status: spec only, no code change in this task. Written so gostream's standalone-split and flux's
topology change have a fixed target to satisfy, and so the plugin's existing stub-consumption code
(`PhantomMaterialisingMediaSourceProvider`, `GostreamPathResolver`) keeps working unmodified through the
split — or, where it cannot, so the required plugin change is flagged now rather than discovered mid-split.

## Today's topology (baseline, same-Pod)

Per `deploy/helm/phantom-library/templates/deployment.yaml`: one Pod per color contains both
containers, coupled through a single `hostPath` volume:

- `gostream` container: privileged, `mountPropagation: Bidirectional`, mounts the hostPath at
  `/mnt/gostream-mkv-virtual`. It is the FUSE **producer** — it owns the mount, creates/removes the
  virtual MKV tree as torrents materialise/evict.
- `jellyfin` container: unprivileged, `mountPropagation: HostToContainer`, mounts the **same**
  hostPath read-only at `/media/gostream`. It is the FUSE **consumer** — the phantom-library plugin,
  running inside this container, reads files under that mount.
- `values.yaml` documents the binding constraint explicitly: *"two FUSE servers cannot both mount the
  same host path concurrently on the one node"* — i.e. `virtualMkvHostPath` is unique per color
  specifically because the mount is host-node-scoped, not because of anything the plugin requires.

The plugin never talks FUSE/mount protocol itself. It has exactly two touchpoints with the gostream
side, and only one of them is coupled to colocation:

1. **HTTP control plane** (`Clients/GostreamClient.cs`): `AddAsync`/`RemoveAsync`/`ProbeAsync`/
   `PrestageAsync`/`UnprestageAsync` against `Configuration.GostreamBaseUrl`
   (default `http://127.0.0.1:9080`). This is already transport-agnostic — it works over any reachable
   HTTP endpoint. The `127.0.0.1` default is a same-Pod *convenience default*, not a hard requirement:
   an operator can already repoint it to a Service DNS name. **Not a blocker for the split**, provided
   the default is updated (see "Required changes" below).
2. **Local filesystem reads of the FUSE-produced tree** — the actual coupling this task exists to
   name:
   - `GostreamPathResolver.ResolvePath` (`Channels/GostreamPathResolver.cs:16-46`): resolves a
     gostream-returned `fuse_path` against `Configuration.GostreamMoviesRoot` /
     `GostreamShowsRoot` (defaults `/var/gostream/gostream-mkv-virtual/{movies,tv}`) using
     `File.Exists`, `Directory.Exists`, `Directory.EnumerateFiles` — all local-filesystem syscalls.
   - `PhantomMaterialisingMediaSourceProvider` (`Channels/PhantomMaterialisingMediaSourceProvider.cs`):
     - `GetMediaSources` (~L145-163): `File.Exists(GostreamPathResolver.Resolve*Path(...))` decides
       whether an item is already materialised vs. needs a "materialise on play" opener.
     - `OpenMediaSource` (~L182-199): re-`File.Exists`s the DB-stored materialised path before
       trusting it; deletes the stale DB row and re-materialises on a miss.
     - `WaitForFileAsync` (~L273-288): polls `File.Exists(path)` in a loop
       (`FusePathWaitTimeoutSeconds`/`FusePathPollIntervalMilliseconds`) after triggering
       materialisation, waiting for the FUSE producer to make the file visible.
     - `PhantomOpenedLiveStream.GetStream()`: `File.OpenRead(MediaSource.Path)` — a raw local open,
       not a range-request/remote read.

None of this code path holds an implicit "same Pod" check — it holds an implicit **"same mount
namespace, POSIX-visible at this path, with `Bidirectional`-propagated liveness"** assumption. That is
satisfied today only because gostream and jellyfin share one Pod's mounts; it is exactly what a
standalone-gostream / cross-Pod-share split changes.

## Required contract for a split (multi-Pod) topology

Whatever mechanism gostream's standalone-split and flux's topology change land on (e.g. a
`ReadWriteMany` PV, an NFS/CSI FUSE re-export, a sidecar bind-mount broker, or a wholly different
propagation channel), it MUST preserve these properties for the plugin's existing code to keep working
with **zero plugin code change**:

1. **POSIX path visibility.** The materialised file MUST be visible to the Jellyfin Pod's filesystem
   at a real, stable, `File.Exists`/`File.OpenRead`-able path — no plugin-side protocol change (no
   S3/HTTP-range/gRPC read path). If the new topology instead exposes files only over a network read
   API, that is a **plugin code change**, not something this contract can paper over — call it out
   explicitly to whichever task implements the split.
2. **Create/delete visibility is timely and monotonic.** `WaitForFileAsync`'s poll loop assumes: once
   gostream (or whatever produces the tree) creates the file, it becomes stat-visible to the consumer
   Pod within `FusePathWaitTimeoutSeconds` (default 60s) of the producer's write completing — not merely
   "eventually", and not "visible then flaps back to absent" (which the code treats as a hard failure,
   not a retry-worthy transient). Any cross-Pod fan-out with weaker-than-today propagation latency
   (e.g. NFS attribute-cache staleness) needs its cache/attr TTLs tuned below that timeout, or the
   timeout raised to match — a config value, not a plugin code change, but MUST be decided explicitly
   rather than left to whatever the new mechanism's default happens to be.
3. **Deletion is also visible.** Eviction (`ProtectFavourites`/`EvictionIdleDays`) relies on gostream's
   `RemoveAsync` actually removing the file from the tree the plugin stats; a consumer-side stale-cache
   that keeps reporting `File.Exists == true` after the producer deleted the file causes phantom-state
   drift only detected on next playback attempt (`OpenMediaSource`'s re-check), not proactively —
   acceptable today, but any added replication/caching layer must not make that staleness window
   materially worse (e.g. hours instead of the current same-node instant).
4. **Multiple concurrent RO consumers.** Once split, more than one Jellyfin Pod (this is the entire
   point of "cross-Pod RO FUSE fan-out" — presumably ≥2 replicas per `p4-gostream-standalone-rig`'s "≥2
   Jellyfin replicas" goal) must each independently satisfy points 1–3 against the SAME producer tree,
   read-only, without requiring per-consumer coordination the plugin doesn't already do (the plugin has
   no cross-instance locking — each Jellyfin process's `PhantomDb` is presumed per-Pod-local per
   `PhantomStubRoot=/var/lib/jellyfin/phantom-library`, a `jellyfin-var-lib-{color}` PVC already scoped
   per color/Pod, not shared — verify the new topology does NOT require sharing that PVC across
   consumer replicas, since nothing in the plugin coordinates writes to it across processes).
5. **No plugin-visible new failure mode without a matching retry/backoff.** If the new mount mechanism
   can transiently fail a stat where a `Bidirectional` hostPath mount never could (e.g. an NFS server
   restart, a CSI FUSE sidecar crash-loop), that failure surfaces to the plugin as an ordinary
   `File.Exists == false`/`IOException`, which today's code treats identically to "not materialised
   yet" (retries via `WaitForFileAsync`) or "genuinely gone" (`OpenMediaSource`'s delete-and-remake
   path) depending on WHEN it hits. A flaky new transport can therefore cause spurious
   re-materialisation (wasted torrent work) rather than a clean transient-retry — flag this to whoever
   designs the fan-out mechanism as a concrete failure mode to bound (e.g. FUSE sidecar readiness
   gating before the Jellyfin Pod is marked Ready).

## Same-Pod-colocation assumptions that WOULD need a plugin code change

These are the concrete places today's code (or config) directly encodes "we are in the same Pod" and
that a split topology breaks unless the mechanism above transparently preserves POSIX visibility:

- **`Configuration/PluginConfiguration.cs:26`** — `GostreamBaseUrl` default `http://127.0.0.1:9080`.
  Once gostream's control-plane container is no longer in the SAME Pod as jellyfin, `127.0.0.1` no
  longer resolves to it. **Required change** (config-only, not logic): change the shipped default to a
  Service DNS name (e.g. `http://gostream-<color>.<ns>.svc.cluster.local:9080`) once the standalone
  Service exists; existing installs already overriding this are unaffected. Same applies to
  `GostreamDiagnosticsBaseUrl` (`http://127.0.0.1:8090`).
- **`values.yaml`'s `virtualMkvHostPath` uniqueness comment** — this constraint is inherent to
  `hostPath` + same-node coupling. A cross-Pod fan-out mechanism that is NOT a `hostPath` (e.g. an
  RWX PV) removes this constraint entirely; if the split still uses `hostPath` semantics under the
  hood (e.g. one gostream Pod pinned to a node, consumers on the SAME node only), the uniqueness
  constraint and node-affinity requirements carry forward unchanged and must be preserved in the new
  Helm chart — a chart change, not a plugin code change.
- **`GostreamMoviesRoot`/`GostreamShowsRoot` defaults** (`/var/gostream/gostream-mkv-virtual/{movies,tv}`)
  — these are today's in-container mount path for the shared hostPath. As long as the new topology
  mounts the fanned-out tree at the SAME in-container path (the deployment.yaml comment already notes
  "the in-container mount path stays identical" is the intended invariant for the *producer* side across
  colors), these plugin config defaults need **no code change** — only a values/Helm change if the
  consumer-side mount path changes, which is a deploy-manifest edit, not a plugin recompile.
- **No code in the plugin assumes gostream and jellyfin share a Pod's network namespace, PID namespace,
  or any IPC other than the HTTP API + the POSIX filesystem.** Confirmed by grepping
  `src/Jellyfin.Plugin.PhantomLibrary/` for `127.0.0.1`/`localhost`/Unix-domain-socket usage: the only
  hits are the two `GostreamBaseUrl`/`GostreamDiagnosticsBaseUrl` defaults above. This means the
  colocation surface is narrow and already enumerated — good news for the split.

## Summary for the downstream split tasks

- **Zero required plugin code change** if the new topology: (a) keeps files POSIX-stat-visible at the
  currently-configured root paths from every consumer Pod, (b) propagates create/delete within the
  existing `FusePathWaitTimeoutSeconds` budget (or that budget is retuned via config), and (c) does not
  require the plugin to share per-Pod local state (`PhantomDb`, `jellyfin-var-lib-{color}` PVC) across
  consumer replicas.
- **One required config-default change** (not logic): update `GostreamBaseUrl` /
  `GostreamDiagnosticsBaseUrl` shipped defaults away from `127.0.0.1` once gostream's control plane
  moves to its own Pod/Service — a `PluginConfiguration.cs` one-line default change plus a Helm
  values/ConfigMap update, trivial and already flagged for `p4-gostream-standalone-rig` to carry out
  alongside the chart split.
- **One flagged risk, not a required change**: a fan-out mechanism with materially weaker
  attribute-cache/staleness guarantees than today's same-node `Bidirectional` hostPath mount can turn
  transient unavailability into spurious re-materialisation or delayed-eviction-visibility; the
  implementing task should choose/tune the mechanism to bound both within roughly today's near-instant
  same-node behavior, or explicitly accept and document the wider window.
