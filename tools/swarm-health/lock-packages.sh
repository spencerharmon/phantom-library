#!/bin/sh
# Maintainer-only lock regeneration; review resulting artifact hashes before commit.
# Run inside the exact Debian base declared in Dockerfile, as root.
set -eu
case "$1" in
  build) packages='g++ pkg-config libtorrent-rasterbar-dev nlohmann-json3-dev' ;;
  runtime) packages='python3 libtorrent-rasterbar2.0t64' ;;
  *) exit 2 ;;
esac
rm -f /etc/apt/sources.list.d/*
printf '%s\n' 'deb [check-valid-until=no] http://snapshot.debian.org/archive/debian/20261124T000000Z trixie main' > /etc/apt/sources.list
apt-get update >&2
apt-get -y --no-install-recommends --download-only install $packages >&2
# Debian's docker-clean hook otherwise deletes the downloads on install.
cd /var/cache/apt/archives
sha256sum *.deb | LC_ALL=C sort -k2
