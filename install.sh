#!/usr/bin/env bash
# Phantom Library — installer.
#
# Builds the plugin DLL, drops it into Jellyfin's plugins dir under
# the canonical versioned name, creates the writable phantom-stub
# tree required by M10, fixes ownership, and offers to restart
# Jellyfin via systemd.
#
# Idempotent. Safe to re-run after a `git pull && ./install.sh`.
#
# Usage:
#   ./install.sh                              # auto-detect everything
#   ./install.sh --jellyfin-data /custom/dir  # override data dir
#   ./install.sh --jellyfin-user myuser       # override service user
#   ./install.sh --no-restart                 # skip the systemctl prompt
#   ./install.sh --skip-build                 # use an existing build
#   ./install.sh --help

set -euo pipefail

# ---------------------------------------------------------------- helpers
red()    { printf '\033[0;31m%s\033[0m\n' "$*" >&2; }
yellow() { printf '\033[0;33m%s\033[0m\n' "$*"; }
green()  { printf '\033[0;32m%s\033[0m\n' "$*"; }
bold()   { printf '\033[1m%s\033[0m\n' "$*"; }

die() { red "ERROR: $*"; exit 1; }

usage() {
  sed -n '2,/^$/p' "$0" | sed 's/^# \{0,1\}//'
  exit 0
}

# Need sudo for /var/lib/jellyfin writes. If we're already root, no-op.
SUDO=""
if [ "$(id -u)" -ne 0 ]; then
  if command -v sudo >/dev/null 2>&1; then
    SUDO="sudo"
  else
    die "Not root and 'sudo' not available. Re-run as root or install sudo."
  fi
fi

# ---------------------------------------------------------------- args
JELLYFIN_DATA=""
JELLYFIN_USER=""
NO_RESTART=0
SKIP_BUILD=0

while [ $# -gt 0 ]; do
  case "$1" in
    --jellyfin-data)  JELLYFIN_DATA="$2"; shift 2 ;;
    --jellyfin-user)  JELLYFIN_USER="$2"; shift 2 ;;
    --no-restart)     NO_RESTART=1; shift ;;
    --skip-build)     SKIP_BUILD=1; shift ;;
    -h|--help)        usage ;;
    *) die "Unknown option: $1 (try --help)" ;;
  esac
done

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$REPO_ROOT"

# ---------------------------------------------------------------- detect
# Read canonical plugin version + GUID from build.yaml so the install
# layout matches what the manifest workflow publishes.
if [ ! -f build.yaml ]; then
  die "build.yaml missing in $REPO_ROOT — are you in the repo root?"
fi

PLUGIN_VERSION="$(awk -F'"' '/^version:/ {print $2; exit}' build.yaml)"
PLUGIN_NAME="Jellyfin.Plugin.PhantomLibrary"
PLUGIN_DIR_NAME="${PLUGIN_NAME}_${PLUGIN_VERSION}"
DLL_OUT="src/${PLUGIN_NAME}/bin/Release/net9.0/${PLUGIN_NAME}.dll"

# Detect Jellyfin data dir if not overridden. Order of preference:
#   1. --jellyfin-data flag.
#   2. /var/lib/jellyfin if it exists (Arch / Debian / RHEL packages).
#   3. $XDG_DATA_HOME/jellyfin (per-user install).
#   4. ~/.config/jellyfin/data (older per-user layout).
if [ -z "$JELLYFIN_DATA" ]; then
  for cand in /var/lib/jellyfin "${XDG_DATA_HOME:-$HOME/.local/share}/jellyfin" "$HOME/.config/jellyfin/data"; do
    if [ -d "$cand" ]; then
      JELLYFIN_DATA="$cand"
      break
    fi
  done
fi

if [ -z "$JELLYFIN_DATA" ]; then
  die "Could not auto-detect Jellyfin data directory. Pass --jellyfin-data <path>."
fi

if [ ! -d "$JELLYFIN_DATA" ]; then
  die "Jellyfin data directory '$JELLYFIN_DATA' does not exist."
fi

# Detect the Jellyfin service user if not overridden.
# /var/lib/jellyfin is owned by the service user on package installs.
if [ -z "$JELLYFIN_USER" ]; then
  JELLYFIN_USER="$(stat -c '%U' "$JELLYFIN_DATA" 2>/dev/null || echo '')"
  if [ -z "$JELLYFIN_USER" ] || [ "$JELLYFIN_USER" = "UNKNOWN" ]; then
    JELLYFIN_USER="jellyfin"
    yellow "Could not detect Jellyfin user from $JELLYFIN_DATA; assuming '$JELLYFIN_USER'."
  fi
fi

# Group: assume same as user; if a group with that name doesn't exist,
# fall back to the primary group of the user.
if getent group "$JELLYFIN_USER" >/dev/null 2>&1; then
  JELLYFIN_GROUP="$JELLYFIN_USER"
else
  JELLYFIN_GROUP="$(id -gn "$JELLYFIN_USER" 2>/dev/null || echo "$JELLYFIN_USER")"
fi

PLUGINS_DIR="$JELLYFIN_DATA/plugins/${PLUGIN_DIR_NAME}"
PHANTOM_STUB_ROOT="$JELLYFIN_DATA/phantom-library"

