# Independent swarm-health service contract

This **replaces** the original `swarm_health_service.py` measurement loop, not the
service or its repository. No Jellyfin/gostream embedding, database, bitmagnet
schema, crawler, torrent handles, payload download, announce, or candidate
sampling/enumeration. Only bounded caller-submitted v1 infohashes become health
observations. Movie and episode hashes use the identical protocol.

## Locked runnable image

`Dockerfile` packages this existing service and native bridge, **not** a new
service owner. The production entrypoint is exec-form
`["python3", "/app/swarm_health_service.py"]`; no checkout, compiler, pip, apt-get,
plugin, database or crawler runs at startup. Nothing is downloaded after pull.
The runtime is UID/GID **10001:10001**, HTTP **8095/TCP**, DHT **6881/UDP**,
SIGTERM shutdown (allow at least **10 seconds**). It runs with read-only root,
all capabilities dropped and no-new-privileges.

Mount `/var/lib/swarm-health` (or set `STATE_DIR`) as writable, retained storage
owned by 10001:10001, directory mode 0700. Container-engine named volumes inherit
that ownership from the image; Kubernetes PVCs need provisioning ownership or an
appropriate init/fsGroup arrangement. Do not override the entrypoint or mount
source/dependencies over `/app`. Keep one owner per volume; use Recreate. A failed
permission check must fail startup, not silently switch to ephemeral state.
The service persists node identity/contacts only, never observations or secrets.

The image targets **linux/amd64**. Its Debian trixie base is pinned by the exact
amd64 OCI manifest digest in the Dockerfile. Both stages use the signed Debian **20261124T000000Z** snapshot
and compare the **entire** downloaded archive set against checked-in SHA256 locks
before offline installation: `build-packages.sha256` (53 archives),
`runtime-packages.sha256` (15 archives). This pins all transitive dependencies,
not only top-level names. Native libtorrent is **2.0.11-1**, Python
**3.13.5-2+deb13u5**, compiler GCC **14.2.0-19**, nlohmann JSON **3.11.3-2.1**.
There is **no Python libtorrent binding**. Base content is pinned by OCI digest;
added package provenance is in the two locks. Regenerate deliberately using
`lock-packages.sh build|runtime` inside the exact base, review every changed
version/hash, then rebuild and rerun tests. Never replace a failed snapshot/hash
check with a floating mirror or disable signature checks.

`/usr/share/swarm-health/` contains source revision/date, Dockerfile and native
source, dependency locks, full build/runtime dpkg inventories, application hashes
and hashes of the interpreter and linked libraries. The revision is also the OCI
`org.opencontainers.image.revision` label; builds refuse content that differs
from that commit. Dependency/application bytes are reproducible from these locked
inputs; OCI serialization/layer metadata can differ across Docker/Podman versions,
so consumers must pin the **tested published digest**, not infer one from a tag.

Run from the repository root, with Python 3 and Podman (or `CONTAINER_ENGINE=docker`):

```sh
./scripts/tests/p05-swarm-health-service-image.test.sh
# Or verify an already published artifact (digest and exact source must be supplied):
SWARM_HEALTH_IMAGE=registry.example.com/images/swarm-health@sha256:<digest> \
SWARM_HEALTH_SOURCE_REVISION=<full-source-commit> \
./scripts/tests/p05-swarm-health-service-image.test.sh
```

The test builds the locked image by default; an override **must** be a digest and
is pulled. It verifies image config, packaged source hashes, UID, library version
and checksums. It boots the actual production ENTRYPOINT in a separate container
sharing a `--network none` namespace with the service-contract controlled UDP
fixture. HTTP tests prove real scrape counts vs unknown, then SIGTERM exit=0,
checksummed 0600 state, container replacement on the same volume with a changed
bind IP and **no bootstrap**, preserved identity, restored contacts and new UDP
queries. Read-only state must fail closed. Only fixture code is copied to the
fixture container: no source or native dependency mounts into the service.

