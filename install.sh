#!/usr/bin/env bash
# Phantom Library - local operator installer.
#
# Installs the pre-built plugin DLL into Jellyfin's plugins dir under
# the canonical versioned name, creates the writable phantom-stub
# tree required by M10, fixes ownership, loads the patched gostream
# container image (mrrobotogit/gostream:testing) into root podman
# storage, and offers to restart Jellyfin via systemd.
#
# This is the operator's local installer; not for general users.
# By default it does NOT rebuild - the agent builds during testing,
# the operator runs this to install the already-built artefacts.
#
# Idempotent. Safe to re-run after a `git pull && ./install.sh`.
#
# Usage:
#   ./install.sh                              # install pre-built DLL + load gostream image
#   ./install.sh --build                      # rebuild plugin + deploy patched Jellyfin DLLs
#   ./install.sh --build --no-deploy-jellyfin-dlls
#                                               # rebuild only; print deploy commands
#   ./install.sh --jellyfin-data /custom/dir  # override data dir
#   ./install.sh --jellyfin-user myuser       # override service user
#   ./install.sh --no-restart                 # skip the systemctl prompt
#   ./install.sh --no-gostream                # skip gostream image load
#   ./install.sh --help

set -euo pipefail

# ---------------------------------------------------------------- helpers
red()    { printf '\033[0;31m%s\033[0m\n' "$*" >&2; }
yellow() { printf '\033[0;33m%s\033[0m\n' "$*"; }
green()  { printf '\033[0;32m%s\033[0m\n' "$*"; }
bold()   { printf '\033[1m%s\033[0m\n' "$*"; }

die() { red "ERROR: $*"; exit 1; }

file_sha256() {
  local path="$1"
  if [ -f "$path" ]; then
    sha256sum "$path" | awk '{print $1}'
  elif [ -n "${SUDO:-}" ] && $SUDO test -f "$path" 2>/dev/null; then
    $SUDO sha256sum "$path" | awk '{print $1}'
  else
    printf 'missing'
  fi
}

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
DO_BUILD=0
DEPLOY_JELLYFIN_DLLS=1
DO_GOSTREAM=1
GOSTREAM_IMAGE="docker.io/mrrobotogit/gostream:testing"
GOSTREAM_TARBALL="/tmp/gostream-testing.tar"

while [ $# -gt 0 ]; do
  case "$1" in
    --jellyfin-data)  JELLYFIN_DATA="$2"; shift 2 ;;
    --jellyfin-user)  JELLYFIN_USER="$2"; shift 2 ;;
    --no-restart)     NO_RESTART=1; shift ;;
    --build)          DO_BUILD=1; shift ;;
    --deploy-jellyfin-dlls) DEPLOY_JELLYFIN_DLLS=1; shift ;; # legacy no-op; deploy is default with --build
    --no-deploy-jellyfin-dlls) DEPLOY_JELLYFIN_DLLS=0; shift ;;
    --no-gostream)    DO_GOSTREAM=0; shift ;;
    -h|--help)        usage ;;
    *) die "Unknown option: $1 (try --help)" ;;
  esac
done

# Deploying patched Jellyfin DLLs only makes sense in the --build path,
# where the artifacts are freshly rebuilt and version-checked. Plain
# ./install.sh installs the already-built plugin DLL only.
if [ "$DO_BUILD" -ne 1 ]; then
  DEPLOY_JELLYFIN_DLLS=0
fi

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$REPO_ROOT"

# ---------------------------------------------------------------- detect
# Read canonical plugin version + GUID from build.yaml so the install
# layout matches what the manifest workflow publishes.
if [ ! -f build.yaml ]; then
  die "build.yaml missing in $REPO_ROOT - are you in the repo root?"
fi

PLUGIN_VERSION="$(awk -F'"' '/^version:/ {print $2; exit}' build.yaml)"
PLUGIN_GUID="$(awk -F'"' '/^guid:/ {print $2; exit}' build.yaml)"
PLUGIN_TARGET_ABI="$(awk -F'"' '/^targetAbi:/ {print $2; exit}' build.yaml)"
PLUGIN_NAME="Jellyfin.Plugin.PhantomLibrary"
PLUGIN_DISPLAY_NAME="Phantom Library"
PLUGIN_DIR_NAME="${PLUGIN_NAME}_${PLUGIN_VERSION}"
DLL_OUT="src/${PLUGIN_NAME}/bin/Release/net9.0/${PLUGIN_NAME}.dll"
EXPECTED_PHANTOM_SCHEMA="$(awk '/CurrentSchemaVersion/ { if (match($0, /[0-9]+/)) { print substr($0, RSTART, RLENGTH); exit } }' "src/${PLUGIN_NAME}/State/PhantomDb.cs" 2>/dev/null || true)"
[ -n "$EXPECTED_PHANTOM_SCHEMA" ] || EXPECTED_PHANTOM_SCHEMA="unknown"

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
PHANTOM_DB_PATH="$JELLYFIN_DATA/plugins/configurations/PhantomLibrary/phantom.db"

bold "Phantom Library installer"
echo "  Repo:               $REPO_ROOT"
echo "  Plugin version:     $PLUGIN_VERSION"
echo "  Jellyfin data dir:  $JELLYFIN_DATA"
echo "  Jellyfin user:grp:  $JELLYFIN_USER:$JELLYFIN_GROUP"
echo "  Plugin dest:        $PLUGINS_DIR"
echo "  Phantom stub root:  $PHANTOM_STUB_ROOT"
echo "  Phantom DB:         $PHANTOM_DB_PATH"
echo