bold "Phantom Library installer"
echo "  Repo:               $REPO_ROOT"
echo "  Plugin version:     $PLUGIN_VERSION"
echo "  Jellyfin data dir:  $JELLYFIN_DATA"
echo "  Jellyfin user:grp:  $JELLYFIN_USER:$JELLYFIN_GROUP"
echo "  Plugin dest:        $PLUGINS_DIR"
echo "  Phantom stub root:  $PHANTOM_STUB_ROOT"
echo

# ---------------------------------------------------------------- build
if [ "$SKIP_BUILD" -eq 0 ]; then
  if ! command -v dotnet >/dev/null 2>&1; then
    die "'dotnet' not found in PATH. Install the .NET 9 SDK or pass --skip-build."
  fi
  bold "Building plugin (Release)..."
  dotnet build -c Release
  echo
else
  yellow "Skipping build (--skip-build)."
fi

if [ ! -f "$DLL_OUT" ]; then
  die "Built DLL not found at $DLL_OUT. Re-run without --skip-build, or check the build output."
fi

# ---------------------------------------------------------------- install dll
bold "Installing plugin DLL..."
$SUDO mkdir -p "$PLUGINS_DIR"
$SUDO install -m 644 "$DLL_OUT" "$PLUGINS_DIR/${PLUGIN_NAME}.dll"
$SUDO chown -R "$JELLYFIN_USER:$JELLYFIN_GROUP" "$PLUGINS_DIR"

# Verify md5 to catch the "I copied the stale build" footgun.
SRC_MD5="$(md5sum "$DLL_OUT" | awk '{print $1}')"
DST_MD5="$(md5sum "$PLUGINS_DIR/${PLUGIN_NAME}.dll" | awk '{print $1}')"
if [ "$SRC_MD5" != "$DST_MD5" ]; then
  die "md5 mismatch after install: src=$SRC_MD5 dst=$DST_MD5. Aborting."
fi
green "  ${PLUGIN_NAME}.dll installed (md5 $SRC_MD5)"

# ---------------------------------------------------------------- phantom stub tree (M10)
bold "Creating phantom stub tree (M10)..."
$SUDO mkdir -p "$PHANTOM_STUB_ROOT/movies" "$PHANTOM_STUB_ROOT/shows"
$SUDO chown -R "$JELLYFIN_USER:$JELLYFIN_GROUP" "$PHANTOM_STUB_ROOT"
# 755 so the jellyfin user can read+traverse, others can read (operator
# inspection); only the jellyfin user can write symlinks.
$SUDO chmod 755 "$PHANTOM_STUB_ROOT" "$PHANTOM_STUB_ROOT/movies" "$PHANTOM_STUB_ROOT/shows"
green "  $PHANTOM_STUB_ROOT/{movies,shows} ready"
yellow "  NOTE: if your plugin config has PhantomStubRoot set to a"
yellow "        different path, also chown that path to $JELLYFIN_USER:$JELLYFIN_GROUP."

# ---------------------------------------------------------------- post-install hints
echo
bold "Next steps:"
echo "  1. Restart Jellyfin to load the plugin (see prompt below)."
echo "  2. Dashboard → Plugins → Phantom Library → set TMDB key, gostream URLs,"
echo "     indexer settings, etc. Save."
echo "  3. Dashboard → Scheduled Tasks → 'Phantom: Refresh Suggestions' → Run"
echo "     (or wait for the next scheduled run)."
echo
echo "  Plugin DB will be created on first run at:"
echo "    $JELLYFIN_DATA/plugins/configurations/PhantomLibrary/phantom.db"
echo

# ---------------------------------------------------------------- restart
if [ "$NO_RESTART" -eq 1 ]; then
  yellow "Skipping Jellyfin restart (--no-restart). Restart manually to load the new DLL."
  exit 0
fi

if ! command -v systemctl >/dev/null 2>&1; then
  yellow "systemctl not available; skipping restart. Restart Jellyfin manually."
  exit 0
fi

# Detect Jellyfin systemd unit name. Common variants:
#   jellyfin.service           (distro packages)
#   jellyfin-server.service    (some setups)
JELLYFIN_UNIT=""
for unit in jellyfin.service jellyfin-server.service; do
  if systemctl list-unit-files "$unit" >/dev/null 2>&1; then
    if systemctl list-unit-files "$unit" 2>/dev/null | grep -q "$unit"; then
      JELLYFIN_UNIT="$unit"
      break
    fi
  fi
done

if [ -z "$JELLYFIN_UNIT" ]; then
  yellow "No Jellyfin systemd unit detected. Restart Jellyfin manually."
  exit 0
fi

echo
read -r -p "Restart $JELLYFIN_UNIT now? [y/N] " ans
case "${ans:-}" in
  y|Y|yes|YES)
    bold "Restarting $JELLYFIN_UNIT..."
    $SUDO systemctl restart "$JELLYFIN_UNIT"
    sleep 2
    if $SUDO systemctl is-active --quiet "$JELLYFIN_UNIT"; then
      green "  $JELLYFIN_UNIT is active."
    else
      red "  $JELLYFIN_UNIT failed to become active. Check: journalctl -u $JELLYFIN_UNIT -n 50"
      exit 1
    fi
    ;;
  *)
    yellow "Skipped restart. Run: sudo systemctl restart $JELLYFIN_UNIT"
    ;;
esac

green "Done."