`.gitea/workflows/swarm-health-image.yml` uses the existing self-hosted Gitea
runner and nixery tools. Configure repo variables `REGISTRY_URL`,
`REGISTRY_USERNAME`, secret `REGISTRY_TOKEN` with package-write permission. It
publishes `<registry>/<username>/swarm-health:<full-source-sha>` only after the
image test passes, then pulls the digest and repeats the same test. The exact
`PUBLISHED_AND_VERIFIED` digest and JSON inventories are in its logs. Registry
credentials live in a temporary Docker client config outside the build context;
never in build args, source, layers or runtime defaults. No registry/site address
is compiled into the service. The image tag is discoverability, not the deploy
contract; Flux consumes that run's immutable digest. This workflow publishes only
and never changes Flux, the plugin image, production routing or a database.

## Image and deployment handoff

Runtime: Python **3.11+**, maintained **libtorrent-rasterbar 2.0.x** (tested against
2.0.11), and the `dht-bridge` executable. Build that executable in the image's
build stage with a C++17 compiler, pkg-config, libtorrent-rasterbar development
headers and nlohmann-json3 headers:

```sh
OUTPUT=/build/dht-bridge ./tools/swarm-health/build-bridge.sh
```

Run from the repository root. Copy `swarm_health_service.py` and `dht-bridge` next
to each other in the final image, with the matching libtorrent runtime libraries.
Entrypoint: `python3 /app/swarm_health_service.py`. `DHT_BRIDGE` can override the
absolute executable path. Do not compile at service start or install a Python
`libtorrent` substitute. The upstream Python binding omits `dht_direct_request`;
the narrow native bridge exposes this maintained C++ API, not a second DHT stack.

**Required configuration change from the operator-delivered baseline:** supply
`DHT_LISTEN_HOST` as one concrete IPv4 interface address (e.g. the pod IP through
Downward API). Wildcards are refused: libtorrent can expand them to multiple
interfaces/nodes. Supply `DHT_BOOTSTRAP` for a cold start. No deployment endpoints
or credentials are compiled in. Configure a writable retained volume at
`STATE_DIR`, owned by the non-root runtime UID. One replica per state volume; use
Recreate/otherwise stop the old process before another opens that volume.

Exactly one libtorrent session/node lives in one native child for the HTTP
process's lifetime. No per-request sessions. It is read-only on the DHT (no
inbound item/peer storage), disables TCP/uTP torrent connections, LSD, UPnP and
NAT-PMP. Native bootstrap/bucket maintenance can use libtorrent's random-key
`get_peers` internally; these maintain routing only, **never** become application
candidates/observations/cache entries. The bounded demand traversal does not call
libtorrent's unconstrained `dht_get_peers` traversal API. It sends one direct
`scrape=1` `get_peers` per visited endpoint, using the same node identity/socket.

Deploy behind a trusted internal Service/network policy. HTTP is plaintext;
terminate TLS at the deployment boundary if needed. `API_TOKEN` enables Bearer
auth for measurement/cancellation. Unset means trusted-network access, not
loopback-only. `/healthz` and `/metrics` remain unauthenticated for probes/scrapes;
do not expose them publicly. Inject secrets at runtime. Update image/config and
restart the service to load new settings; merely changing a ConfigMap is not
sufficient. No plugin DB change or plugin DLL version bump is part of this service
change. Image/Flux publication and live plugin acceptance are separate tasks.

## HTTP API

All connections close after one response. Request bodies are JSON UTF-8;
Content-Length and strict chunked framing are supported. Duplicate headers,
Content-Length + Transfer-Encoding, negative lengths, unsupported encodings,
chunk extensions/trailers, malformed CRLF and excessive chunks are rejected.
Request line+headers, total body bytes, chunk count and whole-request read time
are bounded *before* declared payload allocation/read. A slow byte stream does
not reset the read deadline. Pipelining is unsupported.

### `POST /v1/measure` — full nullable observation API

```json
{"info_hashes":["0123456789abcdef0123456789abcdef01234567"],"priority":0,"request_id":"client-42"}
```

- `info_hashes`: 1..MAX_BATCH 40-hex strings, case-normalized and deduplicated.
  No torrent names/magnets, DB IDs, or autonomous expansion inputs.
