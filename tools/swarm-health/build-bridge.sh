#!/bin/sh
set -eu
# Invoke from the repository root; OUTPUT is required so builds never pollute source.
: "${OUTPUT:?set OUTPUT to the destination binary}"
c++ -std=c++17 -O2 -Wall -Wextra tools/swarm-health/dht_bridge.cpp \
    $(pkg-config --cflags --libs libtorrent-rasterbar) -o "$OUTPUT"
