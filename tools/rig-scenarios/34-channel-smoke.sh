#!/usr/bin/env bash
set -euo pipefail
cd /home/spencer/git-repos/spencerharmon/phantom-library
BASE=http://localhost:18096
TOK=testtoken00000000000000000000000
LOG=/tmp/phantom-channel-integration2.log
DATA=/tmp/jf-test/data
CONF="$DATA/plugins/configurations/Jellyfin.Plugin.PhantomLibrary.xml"
PLUGIN_DIR="$DATA/plugins/Jellyfin.Plugin.PhantomLibrary_0.3.0.0"
PHDB="$DATA/plugins/configurations/PhantomLibrary/phantom.db"
EMPTY=/tmp/jf-test/empty-gostream

setup_plugin() {
  mkdir -p "$PLUGIN_DIR" "$EMPTY/movies" "$EMPTY/tv"
  rm -rf "$DATA/plugins/Jellyfin.Plugin.PhantomLibrary_0.1.0.0" "$DATA/plugins/Jellyfin.Plugin.PhantomLibrary_0.2.0.0"
  cp src/Jellyfin.Plugin.PhantomLibrary/bin/Release/net9.0/Jellyfin.Plugin.PhantomLibrary.dll "$PLUGIN_DIR/"
  src_md5=$(md5sum src/Jellyfin.Plugin.PhantomLibrary/bin/Release/net9.0/Jellyfin.Plugin.PhantomLibrary.dll | awk '{print $1}')
  dst_md5=$(md5sum "$PLUGIN_DIR/Jellyfin.Plugin.PhantomLibrary.dll" | awk '{print $1}')
  echo "PLUGIN_MD5 src=$src_md5 dst=$dst_md5"
  [ "$src_md5" = "$dst_md5" ] || { echo "BAD_PLUGIN_COPY"; exit 1; }
  cat > "$PLUGIN_DIR/meta.json" <<'META'
{"category":"Metadata","changelog":"integration","description":"integration","guid":"9e7a1f4c-2b5d-4e8f-9a3b-7c1d2e5f6a8b","name":"Phantom Library","overview":"integration","owner":"spencerharmon","targetAbi":"10.11.0.0","timestamp":"0001-01-01T00:00:00.0000000Z","version":"0.3.0.0","status":"Active","autoUpdate":false,"assemblies":[]}
META
  python3 - <<PY
from pathlib import Path
p=Path('$CONF')
text=p.read_text() if p.exists() else '<?xml version="1.0" encoding="utf-8"?>\n<PluginConfiguration></PluginConfiguration>'
for tag,val in [('GostreamMoviesRoot','$EMPTY/movies'),('GostreamShowsRoot','$EMPTY/tv')]:
    import re
    if f'<{tag}>' in text:
        text=re.sub(f'<{tag}>.*?</{tag}>', f'<{tag}>{val}</{tag}>', text)
    else:
        text=text.replace('</PluginConfiguration>', f'  <{tag}>{val}</{tag}>\n</PluginConfiguration>')
p.write_text(text)
PY
}

start_jf() {
  : > "$LOG"
  /tmp/jf-test/start.sh >"$LOG" 2>&1 &
  JF=$!
  for i in $(seq 1 70); do
    if ! kill -0 "$JF" 2>/dev/null; then echo "PROCESS_EXITED"; tail -160 "$LOG"; exit 1; fi
    code=$(curl -s --max-time 2 -H "X-Emby-Token: $TOK" -o /tmp/sysinfo.json -w "%{http_code}" "$BASE/System/Info" 2>/dev/null || true)
    if [ "$code" = "200" ]; then echo "UP_AT=${i}s"; return 0; fi
    sleep 1
  done
  echo "NO_START"; tail -160 "$LOG"; exit 1
}

stop_jf() {
  kill -9 "$JF" 2>/dev/null || true
  pkill -u "$USER" -9 -f "dotnet.*jellyfin.dll.*jf-test" 2>/dev/null || true
  sleep 1
}
trap 'stop_jf' EXIT

pkill -u "$USER" -9 -f "dotnet.*jellyfin.dll.*jf-test" 2>/dev/null || true
rm -rf "$DATA/plugins/configurations/PhantomLibrary" "$EMPTY"
setup_plugin