- `priority`: optional integer `0` interactive, `1` normal (default), `2` background.
- `request_id`: optional 1..64 ASCII letters/digits/underscore/hyphen. Generated
  UUID hex when absent. Active reuse returns 409. The same hash in other active
  requests shares work; request IDs themselves are not deduplication keys.

200 response contains `request_id`, `dht_nodes`, and identical `results` and
`observations` arrays in normalized input order. Each row is:

```json
{"info_hash":"0123456789abcdef0123456789abcdef01234567","dht_seeders":8,"dht_peers":20,"checked_at":1795680000,"measured_at":1795680000,"status":"ok","observed_peers":3,"queries":8,"replies":8,"retry_after_s":0}
```

`checked_at` is UTC Unix **seconds at the latest accepted DHT reply**, not HTTP
response time or cache-read time. `measured_at` is an exact compatibility alias.
Both are null when no valid observation arrived. `queries` counts actual direct
queries sent, `replies` counts valid response dictionaries; these are not peer
counts. `observed_peers` is a bounded **distinct compact IP+port** union from
`values` across replies (v4-mapped v6 normalized). Saturating this union marks the
observation partial, never silently complete.

`dht_seeders` is the rounded BEP33 `BFsd` union estimate. `dht_peers` is total
participants: the rounded **union of BFsd and BFpe**, raised to at least the
observed endpoint count. This preserves the original consumer's all-peers meaning,
not a leechers-only count. BEP33 filters deduplicate IPs (including v6); endpoint
observations deduplicate IP+port. Counts are estimates/bounded observations, not a
census, proof of connectivity, current availability or guaranteed playback.
Bloom unions are bitwise OR, **not summed per-node counts**. For k=2, m=2048:
`log(zero_bits/2048)/(2*log(1-1/2048))`. Empty filters are exactly zero; any nonempty
filter estimates at least one; saturation is unknown rather than infinity/NaN.

| status | Meaning and nullability |
| --- | --- |
| `ok` | Every visited node supplied valid 256-byte seed+peer filters; no local timeout or budget/truncation. Counts describe this bounded sample, not the entire global DHT. |
| `partial` | Some positive/scrape observation, but missing/malformed/unsupported replies, saturation, deadline, or query/node/peer truncation. Positive counts may be returned; a would-be zero is **null**, not confirmed dead. |
| `unsupported` | Valid DHT replies, but no usable bloom pair or positive peer values. Counts null; no assumption that the swarm is dead. Includes malformed bloom data. |
| `timeout` | No valid replies before the bounded attempt ended (also when native transaction slots remained occupied). Counts and observation timestamps null, even if the routing table is otherwise healthy. |

Only an `ok` observation with explicit empty bloom filters throughout the
contacted sample confirms a **sampled zero**. Missing fields, silence, a full
routing table, and empty legacy `values` lists never confirm zero. A positive
legacy peer reply without BEP33 support yields `partial`, known observed peers,
and null seeders. Incomplete outcomes carry `retry_after_s` for backoff; consumers
must preserve provenance, distinguish unknown from zero and age prior data.

### `POST /measure` — original consumer compatibility

Same request and row fields. `observations` always contains full nullable rows;
`results` **omits rows with unknown `dht_peers`**. This is deliberate: the existing
C# client expects an int, ignores additive fields, and an omitted int property
would deserialize to false zero while a JSON null would fail the batch. Known
positive partial observations and explicitly confirmed sampled zeros remain in
`results`; unknowns are never assigned a fabricated count or timestamp. Existing
`measured_at` readers retain that field. New ingestion should adopt
`/v1/measure` and inspect status/checked_at, not infer freshness from HTTP 200.

The default MAX_BATCH remains 256 to admit the existing worker's **128-hash**
batch. If that legacy batch reaches REQUEST_TIMEOUT_S, return 200 with completed
results plus any positive partial snapshot, `request_timed_out: true` and
`retry_after_s`, rather than discard all progress. Unfinished rows in observations
carry `reason: "request_deadline"`; unqueried/silent rows are null with status
`timeout`, never zero. Snapshotting does not mutate/cache shared work; remaining
interests are then cancelled normally. This keeps the old int-valued client useful
without unbounded batch latency. `/v1/measure` retains explicit HTTP 504 at its
request deadline; clients there should submit appropriately small priority batches.