if [ "$DO_BUILD" -eq 1 ] && [ "$DEPLOY_JELLYFIN_DLLS" -eq 1 ] && systemctl is-active --quiet jellyfin 2>/dev/null; then
  die "jellyfin.service is active; stop it before running ./install.sh --build, or pass --no-deploy-jellyfin-dlls to build without deploying patched Jellyfin DLLs"
fi

# Refuse to install over an incompatible phantom.db. The plugin will
# HARD-REFUSE schema mismatches at runtime; catching it here gives the
# operator a clear action before restarting Jellyfin.
if [ -f "$PHANTOM_DB_PATH" ] && command -v sqlite3 >/dev/null 2>&1; then
  PHANTOM_SCHEMA="$($SUDO sqlite3 "$PHANTOM_DB_PATH" 'PRAGMA user_version;' 2>/dev/null || echo unknown)"
  case "$PHANTOM_SCHEMA" in
    ''|unknown)
      yellow "Could not read phantom.db user_version at $PHANTOM_DB_PATH; runtime plugin will validate it."
      ;;
    0)
      :
      ;;
    "$EXPECTED_PHANTOM_SCHEMA")
      :
      ;;
    13)
      if [ "$EXPECTED_PHANTOM_SCHEMA" = "14" ]; then
        die "phantom.db schema is v13 but Phantom Library $PLUGIN_VERSION requires v14. Stop Jellyfin and run: sudo bash scripts/migrate-source-validation-v14.sh, then re-run ./install.sh --build"
      fi
      die "phantom.db schema is v$PHANTOM_SCHEMA but Phantom Library $PLUGIN_VERSION requires v$EXPECTED_PHANTOM_SCHEMA. Stop Jellyfin and run: sudo bash scripts/phantom-wipe.sh --commit"
      ;;
    *)
      die "phantom.db schema is v$PHANTOM_SCHEMA but Phantom Library $PLUGIN_VERSION requires v$EXPECTED_PHANTOM_SCHEMA. Stop Jellyfin and run: sudo bash scripts/phantom-wipe.sh --commit"
      ;;
  esac
fi

# ---------------------------------------------------------------- jellyfin version helpers
installed_jellyfin_version() {
  local version=""

  if command -v jellyfin >/dev/null 2>&1; then
    # Some distro wrappers print --version to stderr, not stdout.
    version="$(jellyfin --version 2>&1 | awk '{print $2; exit}' || true)"
    if [ -n "$version" ]; then
      printf '%s\n' "$version"
      return 0
    fi
  fi

  for common in /usr/lib/jellyfin/MediaBrowser.Common.dll /usr/share/jellyfin/bin/MediaBrowser.Common.dll /opt/jellyfin/MediaBrowser.Common.dll; do
    if [ -f "$common" ]; then
      version="$(grep -a -m1 -oE '10\.11\.[0-9]+\.0' "$common" 2>/dev/null || true)"
      if [ -n "$version" ]; then
        printf '%s\n' "$version"
        return 0
      fi
    fi
  done

  return 1
}

version_to_tag() {
  # 10.11.9.0 -> v10.11.9
  printf 'v%s\n' "${1%.0}"
}

