# Operator deploy guide — patched Jellyfin assemblies

**Audience:** the single operator running Phantom Library on their own
box. This guide explains the one piece of the v0.3.0+ deploy that
`./install.sh` cannot do for you: replacing two of your Jellyfin
runtime DLLs with patched builds.

## Why this is required

Phantom Library v0.3.0 is built against a small additive patch to
Jellyfin core (`scripts/jellyfin-patches/0001..0003*.patch`). The
patch adds:

- `IChannelItemRefresh` — an opt-in interface a channel implementation
  declares to expose per-item refresh semantics. (new file under
  `MediaBrowser.Controller/Channels/`)
- `IChannelItemRefreshManager` — a new service that drives the above.
  (new file under `MediaBrowser.Controller/Channels/`, registered in
  `Jellyfin.LiveTv/Extensions/LiveTvServiceCollectionExtensions.cs`,
  implemented in `Jellyfin.LiveTv/Channels/ChannelManager.cs`)

The patch is purely additive — no existing API is mutated. But the
plugin DLL references these new types, so the runtime Jellyfin must
contain the corresponding patched assemblies. The unpatched
distro-package DLLs will produce a `TypeLoadException` when Jellyfin
tries to load the plugin.

Two DLLs need swapping:

- `MediaBrowser.Controller.dll`
- `Jellyfin.LiveTv.dll`

`./install.sh --build` builds the patched versions of both and prints
the exact deploy commands at the end of its output, with paths
pre-filled for your detected Jellyfin install dir.

## Deploy models

You have two options. Model A is what `install.sh` prints commands
for; Model B is for the more cautious operator who'd rather not
modify system files.

### Model A — in-place swap of the system Jellyfin DLLs (recommended)

```
sudo systemctl stop jellyfin

# Back up the originals once. Re-running install.sh later checks
# against these to detect clobber.
sudo cp -p /usr/lib/jellyfin/MediaBrowser.Controller.dll \
        /usr/lib/jellyfin/MediaBrowser.Controller.dll.pre-phantom-bak
sudo cp -p /usr/lib/jellyfin/Jellyfin.LiveTv.dll \
        /usr/lib/jellyfin/Jellyfin.LiveTv.dll.pre-phantom-bak

sudo install -m 644 \
  /path/to/repo/jellyfin/MediaBrowser.Controller/bin/Release/net9.0/MediaBrowser.Controller.dll \
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
       /usr/lib/jellyfin/Jellyfin.LiveTv.dll \
       /usr/lib/jellyfin/Jellyfin.LiveTv.dll.pre-phantom-bak
```

If the live DLL md5 matches the `.pre-phantom-bak` md5 for either
file, the patch has been clobbered. Re-run:

```
cd /path/to/repo
./install.sh --build   # rebuilds patched DLLs
# then re-run the install -m 644 commands from Model A above.
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