### Cancellation, deadlines and overload

`DELETE /measure/<request_id>` removes that request's interests: 202 if active,
404 otherwise. Its original waiting POST gets 409. The ID is reusable immediately
after release; cleanup is bound to the exact admitted request/future, so an old
handler's cancellation, disconnect, deadline or final cleanup cannot remove a
successor or its coalesced interests. A **new DELETE** always targets the currently
active request with that ID: use unique IDs if cancellation retries could outlive
a request. Socket disconnect also drops interests. A queued hash with no interests
is removed immediately; an active hash
with no interests stops scheduling immediately. Other coalesced requests remain
unaffected. Native UDP transactions **cannot be recalled**; their slots stay
charged until libtorrent's actual reply/timeout alert, even after cancellation,
late replies or the shorter application query deadline. Such late replies cannot
complete or contaminate a subsequent hash. No success/cancellation cache entry
is fabricated for cancelled work.

Admission of a batch is atomic. MAX_REQUESTS or MAX_QUEUE exhaustion returns 429;
connection-cap exhaustion returns a best-effort 503 then closes the socket
(possibly TCP reset after the response if unread bytes remain). Not-ready/failed
engine returns 503. The `/v1/measure` request wall deadline including queue wait
returns 504 and releases interests (legacy partial-result behavior is above). 429/503/504 include integer `Retry-After`; callers must back
off with jitter and avoid immediate retries. Invalid input 400, excessive payload
413, excessive headers 431, read deadline 408, missing/incorrect configured token
401. No retry loop lives in the service.

One active hash traversal is deliberate: bounded O(MAX_QUEUE) selection, FIFO
within priority, best priority across coalesced interests. Priority ages one
level per PRIORITY_AGING_S, eventually tying interactive work and winning by age.
Already-active work is not preempted except when cancelled/deadlined. Request
waiters are bounded by admission and the HTTP connection cap, not threads waiting
on a global measurement lock. At least two HTTP slots beyond MAX_REQUESTS must be
configured for control traffic (slow unauthenticated sockets can still occupy
them briefly, bounded by HTTP_READ_S).

## Limits/configuration

All settings are environment variables, read at startup. Invalid values fail
closed. Durations may be fractional; count/port settings must be integers.

| Variable | Default | Allowed range / contract |
| --- | --- | --- |
| LISTEN_HOST / LISTEN_PORT | 0.0.0.0 / 8095 | HTTP bind; port 1..65535 |
| DHT_LISTEN_HOST | **required** | One concrete local IPv4; no wildcard/multi-node expansion |
| DHT_PORT | 6881 | 0..65535; 0 ephemeral, fixed port recommended for deployment |
| DHT_BOOTSTRAP | empty | At most 16 host:port endpoints / 4096 characters. Required on cold start; saved contacts suffice on restart. |
| DHT_ALLOW_PRIVATE | 0 | Set 1 only for isolated controlled/private DHT tests; relaxes private-address/routing-IP restrictions |
| API_TOKEN | empty | Optional Bearer secret, at most 4096 ASCII characters |
| STATE_DIR / DHT_BRIDGE | /var/lib/swarm-health / sibling dht-bridge | Writable persistent directory / executable path |
| MAX_HTTP / MAX_REQUESTS | 64 / 32 | 4..256 / 1..128; requests <= HTTP slots - 2 |
| MAX_BATCH / MAX_QUEUE | 256 / 256 | 1..256 / 1..4096; queue budget includes active hash |
| MAX_BODY / MAX_HEADER | 16384 / 8192 | 128..1048576 / 256..16384 bytes |
| MAX_CHUNKS | 256 | 1..1024; chunk-size frame <=80 bytes |
| HTTP_READ_S / HTTP_WRITE_S | 5 / 3 | .1..30 / .1..10; read deadline covers headers + entire body |
| REQUEST_TIMEOUT_S | 60 | .1..300, queue + all results; then release interests |
| MEASURE_TIMEOUT_S / QUERY_TIMEOUT_S | 8 / 2 | .1..30 / .05..10; work/query observation window |
| MAX_QUERIES / MAX_INFLIGHT | 24 / 8 | 1..128 per hash / 1..16 **native outstanding**, not just application-active |
| MAX_NODES / MAX_PEERS | 128 / 512 | 1..256 contacts per traversal/export / 1..4096 distinct peer endpoints per hash |
| CACHE_SIZE / CACHE_TTL_S | 4096 / 3600 | 1..100000 entries / .1..86400 seconds |
| UNKNOWN_TTL_S | 5 | .1..60; incomplete-result TTL is min(this, CACHE_TTL_S) |
| PRIORITY_AGING_S / RETRY_AFTER_S | 5 / 2 | .1..60 / 1..60 seconds |
| MIN_DHT_NODES | 20 | 0..2560; readiness only, never used to manufacture zero counts |
| STATE_SAVE_S | 60 | .1..600 seconds, also save on SIGTERM/SIGINT |
| DHT_UPLOAD_RATE | 8000 | 1000..65536 bytes/sec libtorrent DHT output quota |