# ---------------------------------------------------------------- build (opt-in)
if [ "$DO_BUILD" -eq 1 ]; then
  if ! command -v dotnet >/dev/null 2>&1; then
    die "'dotnet' not found in PATH. Install the .NET 9 SDK or drop --build."
  fi

  # Apply Jellyfin source-tree patches BEFORE building the plugin. The
  # plugin depends on patched APIs (IChannelItemRefreshManager). The
  # source tag MUST match the installed Jellyfin runtime exactly; copying
  # 10.11.11-built DLLs into a 10.11.9 runtime crashes at startup with
  # assembly-load failures (MediaBrowser.Common version mismatch).
  patches_dir="$REPO_ROOT/scripts/jellyfin-patches"
  if [ -d "$patches_dir" ] && ls "$patches_dir"/*.patch >/dev/null 2>&1; then
    if [ ! -d "$REPO_ROOT/jellyfin/.git" ]; then
      die "jellyfin/ source clone missing or not a git checkout; cannot apply patches"
    fi

    installed_version="$(installed_jellyfin_version || true)"
    if [ -z "$installed_version" ]; then
      die "Could not detect installed Jellyfin version. Refusing to build patched DLLs without exact runtime-version match."
    fi
    installed_tag="$(version_to_tag "$installed_version")"
    source_tag="$(git -C "$REPO_ROOT/jellyfin" describe --tags --match 'v10.11*' --abbrev=0 2>/dev/null || true)"
    if [ "$source_tag" != "$installed_tag" ]; then
      die "Installed Jellyfin is $installed_version ($installed_tag), but jellyfin/ nearest 10.11 tag is ${source_tag:-unknown}. Reset source before building: git -C jellyfin reset --hard $installed_tag && git -C jellyfin clean -fd"
    fi
    green "  Jellyfin runtime/source version match: $installed_version ($installed_tag)"

    bold "Applying Jellyfin patches from scripts/jellyfin-patches/..."
    (
      cd "$REPO_ROOT/jellyfin"
      for patch in "$patches_dir"/*.patch; do
        name=$(basename "$patch")
        # Idempotency: skip patches already applied (e.g. on rebuild
        # without an intervening 'git reset --hard' of jellyfin/).
        if git apply --check "$patch" 2>/dev/null; then
          git apply "$patch"
          echo "    applied: $name"
        elif git apply --check -R "$patch" 2>/dev/null; then
          echo "    already applied: $name"
        else
          red "ERROR: patch $name does not apply cleanly to jellyfin/ at $(git -C "$REPO_ROOT/jellyfin" rev-parse --short HEAD)."
          red "       Likely cause: jellyfin/ source has drifted from the patch base."
          red "       Resolution: rebase the patches. See scripts/jellyfin-patches/REBASE.md"
          exit 1
        fi
      done
    )
    echo
  fi

  bold "Building plugin (Release)... [--build]"
  echo
  yellow "  NOTE: this plugin depends on PATCHED Jellyfin assemblies"
  yellow "        (MediaBrowser.Controller + MediaBrowser.Model + Jellyfin.Api + Jellyfin.LiveTv)."
  yellow "        The patches add IChannelItemRefresh{,Manager} and item actions - used by the plugin"
  yellow "        at runtime. The plugin DLL alone is NOT sufficient;"
  yellow "        you must also deploy the patched Jellyfin DLLs to the"
  yellow "        Jellyfin install dir (default /usr/lib/jellyfin/)."
  yellow "        See docs/operator-deploy.md for the procedure - this"
  yellow "        script will print the exact commands at the end."
  echo
  read -r -a dotnet_build_args <<< "${PHANTOM_DOTNET_BUILD_ARGS:-}"
  if [ ${#dotnet_build_args[@]} -gt 0 ]; then
    yellow "  extra dotnet build args: ${dotnet_build_args[*]}"
  fi
  dotnet build -c Release "${dotnet_build_args[@]}"

  # Also build the patched Jellyfin assemblies the plugin links against
  # at runtime. Building Jellyfin.Server pulls in MediaBrowser.Controller,
  # MediaBrowser.Model, Jellyfin.Api, and Jellyfin.LiveTv transitively.
  # Output ends up in each project's bin/Release/net*/.
  bold "Building patched Jellyfin assemblies (Release)..."
  jf_server_csproj="$REPO_ROOT/jellyfin/Jellyfin.Server/Jellyfin.Server.csproj"
  if [ ! -f "$jf_server_csproj" ]; then
    die "jellyfin/Jellyfin.Server/Jellyfin.Server.csproj missing; expected a v10.11.9 clone at $REPO_ROOT/jellyfin (base commit $(cat "$REPO_ROOT/scripts/jellyfin-patches/REBASE.md" 2>/dev/null | grep -oE '[0-9a-f]{10,}' | head -1 || echo e83a7e62f2))"
  fi
  dotnet build -c Release "$jf_server_csproj" "${dotnet_build_args[@]}"

  jf_controller_dll="$REPO_ROOT/jellyfin/MediaBrowser.Controller/bin/Release/net9.0/MediaBrowser.Controller.dll"
  jf_model_dll="$REPO_ROOT/jellyfin/MediaBrowser.Model/bin/Release/net9.0/MediaBrowser.Model.dll"
  jf_api_dll="$REPO_ROOT/jellyfin/Jellyfin.Api/bin/Release/net9.0/Jellyfin.Api.dll"
  jf_livetv_dll="$REPO_ROOT/jellyfin/src/Jellyfin.LiveTv/bin/Release/net9.0/Jellyfin.LiveTv.dll"
  if ! grep -a -q "Version=$installed_version" "$jf_controller_dll"; then
    die "Patched MediaBrowser.Controller.dll does not reference installed Jellyfin assembly version $installed_version. Refusing to deploy."
  fi
  if grep -a -q 'Version=10\.11\.11\.0' "$jf_controller_dll" && [ "$installed_version" != "10.11.11.0" ]; then
    die "Patched MediaBrowser.Controller.dll contains 10.11.11 refs but installed Jellyfin is $installed_version. Refusing to deploy."
  fi
  green "  patched Jellyfin DLLs match runtime assembly version $installed_version"
  echo
else
  yellow "Skipping build (pass --build to rebuild). Using existing artefacts."
fi

if [ ! -f "$DLL_OUT" ]; then
  die "Built DLL not found at $DLL_OUT. Re-run with --build, or build manually first."
fi

if [ "$DO_BUILD" -eq 1 ] && [ "$DEPLOY_JELLYFIN_DLLS" -eq 1 ]; then
  runtime_dir=""
  for cand in /usr/lib/jellyfin /usr/share/jellyfin/bin /opt/jellyfin; do
    if [ -f "$cand/MediaBrowser.Controller.dll" ] && [ -f "$cand/MediaBrowser.Model.dll" ] && [ -f "$cand/Jellyfin.Api.dll" ] && [ -f "$cand/Jellyfin.LiveTv.dll" ]; then
      runtime_dir="$cand"
      break
    fi
  done
  if [ -z "$runtime_dir" ]; then
    die "Could not detect Jellyfin runtime install dir for patched DLL deploy; pass --no-deploy-jellyfin-dlls to build without installing plugin/runtime DLLs"
  fi
fi

