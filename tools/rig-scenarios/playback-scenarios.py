#!/usr/bin/env python3
"""35/36: real Jellyfin native-open movie/episode parity, shared across DB backends."""
import json
import os
from pathlib import Path
import sqlite3
import sys
import time
import urllib.request
import uuid

from playback_db import PhantomDatabase

ROOT = Path(os.environ["JF_RIG_ROOT"])
OUT = Path(os.environ["PHANTOM_RIG_OUTPUT"])
OUT.mkdir(parents=True, exist_ok=True)
API = os.environ["PHANTOM_RIG_API"]
TOKEN = os.environ["PHANTOM_RIG_TOKEN"]
JDB = os.environ["PHANTOM_RIG_JDB"]
DB = PhantomDatabase()
USER = None
AUTH = None
SEQUENCE = 0


def api(path, body=None, *, post=False, user=False):
    global SEQUENCE
    token = AUTH if user else TOKEN
    headers = {"X-Emby-Token": token,
               "X-Emby-Authorization": 'MediaBrowser Client="phantom-rig", Device="phantom-rig", '
               f'DeviceId="phantom-parity", Version="1", Token="{token}"'}
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    request = urllib.request.Request(API + path, data=data, headers=headers,
                                     method="POST" if post or body is not None else "GET")
    with urllib.request.urlopen(request, timeout=120) as response:
        raw = response.read()
    SEQUENCE += 1
    # Keep every response, including retries, rather than overwrite failure evidence.
    result = json.loads(raw) if raw else None
    recorded = {**result, "AccessToken": "<synthetic-token-redacted>"} if isinstance(result, dict) and "AccessToken" in result else result
    (OUT / f"{SEQUENCE:03d}-{path.split('?')[0].strip('/').replace('/', '-')}.json").write_text(json.dumps(recorded))
    return result


def items(path):
    return api(path)["Items"]


def jellyfin_sql(sql, args):
    with sqlite3.connect(JDB, timeout=30) as db:
        return db.execute(sql, args).fetchone()


def guid(item):
    return str(uuid.UUID(item["Id"]))


def source(info, *, opening=False, kind="movie", streams=1):
    assert not info.get("ErrorCode"), info
    sources = info.get("MediaSources") or []
    assert len(sources) == 1, sources
    src = sources[0]
    uuid.UUID(src["Id"])
    if opening:
        assert src.get("RequiresOpening"), src
        suffix = "movie_99000001" if kind == "movie" else "episode_99100001_s01e01"
        assert src.get("OpenToken", "").endswith("_phantom:" + suffix), src
        assert src.get("Path") in (None, ""), src
    else:
        assert not src.get("RequiresOpening"), src
        directory = ROOT / "gostream" / ("movies" if kind == "movie" else "tv")
        assert str(src.get("Path", "")).startswith(str(directory) + "/"), src
        assert src.get("Protocol") == "File", src
        assert len(src.get("MediaStreams") or []) >= streams, src
    print("SOURCE", {k: src.get(k) for k in ("Id", "Path", "RequiresOpening", "OpenToken", "Protocol")}, flush=True)
    return src


def playback(item, kind, opening=False, auto=False):
    path = f"/Items/{guid(item)}/PlaybackInfo"
    if auto:
        info = api(path + "?AutoOpenLiveStream=true", {"AutoOpenLiveStream": True})
    else:
        # The existing external-file probe may complete asynchronously.
        for _ in range(30):
            info = api(path)
            if opening or len((info.get("MediaSources") or [{}])[0].get("MediaStreams") or []) >= 1:
                break
            time.sleep(1)
    return source(info, opening=opening, kind=kind)


def stream(item, label):
    request = urllib.request.Request(API + f"/Videos/{guid(item)}/stream.mkv?static=true",
                                     headers={"X-Emby-Token": TOKEN, "Range": "bytes=0-4095"})
    with urllib.request.urlopen(request, timeout=20) as response:
        assert response.status in (200, 206), response.status
        data = response.read(4096)
        assert data, "empty real stream"
        (OUT / (label + ".bin")).write_bytes(data)
        print(f"STREAM {label} http={response.status} bytes={len(data)}", flush=True)


def runtime(item, minimum):
    assert item.get("RunTimeTicks", 0) >= minimum, item
    print("RUNTIME", item["Name"], item["RunTimeTicks"], flush=True)