The old SETTLE_S no longer controls anything: completion requires replies or
explicit bounded incomplete outcomes, not a silence-based settling heuristic.

Cache is a hard-count LRU with monotonic TTL; expired entries are removed on driver
ticks and admission, even without cache reads. Hits return original observation
timestamps unchanged. Partial/unknown backoff entries are bounded too. Cache and
pending demand never persist across restart.

The native bridge accepts fixed <=256 KiB IPC frames, with a 3-second RPC deadline;
its maximum 16 outstanding replies fits that frame bound. A failed/stalled native
RPC can add at most that 3-second failure deadline beyond a work window before
the engine fails closed; HTTP queue/request/read/write deadlines are independent. It rejects responses
beyond 4096 encoded bytes, extracts only bounded fields/values, and relies on
libtorrent's own smaller UDP packet/parser limits. Alerts are bounded to 256.
No unbounded Python bdecode of untrusted DHT traffic. Direct queries are globally
inflight-bounded, per-hash count/wall-bounded; libtorrent's routing maintenance is
separate and upload-rate limited. Its non-extended routing table has a fixed
160-bit key space, 8-node buckets and bounded replacements (at most 2560 contacts
including replacements). Exported/queued/persisted contacts additionally have
MAX_NODES caps. It is read-only with storage limits set to one as defense in depth.
Container CPU/memory/network limits remain appropriate protection against UDP
floods; there is no claim that arbitrary hostile traffic costs zero CPU.

## Persistence and recovery

`STATE_DIR/dht-state.json` is a version-1 JSON envelope containing hex libtorrent
DHT-only session state and SHA256 integrity checksum (not an authentication MAC).
It contains one node ID and bounded bootstrap contacts, **no** hash submissions,
health observations, credentials, plugin data or library settings. Files are
bounded to 64 KiB before decode. Native bdecode additionally has depth/token limits
and requires exactly one node ID and bounded IPv4 contacts. Restore remaps the
saved ID to the configured concrete local interface so a new private pod IP does
not lose continuity. Libtorrent may still rotate identity when required by an
external-address/network change; no guarantee across a changed public identity.

State writes use a private-mode temporary file, flush+fsync, atomic same-directory
replace, then directory fsync. A lifetime `node.lock` flock rejects a second owner.
A leftover `.tmp` from a crash is ignored and overwritten, not loaded. Missing
state bootstraps normally. Oversize, checksum/version, malformed JSON (including
parser-depth exhaustion within the byte limit) or invalid native state cold-starts
with a recovery counter; the next valid save replaces it
(no unbounded quarantine history). **Bootstrap must be configured for corruption
recovery.** If bootstrap is absent and saved contacts are unusable, readiness stays
503, never a false healthy result. Mount a trusted directory; do not share it with
untrusted writers. Other I/O/permission errors fail closed instead of being treated
as ordinary corruption. Native bridge/IPC failure stops admission and exits for
the deployment supervisor; it never silently falls back to another DHT node.