if [ "$DO_BUILD" -eq 0 ]; then
  runtime_dir=""
  for cand in /usr/lib/jellyfin /usr/share/jellyfin/bin /opt/jellyfin; do
    if [ -f "$cand/MediaBrowser.Controller.dll" ] && [ -f "$cand/MediaBrowser.Model.dll" ] && [ -f "$cand/Jellyfin.Api.dll" ] && [ -f "$cand/Jellyfin.LiveTv.dll" ]; then
      runtime_dir="$cand"
      break
    fi
  done
  if [ -z "$runtime_dir" ]; then
    die "Could not verify patched Jellyfin runtime DLLs; run ./install.sh --build so patched assemblies are built/deployed"
  fi
  grep -a -q IChannelItemRefreshManager "$runtime_dir/MediaBrowser.Controller.dll" \
    || die "Runtime MediaBrowser.Controller.dll lacks Phantom patches; run ./install.sh --build"
  grep -a -q ItemActionInfo "$runtime_dir/MediaBrowser.Model.dll" \
    || die "Runtime MediaBrowser.Model.dll lacks Phantom item-action patch; run ./install.sh --build"
  grep -a -q ItemActionsController "$runtime_dir/Jellyfin.Api.dll" \
    || die "Runtime Jellyfin.Api.dll lacks Phantom item-action patch; run ./install.sh --build"
  grep -a -q RefreshChannelItemAsync "$runtime_dir/Jellyfin.LiveTv.dll" \
    || die "Runtime Jellyfin.LiveTv.dll lacks Phantom channel-refresh patch; run ./install.sh --build"
fi

# ---------------------------------------------------------------- install dll
bold "Installing plugin DLL..."

# Jellyfin scans versioned plugin directories. Leaving an older
# Jellyfin.Plugin.PhantomLibrary_<version>/ directory behind can keep the
# old plugin active or make the dashboard show the wrong version. Move all
# stale Phantom Library plugin dirs out of plugins/ before installing this
# version.
PLUGIN_BACKUP_DIR="$JELLYFIN_DATA/phantom-plugin-dir-backups/$(date -u +%Y%m%dT%H%M%SZ)"
shopt -s nullglob
for old_dir in "$JELLYFIN_DATA"/plugins/${PLUGIN_NAME}_*; do
  if [ "$(basename "$old_dir")" != "$PLUGIN_DIR_NAME" ]; then
    $SUDO mkdir -p "$PLUGIN_BACKUP_DIR"
    yellow "  moving stale plugin dir out of plugins/: $old_dir"
    $SUDO mv "$old_dir" "$PLUGIN_BACKUP_DIR/"
  fi
done
shopt -u nullglob

$SUDO mkdir -p "$PLUGINS_DIR"
$SUDO install -m 644 "$DLL_OUT" "$PLUGINS_DIR/${PLUGIN_NAME}.dll"

META_TMP="$(mktemp)"
cat > "$META_TMP" <<EOF
{
  "category": "Metadata",
  "changelog": "v0.3.0.0: IChannel-based Phantom Library architecture; requires patched Jellyfin and wipe.",
  "description": "Makes the entire TMDB catalogue appear inside Jellyfin. Titles materialise on demand via gostream's FUSE-backed virtual MKV files.",
  "guid": "$PLUGIN_GUID",
  "name": "$PLUGIN_DISPLAY_NAME",
  "overview": "Phantom Library: TMDB catalogue as Jellyfin channels, materialised on demand.",
  "owner": "spencerharmon",
  "targetAbi": "$PLUGIN_TARGET_ABI",
  "timestamp": "0001-01-01T00:00:00.0000000Z",
  "version": "$PLUGIN_VERSION",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": []
}
EOF
$SUDO install -m 644 "$META_TMP" "$PLUGINS_DIR/meta.json"
rm -f "$META_TMP"

$SUDO chown -R "$JELLYFIN_USER:$JELLYFIN_GROUP" "$PLUGINS_DIR"

# Verify md5 to catch the "I copied the stale build" footgun.
SRC_MD5="$(md5sum "$DLL_OUT" | awk '{print $1}')"
DST_MD5="$($SUDO md5sum "$PLUGINS_DIR/${PLUGIN_NAME}.dll" | awk '{print $1}')"
if [ "$SRC_MD5" != "$DST_MD5" ]; then
  die "md5 mismatch after install: src=$SRC_MD5 dst=$DST_MD5. Aborting."
fi
green "  ${PLUGIN_NAME}.dll installed (md5 $SRC_MD5)"

SRC_SHA256="$(file_sha256 "$DLL_OUT")"
DST_SHA256="$(file_sha256 "$PLUGINS_DIR/${PLUGIN_NAME}.dll")"
if [ "$SRC_SHA256" != "$DST_SHA256" ]; then
  die "sha256 mismatch after install: src=$SRC_SHA256 dst=$DST_SHA256. Operator would not be testing the intended plugin."
fi

echo
bold "Post-install verification:"
echo "  repo:                    $REPO_ROOT"
echo "  git commit:              $(git rev-parse HEAD 2>/dev/null || echo unknown)"
echo "  git dirty files:"
git status --short 2>/dev/null | sed 's/^/    /' || true
echo "  plugin schema source:    $EXPECTED_PHANTOM_SCHEMA"
if [ -f "$PHANTOM_DB_PATH" ] && command -v sqlite3 >/dev/null 2>&1; then
  VERIFY_PHANTOM_SCHEMA="$($SUDO sqlite3 "$PHANTOM_DB_PATH" 'PRAGMA user_version;' 2>/dev/null || echo unknown)"
else
  VERIFY_PHANTOM_SCHEMA="missing"
fi
echo "  phantom.db schema:       $VERIFY_PHANTOM_SCHEMA"
echo "  plugin built sha256:     $SRC_SHA256"
echo "  plugin deployed sha256:  $DST_SHA256"
VERIFY_JF_INSTALL_DIR=""
for cand in /usr/lib/jellyfin /usr/share/jellyfin/bin /opt/jellyfin; do
  if [ -f "$cand/MediaBrowser.Controller.dll" ] && [ -f "$cand/MediaBrowser.Model.dll" ] && [ -f "$cand/Jellyfin.Api.dll" ] && [ -f "$cand/Jellyfin.LiveTv.dll" ]; then
    VERIFY_JF_INSTALL_DIR="$cand"
    break
  fi
