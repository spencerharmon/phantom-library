# Operator deploy guide — patched Jellyfin assemblies

**Audience:** the single operator running Phantom Library on their own
box. This guide explains the one piece of the v0.3.0+ deploy that
`./install.sh` must do outside the plugin directory: replacing patched
Jellyfin runtime DLLs with builds that match the plugin.

## Why this is required

Phantom Library v0.3.0 is built against a small additive patch to
Jellyfin core (`scripts/jellyfin-patches/*.patch`). The patch stack adds:

- `IChannelItemRefresh` — an opt-in interface a channel implementation
  declares to expose per-item refresh semantics. (new file under
  `MediaBrowser.Controller/Channels/`)
- `IChannelItemRefreshManager` — a new service that drives the above.
  (new file under `MediaBrowser.Controller/Channels/`, registered in
  `Jellyfin.LiveTv/Extensions/LiveTvServiceCollectionExtensions.cs`,
  implemented in `Jellyfin.LiveTv/Channels/ChannelManager.cs`)
- `IItemActionProvider` + `/Items/{itemId}/Actions` — a server-advertised
  item-action contract that lets Phantom expose Materialise / Reset /
  Reject actions through normal item menus instead of web-only DOM shims.

The patch is purely additive — no existing API is mutated. But the
plugin DLL references these new types, so the runtime Jellyfin must
contain the corresponding patched assemblies. The unpatched
distro-package DLLs will produce a `TypeLoadException` when Jellyfin
tries to load the plugin, and unpatched API/model DLLs will not expose
native item actions.

Four DLLs need swapping:

- `MediaBrowser.Controller.dll`
- `MediaBrowser.Model.dll`
- `Jellyfin.Api.dll`
- `Jellyfin.LiveTv.dll`

`./install.sh --build` builds the patched versions and, by
default, deploys them alongside the plugin DLL. The install script also
verifies the destination DLL hashes after copy. Use
`--no-deploy-jellyfin-dlls` only when you intentionally want to build but
not touch the runtime Jellyfin install.

## Runtime alignment contract

Phantom Library v0.3+ is sensitive to exact Jellyfin runtime/source
alignment:

- The in-tree Jellyfin source clone must be exact tag `v10.11.9`
  (base SHA `e83a7e62f2`) unless the patch stack has deliberately been
  rebased.
- The runtime Jellyfin installed on the operator box must also be
  `10.11.9` when installing this patch stack.
- The plugin DLL alone is not enough: `MediaBrowser.Controller.dll`,
  `MediaBrowser.Model.dll`, `Jellyfin.Api.dll`, and `Jellyfin.LiveTv.dll`
  in the runtime install dir must be the patched build outputs that
  correspond to the plugin.
- `./install.sh --build` is the expected deploy path for normal
  operator installs because it builds plugin + patched Jellyfin and
  deploys the patched runtime DLLs by default.
- After package-manager upgrades of Jellyfin, assume patched DLLs may
  have been clobbered. Re-run `./install.sh --build` before debugging
  plugin load or channel/playback failures.

Do not diagnose `TypeLoadException`, channel-refresh failures, or
native-open playback failures until the runtime DLL hashes have been
verified against the freshly built patched DLLs.

## Deploy models

You have two options. Model A is what `install.sh --build` performs by
default; Model B is for the more cautious operator who'd rather not
modify system files.

### Model A — in-place swap of the system Jellyfin DLLs (recommended)

```
sudo systemctl stop jellyfin

# Back up the originals once. Re-running install.sh later checks
# against these to detect clobber.
sudo cp -p /usr/lib/jellyfin/MediaBrowser.Controller.dll \
        /usr/lib/jellyfin/MediaBrowser.Controller.dll.pre-phantom-bak
sudo cp -p /usr/lib/jellyfin/MediaBrowser.Model.dll \
        /usr/lib/jellyfin/MediaBrowser.Model.dll.pre-phantom-bak
sudo cp -p /usr/lib/jellyfin/Jellyfin.Api.dll \
        /usr/lib/jellyfin/Jellyfin.Api.dll.pre-phantom-bak
sudo cp -p /usr/lib/jellyfin/Jellyfin.LiveTv.dll \
        /usr/lib/jellyfin/Jellyfin.LiveTv.dll.pre-phantom-bak

sudo install -m 644 \
  /path/to/repo/jellyfin/MediaBrowser.Controller/bin/Release/net9.0/MediaBrowser.Controller.dll \
  /usr/lib/jellyfin/
sudo install -m 644 \
  /path/to/repo/jellyfin/MediaBrowser.Model/bin/Release/net9.0/MediaBrowser.Model.dll \
  /usr/lib/jellyfin/
sudo install -m 644 \
  /path/to/repo/jellyfin/Jellyfin.Api/bin/Release/net9.0/Jellyfin.Api.dll \
  /usr/lib/jellyfin/
sudo install -m 644 \
  /path/to/repo/jellyfin/src/Jellyfin.LiveTv/bin/Release/net9.0/Jellyfin.LiveTv.dll \
  /usr/lib/jellyfin/

sudo systemctl start jellyfin
```