Initialize DHT disabled, apply initial settings, then enable once: libtorrent 2.0
otherwise can discard restored state during startup with empty bootstrap config.
The integration test specifically restarts with **no configured bootstrap** and a
changed local bind IP, verifies the same node ID and makes a successful new query.

## Metrics (fixed cardinality)

`GET /metrics`: Prometheus text. Every series has prefix `swarm_health_`; **no
labels**, request paths, infohashes, peer IPs, node IDs, errors or request IDs. Types
are counter (`*_total`) or gauge. Enumerated series:

- Outcomes: `outcome_{ok,partial,unsupported,timeout}_total` (one per measured hash,
  not per HTTP waiter); `started_total`, `queries_total`, `query_timeouts_total`,
  `budget_exhausted_total`, `coalesced_total`, `cancelled_total`, `request_timeouts_total`, `overload_total`,
  `http_rejected_total`, `http_write_timeouts_total`, `engine_errors_total`.
- Cache: `cache_hits_total`, `cache_expired_total`, `cache_evictions_total`,
  `cache_entries`.
- State: `state_loads_total`, `state_saves_total`, `state_recoveries_total`.
- Health/budgets: `dht_nodes`, `node_contacts`, `native_pending`, `queue`, `active`,
  `requests`, `http_clients`, `ready`, plus `limit_{max_queue,max_inflight,max_nodes,
  max_peers,max_queries,cache_size,max_http,max_requests}`.

`GET /healthz`: 200 only when driver healthy and routing count >= MIN_DHT_NODES;
otherwise 503. Body has `ready`, `dht_nodes`, `node_id` (identity belongs here,
never a metric label). Readiness is **not** a measurement/playability claim.
Old `result="dead"` metrics and fabricated zero histogram are removed rather than
kept as a parallel false-zero signal. Update dashboard queries to the new outcome
counters; do not equate timeout/unsupported with dead.

## Reproducible integration proof

```sh
./scripts/tests/p05-swarm-health-service-contract.test.sh
```

Runs unittest against a real standalone process + built libtorrent bridge and
four controlled UDP nodes (one bootstrap router and three responding contacts).
Checks the BEP33 published vector (1256 inserted IPs -> 1225 estimate), overlapping
bloom unions, deduplicated peers, explicit zero vs silence on a healthy table,
legacy omission, malformed/saturated filters, TTL/timestamps/eviction, query/peer/
node budgets, native slots after cancellation, cross-request coalescing/promotion,
aging, atomic overload, disconnect/DELETE/deadlines, the legacy default 128-hash
batch's bounded partial progress, HTTP framing/slowloris/auth,
constant metric cardinality and state restart/corruption/locking. Repeated real
DELETE/reuse socket races cover same-hash and different-hash successors alongside
coalesced waiters. Disconnect and both API deadline paths preserve shared work.
Deeply nested corrupt state must bootstrap, measure, increment recovery, atomically
replace the file, and restore successfully without bootstrap on the next restart;
real filesystem I/O errors must still fail closed.

When native build dependencies are absent, the runner builds `Dockerfile.test`
and reexecutes in rootless Podman with source mounted read-only. Python libtorrent
in that **test** image exists only to exercise the original baseline. The normal
service uses real C++ libtorrent, not the Python module and not a fake engine.
`swarm-health-service.test.sh` now delegates here; the obsolete selftest is removed.

For a negative control, set `SERVICE_UNDER_TEST` to an unmodified baseline script
in that container and `TEST_DHT_NETWORK=1` (old baseline hardcodes wildcard bind,
so use the container interface rather than loopback). Select
`Contract.test_unknown_is_not_zero_even_with_healthy_routing`: it fails because
the baseline emits `dht_peers: 0` for a silent hash. The same network case passes
with this service. These isolated tests are not final live service/plugin/image
acceptance; that remains `p05-swarm-health-acceptance`.