done
if [ -n "$VERIFY_JF_INSTALL_DIR" ]; then
  echo "  Jellyfin install dir:    $VERIFY_JF_INSTALL_DIR"
  echo "  Controller sha256:       $(file_sha256 "$VERIFY_JF_INSTALL_DIR/MediaBrowser.Controller.dll")"
  echo "  Model sha256:            $(file_sha256 "$VERIFY_JF_INSTALL_DIR/MediaBrowser.Model.dll")"
  echo "  Api sha256:              $(file_sha256 "$VERIFY_JF_INSTALL_DIR/Jellyfin.Api.dll")"
  echo "  LiveTv sha256:           $(file_sha256 "$VERIFY_JF_INSTALL_DIR/Jellyfin.LiveTv.dll")"
else
  echo "  Jellyfin install dir:    not detected"
fi
if [ -d "$REPO_ROOT/gostream/.git" ]; then
  echo "  gostream commit:         $(git -C "$REPO_ROOT/gostream" rev-parse HEAD 2>/dev/null || echo unknown)"
  echo "  gostream dirty files:"
  git -C "$REPO_ROOT/gostream" status --short 2>/dev/null | sed 's/^/    /' || true
fi
if [ "$EXPECTED_PHANTOM_SCHEMA" != "unknown" ] && [ "$VERIFY_PHANTOM_SCHEMA" != "missing" ] && [ "$VERIFY_PHANTOM_SCHEMA" != "0" ] && [ "$VERIFY_PHANTOM_SCHEMA" != "$EXPECTED_PHANTOM_SCHEMA" ]; then
  die "phantom.db schema is v$VERIFY_PHANTOM_SCHEMA but this build expects v$EXPECTED_PHANTOM_SCHEMA. Stop Jellyfin and run: sudo bash scripts/phantom-wipe.sh --commit"
fi