def resume(item, src, position):
    payload = {"ItemId": guid(item), "MediaSourceId": src["Id"],
               "PlaySessionId": "phantom-parity-" + item["Id"], "PositionTicks": position,
               "CanSeek": True, "IsPaused": True, "PlayMethod": "DirectPlay"}
    api("/Sessions/Playing", payload, user=True)
    api("/Sessions/Playing/Progress", payload, user=True)
    ticks = jellyfin_sql("SELECT COALESCE(RunTimeTicks,0) FROM BaseItems WHERE Id=upper(?)", (guid(item),))[0]
    if ticks > 0 and position >= ticks:
        position = ticks // 2
    api(f"/Users/{USER}/Items/{guid(item)}/UserData",
        {"PlaybackPositionTicks": position, "Played": False, "PlayCount": 0, "IsFavorite": False}, user=True)
    for _ in range(20):
        result = api(f"/Users/{USER}/Items/Resume?Fields=UserData,ProviderIds,RunTimeTicks&MediaTypes=Video&EnableUserData=true&Limit=50", user=True)
        match = next((x for x in result["Items"] if uuid.UUID(x["Id"]) == uuid.UUID(item["Id"])), None)
        if match:
            assert match["UserData"]["PlaybackPositionTicks"] > 0, match
            print("RESUME", match["Id"], match["UserData"], flush=True)
            return
        time.sleep(1)
    raise AssertionError(f"item missing from Continue Watching: {item['Id']}")


def discovery(kind):
    task = next(t for t in api("/ScheduledTasks") if t["Key"] == "PhantomLibrary.DiscoveryRefresh")
    previous = task.get("LastExecutionResult")
    api("/ScheduledTasks/Running/" + task["Id"], post=True)
    for _ in range(120):
        current = next(t for t in api("/ScheduledTasks") if t["Id"] == task["Id"])
        result = current.get("LastExecutionResult")
        if current["State"] == "Idle" and result and result != previous:
            assert result["Status"] == "Completed", result
            break
        time.sleep(1)
    else:
        raise AssertionError("discovery did not become Idle")
    typ, count = ("movie", 3) if kind == "movie" else ("series", 2)
    actual = int(DB.query(f"SELECT COUNT(*) FROM catalogue_items WHERE type='{typ}';"))
    assert actual >= count, (typ, actual)
    print("DISCOVERY", typ, actual, flush=True)


def seed(kind):
    movie = kind == "movie"
    tmdb, imdb = (99000001, "tt99000001") if movie else (99100001, "tt99100001")
    season, episode = (-1, -1) if movie else (1, 1)
    hash_ = "1" * 40 if movie else "2" * 40
    magnet = "magnet:?xt=urn:btih:" + hash_ + "&dn=" + ("Phantom+Rig+Alpha" if movie else "Phantom+Rig+Delta+S01E01")
    now = int(time.time())
    DB.upsert("magnet_cache", ("tmdb_id", "imdb_id", "type", "season", "episode", "preset"),
              dict(tmdb_id=tmdb, imdb_id=imdb, type=kind, season=0 if movie else season,
                   episode=0 if movie else episode, preset="gostream-default", magnet=magnet,
                   info_hash=hash_, size=10485760, seeders=100, indexer="rig-cache", cached_at=now,
                   ttl_seconds=86400, source="rig"))
    DB.upsert("availability_items", ("tmdb_id", "type", "season", "episode"),
              dict(tmdb_id=tmdb, type=kind, season=season, episode=episode, status="available",
                   checked_at=now, next_check_at=now + 604800, candidate_magnet=magnet,
                   candidate_info_hash=hash_, candidate_size=10485760, candidate_seeders=100,
                   candidate_indexer="rig-cache", candidate_source="rig"))
    # Current browse pruning distinguishes previously-assessed/no-candidate
    # rows from never-assessed ones. Seed a real candidate, not a stale available
    # summary left over from the initial discovery probe.
    DB.upsert("source_candidates", ("tmdb_id", "type", "season", "episode", "preset", "magnet"),
              dict(tmdb_id=tmdb, type=kind, season=season, episode=episode, preset="gostream-default",
                   magnet=magnet, info_hash=hash_, indexer="rig-cache", title="Parity fixture",
                   seeders=100, size=10485760, rank=1, source="rig", fetched_at=now,
                   expires_at=now + 604800, validation_status="unknown", validation_policy_version="unknown"))
    DB.upsert("plugin_meta", ("key",), {"key": "channel_dataversion_" + ("movies" if movie else "shows"), "value": f"{now}-rig-seed"})