**Pros:** simple. The Jellyfin systemd unit you already have keeps
working without modification.

**Cons:** a `pacman -Syu` / `apt upgrade` / equivalent that touches
the `jellyfin-server` package will silently overwrite these DLLs and
your plugin will fail to load on the next restart. See "Detecting
package-manager clobber" below.

### Model B — run Jellyfin from a self-built tree

Skip swapping system DLLs entirely. Run Jellyfin from the build
output under `repo/jellyfin/Jellyfin.Server/bin/Release/net9.0/`
instead. You'll need to point your systemd unit at the new exe path
and pass `--datadir /var/lib/jellyfin` etc. so it still reads your
existing data dir.

Sketch:

```
# Override the unit to run from the self-built tree.
sudo systemctl edit jellyfin
# In the editor:
#   [Service]
#   ExecStart=
#   ExecStart=/path/to/repo/jellyfin/Jellyfin.Server/bin/Release/net9.0/jellyfin \
#     $JELLYFIN_WEB_OPT $JELLYFIN_FFMPEG_OPT $JELLYFIN_SERVICE_OPT \
#     $JELLYFIN_NOWEBAPP_OPT $JELLYFIN_ADDITIONAL_OPTS
sudo systemctl daemon-reload
sudo systemctl restart jellyfin
```

**Pros:** survives `pacman -Syu` of the jellyfin-server package
(though the package upgrade may still cause downtime if it stops
the service).

**Cons:** more moving parts. The self-built tree lives in the
repo and disappears if you `git clean -fdx`. The build directory
is large (~hundreds of MB). You're not running the distro-packaged
Jellyfin anymore; bug reports against the distro package no longer
apply.

Recommendation: Model A unless you have a strong reason for Model B.

## Detecting package-manager clobber

After any system update, before assuming Jellyfin is working
normally:

```
md5sum /usr/lib/jellyfin/MediaBrowser.Controller.dll \
       /usr/lib/jellyfin/MediaBrowser.Controller.dll.pre-phantom-bak \
       /usr/lib/jellyfin/MediaBrowser.Model.dll \
       /usr/lib/jellyfin/MediaBrowser.Model.dll.pre-phantom-bak \
       /usr/lib/jellyfin/Jellyfin.Api.dll \
       /usr/lib/jellyfin/Jellyfin.Api.dll.pre-phantom-bak \
       /usr/lib/jellyfin/Jellyfin.LiveTv.dll \
       /usr/lib/jellyfin/Jellyfin.LiveTv.dll.pre-phantom-bak
```

If the live DLL md5 matches the `.pre-phantom-bak` md5 for either
file, the patch has been clobbered. Re-run:

```
cd /path/to/repo
./install.sh --build   # rebuilds and redeploys patched DLLs by default
# or, if using --no-deploy-jellyfin-dlls, re-run the install -m 644
# commands from Model A above yourself.
```

You'll also see `TypeLoadException` for `IChannelItemRefreshManager`
in `journalctl -u jellyfin` immediately after the clobber.

## Maintenance: rebasing the patches

If you update `jellyfin/` to a newer upstream tag, the patches may
need rebasing. `./install.sh --build` aborts with an actionable
error if a patch fails to apply. See
`scripts/jellyfin-patches/REBASE.md` for the rebase procedure.

## Out of scope (for now)

- Packaging (rpm/deb/pacman of the patched Jellyfin). The current
  workflow assumes the operator builds from source.
- Detecting clobber automatically on Jellyfin startup. Future work
  may add a plugin-side health check that warns in the dashboard
  if the patched types are unreachable.
- Multi-host deploys. Single-operator only.