# ---------------------------------------------------------------- patched jellyfin assemblies (operator-deploy notice)
# The plugin references IChannelItemRefreshManager (added by the
# scripts/jellyfin-patches/ patch series). The operator's runtime
# Jellyfin install (e.g. /usr/lib/jellyfin/) is the unpatched distro
# package. Plugin load will fail with TypeLoadException unless the
# matching patched DLLs are deployed alongside.
#
# install.sh does NOT auto-deploy these. Replacing system files is
# destructive and a package-manager upgrade would silently clobber
# them; the operator owns that decision. We print the exact commands
# the operator should run, sourced from the build outputs we just
# produced (when --build was passed).
if [ "$DO_BUILD" -eq 1 ]; then
  jf_controller_dll="$REPO_ROOT/jellyfin/MediaBrowser.Controller/bin/Release/net9.0/MediaBrowser.Controller.dll"
  jf_model_dll="$REPO_ROOT/jellyfin/MediaBrowser.Model/bin/Release/net9.0/MediaBrowser.Model.dll"
  jf_api_dll="$REPO_ROOT/jellyfin/Jellyfin.Api/bin/Release/net9.0/Jellyfin.Api.dll"
  jf_livetv_dll="$REPO_ROOT/jellyfin/src/Jellyfin.LiveTv/bin/Release/net9.0/Jellyfin.LiveTv.dll"
  # Detect the operator's Jellyfin install dir. /usr/lib/jellyfin/ on
  # Arch + Debian distro packages. Skip the section quietly if neither
  # patched DLL exists yet (build failed earlier).
  if [ ! -f "$jf_controller_dll" ] || [ ! -f "$jf_model_dll" ] || [ ! -f "$jf_api_dll" ] || [ ! -f "$jf_livetv_dll" ]; then
    yellow "Patched Jellyfin DLLs not found under jellyfin/.../bin/Release/net9.0/."
    yellow "  expected: $jf_controller_dll"
    yellow "            $jf_model_dll"
    yellow "            $jf_api_dll"
    yellow "            $jf_livetv_dll"
    yellow "  Did 'dotnet build -c Release jellyfin/Jellyfin.Server/Jellyfin.Server.csproj' succeed?"
  else
    jf_install_dir=""
    for cand in /usr/lib/jellyfin /usr/share/jellyfin/bin /opt/jellyfin; do
      if [ -f "$cand/MediaBrowser.Controller.dll" ] && [ -f "$cand/MediaBrowser.Model.dll" ] && [ -f "$cand/Jellyfin.Api.dll" ] && [ -f "$cand/Jellyfin.LiveTv.dll" ]; then
        jf_install_dir="$cand"
        break
      fi
    done
    echo
    bold "Patched Jellyfin assemblies ready for operator deploy:"
    echo "  built artefacts:"
    echo "    $jf_controller_dll"
    echo "    $jf_model_dll"
    echo "    $jf_api_dll"
    echo "    $jf_livetv_dll"
    if [ -n "$jf_install_dir" ]; then
      echo
      echo "  detected runtime Jellyfin install dir: $jf_install_dir"
      yellow "  the plugin DLL just installed REFERENCES types added by the patches;"
      yellow "  the plugin will fail to load against the unpatched DLLs currently"
      yellow "  at $jf_install_dir/{MediaBrowser.Controller.dll,MediaBrowser.Model.dll,Jellyfin.Api.dll,Jellyfin.LiveTv.dll}."
      echo
      bold "  Operator deploy procedure (run BEFORE restarting jellyfin):"
      echo "    sudo systemctl stop jellyfin"
      echo "    test -f $jf_install_dir/MediaBrowser.Controller.dll.pre-phantom-bak || sudo cp -p $jf_install_dir/MediaBrowser.Controller.dll \\"
      echo "            $jf_install_dir/MediaBrowser.Controller.dll.pre-phantom-bak"
      echo "    test -f $jf_install_dir/MediaBrowser.Model.dll.pre-phantom-bak || sudo cp -p $jf_install_dir/MediaBrowser.Model.dll \\"
      echo "            $jf_install_dir/MediaBrowser.Model.dll.pre-phantom-bak"
      echo "    test -f $jf_install_dir/Jellyfin.Api.dll.pre-phantom-bak || sudo cp -p $jf_install_dir/Jellyfin.Api.dll \\"
      echo "            $jf_install_dir/Jellyfin.Api.dll.pre-phantom-bak"
      echo "    test -f $jf_install_dir/Jellyfin.LiveTv.dll.pre-phantom-bak || sudo cp -p $jf_install_dir/Jellyfin.LiveTv.dll \\"
      echo "            $jf_install_dir/Jellyfin.LiveTv.dll.pre-phantom-bak"
      echo "    sudo install -m 644 $jf_controller_dll $jf_install_dir/"
      echo "    sudo install -m 644 $jf_model_dll $jf_install_dir/"
      echo "    sudo install -m 644 $jf_api_dll $jf_install_dir/"
      echo "    sudo install -m 644 $jf_livetv_dll $jf_install_dir/"
      echo "    grep -a -q 'Version=$installed_version' $jf_install_dir/MediaBrowser.Controller.dll && echo OK_VERSION_MATCH"
      echo "    grep -a -q IChannelItemRefreshManager $jf_install_dir/MediaBrowser.Controller.dll && echo OK_CONTROLLER_PATCHED"
      echo "    grep -a -q ItemActionInfo $jf_install_dir/MediaBrowser.Model.dll && echo OK_MODEL_PATCHED"
      echo "    grep -a -q ItemActionsController $jf_install_dir/Jellyfin.Api.dll && echo OK_API_PATCHED"
      echo "    grep -a -q RefreshChannelItemAsync $jf_install_dir/Jellyfin.LiveTv.dll && echo OK_LIVETV_PATCHED"
      echo "    sudo systemctl start jellyfin"
      echo

      if [ "$DEPLOY_JELLYFIN_DLLS" -eq 1 ]; then
        bold "  Deploying patched Jellyfin DLLs now (--deploy-jellyfin-dlls)..."
        if systemctl is-active --quiet jellyfin 2>/dev/null; then
          die "jellyfin.service is active; stop it before deploying patched DLLs"
        fi
        if ! $SUDO test -f "$jf_install_dir/MediaBrowser.Controller.dll.pre-phantom-bak"; then
          $SUDO cp -p "$jf_install_dir/MediaBrowser.Controller.dll" "$jf_install_dir/MediaBrowser.Controller.dll.pre-phantom-bak"
        fi
        if ! $SUDO test -f "$jf_install_dir/MediaBrowser.Model.dll.pre-phantom-bak"; then
          $SUDO cp -p "$jf_install_dir/MediaBrowser.Model.dll" "$jf_install_dir/MediaBrowser.Model.dll.pre-phantom-bak"
        fi
        if ! $SUDO test -f "$jf_install_dir/Jellyfin.Api.dll.pre-phantom-bak"; then
          $SUDO cp -p "$jf_install_dir/Jellyfin.Api.dll" "$jf_install_dir/Jellyfin.Api.dll.pre-phantom-bak"
        fi
        if ! $SUDO test -f "$jf_install_dir/Jellyfin.LiveTv.dll.pre-phantom-bak"; then
          $SUDO cp -p "$jf_install_dir/Jellyfin.LiveTv.dll" "$jf_install_dir/Jellyfin.LiveTv.dll.pre-phantom-bak"
        fi
        $SUDO install -m 644 "$jf_controller_dll" "$jf_install_dir/"
        $SUDO install -m 644 "$jf_model_dll" "$jf_install_dir/"
        $SUDO install -m 644 "$jf_api_dll" "$jf_install_dir/"
        $SUDO install -m 644 "$jf_livetv_dll" "$jf_install_dir/"
        grep -a -q "Version=$installed_version" "$jf_install_dir/MediaBrowser.Controller.dll" \
          || die "deployed MediaBrowser.Controller.dll does not reference Version=$installed_version"
        grep -a -q IChannelItemRefreshManager "$jf_install_dir/MediaBrowser.Controller.dll" \
          || die "deployed MediaBrowser.Controller.dll lacks IChannelItemRefreshManager"
        grep -a -q ItemActionInfo "$jf_install_dir/MediaBrowser.Model.dll" \
          || die "deployed MediaBrowser.Model.dll lacks ItemActionInfo"
        grep -a -q ItemActionsController "$jf_install_dir/Jellyfin.Api.dll" \
          || die "deployed Jellyfin.Api.dll lacks ItemActionsController"
        grep -a -q RefreshChannelItemAsync "$jf_install_dir/Jellyfin.LiveTv.dll" \
          || die "deployed Jellyfin.LiveTv.dll lacks RefreshChannelItemAsync"
        green "  patched Jellyfin DLLs deployed + verified in $jf_install_dir"
        echo "  deployed Controller sha256: $(file_sha256 "$jf_install_dir/MediaBrowser.Controller.dll")"
        echo "  deployed Model sha256:      $(file_sha256 "$jf_install_dir/MediaBrowser.Model.dll")"
        echo "  deployed Api sha256:        $(file_sha256 "$jf_install_dir/Jellyfin.Api.dll")"
        echo "  deployed LiveTv sha256:     $(file_sha256 "$jf_install_dir/Jellyfin.LiveTv.dll")"
        echo
      fi

      yellow "  NOTE: a 'pacman -Syu' / 'apt upgrade' / etc. of the jellyfin-server"
      yellow "        package will silently overwrite these DLLs and the plugin"
      yellow "        will fail to load on the next jellyfin restart. To detect:"
      echo   "          md5sum $jf_install_dir/MediaBrowser.Controller.dll \\"
      echo   "                 $jf_install_dir/MediaBrowser.Model.dll \\"
      echo   "                 $jf_install_dir/Jellyfin.Api.dll \\"
      echo   "                 $jf_install_dir/Jellyfin.LiveTv.dll"
      yellow "        Compare against the md5 of the .pre-phantom-bak files. If"
      yellow "        the live DLLs match the .pre-phantom-bak, the patch was clobbered;"
      yellow "        re-run ./install.sh --build then redo this deploy block."
      yellow "        Backup commands use test -f guards so original .pre-phantom-bak"
      yellow "        files are not overwritten by a bad patched build."
    else
      yellow "  Could not auto-detect a Jellyfin install dir with the unpatched DLLs."
      yellow "  Deploy these four DLLs to your Jellyfin install dir (the one containing"
      yellow "  jellyfin.dll), back up the originals first. See docs/operator-deploy.md."
    fi
    echo
  fi