# First startup: create schema through one channel browse.
start_jf
curl -s -H "X-Emby-Token: $TOK" "$BASE/Channels" > /tmp/channels.json
MOVIES_ID=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/channels.json'))
items=j.get('Items', j if isinstance(j,list) else [])
print(next(x['Id'] for x in items if x.get('Name')=='Phantom Movies'))
PY
)
curl -s -H "X-Emby-Token: $TOK" "$BASE/Channels/$MOVIES_ID/Items" > /tmp/empty.json
for i in $(seq 1 20); do
  schema=$(sqlite3 "$PHDB" 'PRAGMA user_version;' 2>/dev/null || echo 0)
  [ "$schema" = "9" ] && break
  sleep 1
done
[ "$schema" = "9" ] || { echo BAD_SCHEMA=$schema; exit 1; }
stop_jf

# Seed before second startup so ChannelStateProvider hydrates with bumped version.
now=$(date +%s)
sqlite3 "$PHDB" <<SQL
INSERT OR REPLACE INTO discovery_cache (tmdb_id,type,discovered_at,last_refreshed) VALUES (42,'movie',$now,$now);
INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,overview,poster_url,backdrop_url,genres_json,official_rating,community_rating,original_title,fetched_at)
VALUES (42,'movie','Integration Movie',2026,'overview','https://image.tmdb.org/t/p/w500/poster.jpg',NULL,'["Drama"]','PG-13',7.5,'Integration Movie Original',$now);
INSERT OR REPLACE INTO plugin_meta (key,value) VALUES ('channel_dataversion_movies','$now');
SQL

start_jf
curl -s -H "X-Emby-Token: $TOK" "$BASE/Channels" > /tmp/channels.json
python3 - <<'PY'
import json
j=json.load(open('/tmp/channels.json'))
items=j.get('Items', j if isinstance(j,list) else [])
names=[x.get('Name') for x in items]
print('CHANNELS=', names)
assert 'Phantom Movies' in names
assert 'Phantom Shows' in names
PY
MOVIES_ID=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/channels.json'))
items=j.get('Items', j if isinstance(j,list) else [])
print(next(x['Id'] for x in items if x.get('Name')=='Phantom Movies'))
PY
)
curl -s -H "X-Emby-Token: $TOK" "$BASE/Channels/$MOVIES_ID/Items" > /tmp/movies.json
python3 - <<'PY'
import json
j=json.load(open('/tmp/movies.json'))
items=j.get('Items', [])
print('MOVIE_COUNT=', len(items))
print('MOVIES=', [(x.get('Name'),x.get('Type'),x.get('Id'),x.get('Tags')) for x in items[:10]])
m=next((x for x in items if x.get('Name')=='Integration Movie'), None)
assert m, j
assert m.get('Type') == 'Movie', m
print('MOVIE_DTO_ID='+m['Id'])
PY
ITEM_ID=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/movies.json'))
print(next(x['Id'] for x in j['Items'] if x.get('Name')=='Integration Movie'))
PY
)
row=$(sqlite3 "$DATA/data/jellyfin.db" "SELECT ExternalId || '|' || Path || '|' || Tags FROM BaseItems WHERE Id=upper(substr('$ITEM_ID',1,8)||'-'||substr('$ITEM_ID',9,4)||'-'||substr('$ITEM_ID',13,4)||'-'||substr('$ITEM_ID',17,4)||'-'||substr('$ITEM_ID',21));")
echo "BASEITEM_ROW=$row"
case "$row" in
  movie_42\|*splash.mp4\|*phantom*) : ;;
  *) echo "BAD_BASEITEM_ROW=$row"; exit 1 ;;
esac
ITEM_ID_HYPHEN=$(python3 - <<PY2
s='$ITEM_ID'
print(f'{s[:8]}-{s[8:12]}-{s[12:16]}-{s[16:20]}-{s[20:]}')
PY2
)
echo "POST_ID=$ITEM_ID_HYPHEN"
curl -s -X POST -H "X-Emby-Token: $TOK" -H 'Content-Type: application/json' -d "{\"ids\":[\"$ITEM_ID_HYPHEN\"]}" "$BASE/Plugins/PhantomLibrary/States" > /tmp/states.json
python3 - <<'PY'
import json
j=json.load(open('/tmp/states.json'))
print('STATES=', j)
assert any(v=='Phantom' for v in j.values()), j
PY

echo INTEGRATION_OK