FIELDS = "Tags,ProviderIds,MediaSources,Path,Overview,ProductionYear,RunTimeTicks,ExternalId"


def browse(channel, folder=None):
    path = f"/Channels/{channel}/Items?Fields={FIELDS}&Limit=50"
    return items(path + ("&FolderId=" + folder if folder else ""))


def match_tmdb(rows, tmdb):
    return next(x for x in rows if x.get("ProviderIds", {}).get("Tmdb") == str(tmdb))


def wait_metadata(item, *, after="1970-01-01", path=None):
    """Wait for the real queued metadata save, not a dynamic MediaSource.

    ChannelManager queues probes on first browse and on post-flight refresh.
    A new MediaSource can already be real while the earlier phantom probe still
    owns an old BaseItem. Do not overlap those fixture phases: its late save can
    otherwise put phantom Tags back (ordinary browse doesn't overwrite Tags).
    DateLastRefreshed is persisted by MetadataService after providers finish.
    """
    observations = []
    try:
        for _ in range(60):
            row = jellyfin_sql("SELECT DateLastRefreshed,Path,Tags FROM BaseItems WHERE Id=upper(?)", (guid(item),))
            assert row is not None, f"BaseItem disappeared during metadata refresh: {item['Id']}"
            observations.append(row)
            if row[0] and row[0] > after and (path is None or (row[1] == path and "phantom" not in (row[2] or "").split("|"))):
                print("METADATA_SAVED", item["Id"], row, flush=True)
                return row[0]
            time.sleep(1)
        raise AssertionError(f"queued metadata save did not converge: after={after} path={path} rows={observations}")
    finally:
        phase = "materialised" if path else "initial"
        (OUT / f"metadata-{item['Id']}-{phase}.json").write_text(json.dumps(observations, indent=2))


def materialise(item, kind, channel, folder=None):
    assert "phantom" in item.get("Tags", []), item
    source(item, opening=True, kind=kind)
    playback(item, kind, opening=True)
    before = wait_metadata(item)
    opened = playback(item, kind, auto=True)
    where = "tmdb_id=99000001 AND type='movie' AND season=-1 AND episode=-1" if kind == "movie" else "tmdb_id=99100001 AND type='episode' AND season=1 AND episode=1"
    assert DB.query("SELECT COUNT(*) FROM materialised_state WHERE " + where) == "1"
    wait_metadata(item, after=before, path=opened["Path"])
    for _ in range(20):
        refreshed = next(x for x in browse(channel, folder) if x["Id"] == item["Id"])
        if "phantom" not in refreshed.get("Tags", []) and (refreshed.get("MediaSources") or [{}])[0].get("Path"):
            break
        time.sleep(1)
    assert "phantom" not in refreshed.get("Tags", []), refreshed
    source(refreshed, kind=kind, streams=0)
    runtime(refreshed, 1)
    real = playback(item, kind)
    stream(item, "materialised-" + kind)
    if kind == "episode":
        playback(item, kind)
        stream(item, "materialised-episode-second-play")
    resume(item, real, 12000000000 if kind == "movie" else 10000000000)
    row = jellyfin_sql("SELECT ExternalId,Path,COALESCE(RunTimeTicks,0) FROM BaseItems WHERE Id=upper(?)", (guid(item),))
    expected = "movie_99000001" if kind == "movie" else "episode_99100001_s01e01"
    assert row[0] == expected and row[1] == real["Path"] and row[2] > 0, row
    print("PERSISTED", row, flush=True)