fi

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

# ---------------------------------------------------------------- gostream image
if [ "$DO_GOSTREAM" -eq 1 ]; then
  echo
  bold "Preparing gostream image..."
  GOSTREAM_LOAD=1
  if ! command -v podman >/dev/null 2>&1; then
    if [ "$DO_BUILD" -eq 1 ]; then
      die "podman not in PATH; cannot build gostream image. Install podman or pass --no-gostream."
    fi
    yellow "  podman not in PATH - skipping. Install podman or pass --no-gostream."
  else
    if [ "$DO_BUILD" -eq 1 ]; then
      if [ ! -d "$REPO_ROOT/gostream/.git" ]; then
        die "gostream/ source checkout missing; cannot build $GOSTREAM_IMAGE. Pass --no-gostream to skip."
      fi
      if [ ! -f "$REPO_ROOT/gostream/docker/Dockerfile" ]; then
        die "gostream/docker/Dockerfile missing; cannot build $GOSTREAM_IMAGE."
      fi

      bold "  Building gostream image from $REPO_ROOT/gostream..."
      (
        cd "$REPO_ROOT/gostream"
        podman build -f docker/Dockerfile -t "$GOSTREAM_IMAGE" .
        # podman saves docker-archive output, which cannot be modified in
        # place. Remove any stale tarball first so repeated installs do not
        # fail with: docker-archive doesn't support modifying existing images.
        rm -f "$GOSTREAM_TARBALL"
        podman save -o "$GOSTREAM_TARBALL" "$GOSTREAM_IMAGE"
      )
      green "  built $GOSTREAM_IMAGE and wrote $GOSTREAM_TARBALL"
      echo "  gostream commit:       $(git -C "$REPO_ROOT/gostream" rev-parse HEAD 2>/dev/null || echo unknown)"
      echo "  gostream tar sha256:   $(file_sha256 "$GOSTREAM_TARBALL")"
    elif [ ! -f "$GOSTREAM_TARBALL" ]; then
      yellow "  $GOSTREAM_TARBALL not found."
      yellow "  Re-run with --build to build gostream automatically, or create it with:"
      yellow "    cd gostream && podman build -f docker/Dockerfile -t $GOSTREAM_IMAGE ."
      yellow "    podman save -o $GOSTREAM_TARBALL $GOSTREAM_IMAGE"
      yellow "  Skipping image load."
      GOSTREAM_LOAD=0
    fi

    if [ "$GOSTREAM_LOAD" -eq 1 ]; then
    bold "Loading gostream image into root podman storage..."
    # Capture the pre-load image id (if any) so we can detect whether
    # the load actually replaced anything new. Empty string if absent.
    OLD_IMG_ID="$($SUDO podman image inspect --format '{{.Id}}' "$GOSTREAM_IMAGE" 2>/dev/null || true)"

    if [ -n "$OLD_IMG_ID" ]; then
      yellow "  $GOSTREAM_IMAGE already present (id=${OLD_IMG_ID:0:12}); reloading."
      $SUDO podman rmi -f "$GOSTREAM_IMAGE" >/dev/null 2>&1 || true
    fi
    $SUDO podman load -i "$GOSTREAM_TARBALL"
    NEW_IMG_ID="$($SUDO podman image inspect --format '{{.Id}}' "$GOSTREAM_IMAGE" 2>/dev/null || true)"

    if [ -z "$NEW_IMG_ID" ]; then
      red "  podman load succeeded but $GOSTREAM_IMAGE not found in root storage; check output above."
    else
      green "  $GOSTREAM_IMAGE loaded into root podman storage (id=${NEW_IMG_ID:0:12})."

      # Restart gostream.service only if the image id actually
      # changed (or there was no prior image). No point bouncing
      # the container if the tarball matched what was already loaded.
      if [ "$OLD_IMG_ID" != "$NEW_IMG_ID" ]; then
        if command -v systemctl >/dev/null 2>&1 \
           && $SUDO systemctl list-unit-files gostream.service >/dev/null 2>&1 \
           && $SUDO systemctl list-unit-files gostream.service 2>/dev/null | grep -q gostream.service; then
          bold "  Restarting gostream.service (image changed)..."
          $SUDO systemctl restart gostream.service
          sleep 2
          if $SUDO systemctl is-active --quiet gostream.service; then
            green "    gostream.service active."
          else
            red "    gostream.service failed to become active. Check: journalctl -u gostream -n 50"
          fi
        else
          yellow "  gostream.service not found in systemd; restart your container manually."
        fi
      else
        yellow "  Image id unchanged; gostream.service not restarted."
      fi
    fi
    fi
  fi