def movie():
    discovery("movie")
    seed("movie")
    channel = next(x["Id"] for x in api("/Channels")["Items"] if x["Name"] == "Phantom Movies")
    rows = browse(channel)
    alpha, bravo = match_tmdb(rows, 99000001), match_tmdb(rows, 99000002)
    runtime(alpha, 57000000000)
    runtime(bravo, 57000000000)
    assert not {"orphan", "phantom"}.intersection(bravo.get("Tags", [])), bravo
    assert not any("Phantom_Rig_Bravo" in x["Name"] for x in rows), rows
    assert bravo["Name"] == "Phantom Rig Bravo", bravo
    assert bravo["MediaSources"][0]["Path"] == str(ROOT / "gostream/movies/Phantom_Rig_Bravo_2024_2160p_HDR_cafebabe.mkv"), bravo
    real = playback(bravo, "movie")
    stream(bravo, "existing-gostream-bravo")
    resume(bravo, real, 12000000000)
    materialise(alpha, "movie", channel)
    print("SOURCE_REPLACEMENT", flush=True)
    where = "tmdb_id=99000001 AND type='movie' AND season=-1 AND episode=-1"
    old = DB.query("SELECT stub_path FROM materialised_state WHERE " + where)
    magnet = "magnet:?xt=urn:btih:" + "2" * 40 + "&dn=Phantom+Rig+Alpha+Alternate"
    now = int(time.time())
    DB.execute(f"UPDATE availability_items SET candidate_magnet='{magnet}', candidate_info_hash='{'2' * 40}', candidate_size=20971520, candidate_seeders=55, candidate_indexer='rig-alt', candidate_source='rig-alt', status='available', checked_at={now}, next_check_at={now + 604800} WHERE {where};")
    DB.upsert("source_candidates", ("tmdb_id", "type", "season", "episode", "preset", "magnet"),
              dict(tmdb_id=99000001, type="movie", season=-1, episode=-1, preset="gostream-default",
                   magnet=magnet, info_hash="2" * 40, indexer="rig-alt", title="Phantom Rig Alpha Alternate",
                   seeders=55, size=20971520, rank=2, source="rig-alt", fetched_at=now,
                   expires_at=now + 604800, validation_status="unknown", validation_policy_version="unknown"))
    result = api("/Plugins/PhantomLibrary/Items/movie_99000001/Sources")
    candidates = result.get("Candidates") or result.get("candidates") or []
    assert any((c.get("Magnet") or c.get("magnet")) == magnet for c in candidates), result
    result = api("/Plugins/PhantomLibrary/Items/movie_99000001/Sources/RejectCurrent", post=True)
    code, status = result.get("Code") or result.get("code"), str(result.get("Status") or result.get("status"))
    assert code == "materialised" or "Success" in status or status == "0", result
    new = DB.query("SELECT stub_path FROM materialised_state WHERE " + where)
    assert new and new != old, (old, new)
    reason = DB.query("SELECT reason FROM magnet_failure_cache WHERE tmdb_id=99000001 AND type='movie' AND magnet LIKE 'magnet:%1111111111111111111111111111111111111111%' LIMIT 1;")
    assert reason == "operator_rejected", reason
    browse(channel)
    playback(alpha, "movie")
    stream(alpha, "materialised-alpha-after-source-reject")
    print("CHANNEL_E2E_PLAYBACK_OK", flush=True)


def episode():
    discovery("episode")
    seed("episode")
    channel = next(x["Id"] for x in api("/Channels")["Items"] if x["Name"] == "Phantom Shows")
    series = match_tmdb(browse(channel), 99100001)
    season = next(x for x in browse(channel, series["Id"]) if x["Name"] == "Season 1")
    assert season["Type"] == "Season", season
    for text in ("Season 1 overview for Phantom Rig Delta.", "8 episodes", "1 available/materialised", "7 unknown"):
        assert text in season.get("Overview", ""), season
    assert season["ProductionYear"] == 2024, season
    ep = next(x for x in browse(channel, season["Id"]) if x["Name"] == "Phantom Rig Delta Episode 1")
    runtime(ep, 25200000000)
    children = api(f"/Users/{USER}/Items?ParentId={season['Id']}&Fields=Tags,IndexNumber,ParentIndexNumber,Overview,RunTimeTicks&Limit=20")
    assert children["TotalRecordCount"] == 8 and len(children["Items"]) == 8, children
    badges = api("/Plugins/PhantomLibrary/States", {"ids": [series["Id"], season["Id"], ep["Id"]]})
    assert series["Id"] not in badges and season["Id"] not in badges, badges
    assert badges.get(ep["Id"]) == "Phantom", badges
    print("BADGE_SCOPE", badges, flush=True)
    materialise(ep, "episode", channel, season["Id"])
    print("CHANNEL_EPISODE_E2E_PLAYBACK_OK", flush=True)


if __name__ == "__main__":
    kind = sys.argv[1]
    assert kind in ("movie", "episode")
    print("DATABASE", DB.assert_backend(), flush=True)
    auth = api("/Users/AuthenticateByName", {"Username": "a", "Pw": "a"})
    USER, AUTH = auth["User"]["Id"], auth["AccessToken"]
    (movie if kind == "movie" else episode)()
    receipt = DB.assert_backend()
    receipt.update(scenario=kind, result="passed")
    (OUT / "result.json").write_text(json.dumps(receipt, indent=2) + "\n")