else
  yellow "Skipping gostream image load (--no-gostream)."
fi

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
read -r -p "Restart $JELLYFIN_UNIT now? [Y/n] " ans
case "${ans:-y}" in
  n|N|no|NO)
    yellow "Skipped restart. Run: sudo systemctl restart $JELLYFIN_UNIT"
    ;;
  *)
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
esac

# ---------------------------------------------------------------
# PhantomKebab: inject the item-detail Materialise-button shim into
# jellyfin-web's index.html. The 10.11.x BrandingOptions only exposes
# CustomCss (no CustomJs field), and the SPA wraps CustomCss in a
# <style> tag, so a CSS-injected <script> would not execute. We patch
# /usr/share/jellyfin/web/index.html directly. The patch is idempotent
# (skips re-add if marker already present) and survives plugin
# reinstalls. NOTE: a jellyfin-web package upgrade will overwrite
# index.html and remove the patch - re-run install.sh after such an
# upgrade to reapply.
# ---------------------------------------------------------------
INDEX_CANDIDATES=(
  "/usr/share/jellyfin/web/index.html"
  "/usr/share/jellyfin-web/index.html"
  "/var/lib/jellyfin/jellyfin-web/index.html"
)
INDEX_PATH=""
for cand in "${INDEX_CANDIDATES[@]}"; do
  if [ -f "$cand" ]; then
    INDEX_PATH="$cand"
    break
  fi
done

if [ -z "$INDEX_PATH" ]; then
  yellow "jellyfin-web index.html not found in standard locations; skipping kebab shim install."
  yellow "To install manually, add this line before </body> in your jellyfin-web index.html:"
  yellow '  <script src="/Plugins/PhantomLibrary/kebab.js" defer></script>'
elif $SUDO grep -qE 'name=PhantomKebab|Plugins/PhantomLibrary/kebab\.js' "$INDEX_PATH" 2>/dev/null; then
  # If an older snippet (?name=PhantomKebab) is present, replace it.
  if $SUDO grep -q 'name=PhantomKebab' "$INDEX_PATH" 2>/dev/null; then
    bold "Upgrading PhantomKebab shim URL to controller route"
    $SUDO sed -i 's|<!--phantom-library-kebab--><script src="/web/ConfigurationPage?name=PhantomKebab" defer></script>|<!--phantom-library-kebab--><script src="/Plugins/PhantomLibrary/kebab.js" defer></script>|' "$INDEX_PATH"
  fi
  green "PhantomKebab shim present in $INDEX_PATH"
else
  bold "Injecting PhantomKebab shim into $INDEX_PATH"
  # Backup once. Subsequent runs keep the original backup.
  if [ ! -f "${INDEX_PATH}.phantom-orig" ]; then
    $SUDO cp "$INDEX_PATH" "${INDEX_PATH}.phantom-orig"
  fi
  # Insert just before </body>. Use a sentinel comment so we can grep
  # for it later and avoid double-injection.
  SNIPPET='<!--phantom-library-kebab--><script src="/Plugins/PhantomLibrary/kebab.js" defer></script>'
  $SUDO sed -i "s|</body>|${SNIPPET}</body>|" "$INDEX_PATH"
  if $SUDO grep -q 'name=PhantomKebab' "$INDEX_PATH"; then
    green "  injected. (Backup at ${INDEX_PATH}.phantom-orig)"
  else
    red "  injection failed; index.html unchanged. Inspect manually."
  fi
fi

# PhantomBadges: separate shim that decorates list/card DOM nodes with
# a Phantom/Materialised/Unavailable badge. Injected as its own
# <script> tag next to the kebab snippet, with the same idempotency
# semantics. Sentinel comment <!--phantom-library-badges--> keeps the
# patch easy to grep for and easy to remove on uninstall.
if [ -z "$INDEX_PATH" ]; then
  yellow "jellyfin-web index.html not found; skipping badges shim install."
  yellow "To install manually, add this line before </body> in your jellyfin-web index.html:"
  yellow '  <script src="/Plugins/PhantomLibrary/badges.js" defer></script>'
elif $SUDO grep -q 'Plugins/PhantomLibrary/badges\.js' "$INDEX_PATH" 2>/dev/null; then
  green "PhantomBadges shim present in $INDEX_PATH"
else
  bold "Injecting PhantomBadges shim into $INDEX_PATH"
  if [ ! -f "${INDEX_PATH}.phantom-orig" ]; then
    $SUDO cp "$INDEX_PATH" "${INDEX_PATH}.phantom-orig"
  fi
  BADGES_SNIPPET='<!--phantom-library-badges--><script src="/Plugins/PhantomLibrary/badges.js" defer></script>'
  $SUDO sed -i "s|</body>|${BADGES_SNIPPET}</body>|" "$INDEX_PATH"
  if $SUDO grep -q 'phantom-library-badges' "$INDEX_PATH"; then
    green "  injected. (Backup at ${INDEX_PATH}.phantom-orig)"
  else
    red "  injection failed; index.html unchanged. Inspect manually."
  fi
fi

green "Done."
