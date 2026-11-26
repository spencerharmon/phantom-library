#!/usr/bin/env python3
"""Real PostgreSQL/loaded DLL/REST proof of empty-candidate eager re-probing."""
import importlib.util
import json
from pathlib import Path
import sys
import subprocess
import time

spec = importlib.util.spec_from_file_location("playback_scenarios", Path(__file__).with_name("playback-scenarios.py"))
rig = importlib.util.module_from_spec(spec)
spec.loader.exec_module(rig)


def snapshot(where):
    return json.loads(rig.DB.query("SELECT row_to_json(a) FROM availability_items a WHERE " + where))


def assert_excluded(visible, rows, kind, target, control):
    ids = visible["Movies" if kind == "movie" else "Series"]
    assert target not in ids and control in ids, ("repository exclusion/control", ids)
    rest_ids = [x.get("ProviderIds", {}).get("Tmdb") for x in rows]
    assert str(target) not in rest_ids and str(control) in rest_ids, ("REST exclusion/control", rest_ids)


def assert_reprobe(before, after, events, candidates, fresh_hash, now):
    assert before["status"] == "available" and before["candidate_magnet"], before
    assert before["priority"] == 0 and before["next_check_at"] > now, before
    assert before["probe_policy_hash"] == after["probe_policy_hash"], "policy drift could make row ordinarily due"
    promoted = next((e for e in events if e["priority"] >= 50 and e["next_check_at"] <= now
                     and e["probe_generation"] == before["probe_generation"]), None)
    assert promoted, ("no pre-claim eager promotion observed", events)
    assert after["priority"] >= 50 and after["probe_generation"] == before["probe_generation"] + 1, after
    assert after["status"] == "available" and after["checked_at"] >= now and after["next_check_at"] > now, after
    assert after["candidate_info_hash"] == fresh_hash and after["candidate_indexer"] == "Torrentio", after
    assert after["lease_owner"] is None and after["last_error_kind"] is None, after
    assert len(candidates) >= 1 and any(c["info_hash"] == fresh_hash and c["source"] == "availability_probe"
        and c["fetched_at"] >= now and c["expires_at"] > now and c["validation_status"] != "invalid" for c in candidates), candidates


def wait_restored(channel, target):
    # Allow asynchronous server cache invalidation, but never change DataVersion,
    # restart Jellyfin, seed a candidate, or trigger another probe to manufacture success.
    observations = []
    for attempt in range(11):
        rows = rig.browse(channel)
        observations.append([x.get("ProviderIds", {}).get("Tmdb") for x in rows])
        restored = next((x for x in rows if x.get("ProviderIds", {}).get("Tmdb") == str(target)), None)
        if restored:
            return restored
        if attempt < 10:
            time.sleep(1)
    raise AssertionError(f"REST visibility not restored within 10s for {target}: {observations}")


def main(kind):
    assert kind in ("movie", "episode")
    assert rig.DB.assert_backend()["backend"] == "postgres", "this live check requires PostgreSQL"
    rig.discovery(kind)
    movie = kind == "movie"
    target, control = (99000001, 99000003) if movie else (99100001, 99100002)
    season, episode = (-1, -1) if movie else (1, 1)
    where = f"tmdb_id={target} AND type='{kind}' AND season={season} AND episode={episode}"
    policy = rig.api("/Rig/Availability/Policy")
    assert policy["Enabled"] is False and policy["Ticks"] == 0, policy
    now = int(time.time())
    future = now + 604800
    # Quiesce unrelated discovery backlog; the target has the CURRENT policy and
    # a future due date, so a normal claim cannot pass instead of eager promotion.
    rig.DB.execute(f"UPDATE availability_items SET next_check_at={future}, probe_policy_hash='{policy['Hash']}';\n"
                   f"UPDATE series_expansion_state SET next_expand_at={future};")
    for tmdb, magnet in ((target, "magnet:?xt=urn:btih:" + "c" * 40), (control, None)):
        rig.DB.upsert("availability_items", ("tmdb_id", "type", "season", "episode"),
                      dict(tmdb_id=tmdb, type=kind, season=season, episode=episode, status="available",
                           checked_at=now - 86400, next_check_at=future, candidate_magnet=magnet,
                           probe_policy_hash=policy["Hash"], priority=0, lease_owner=None, lease_until=None))
    rig.DB.execute(f"DELETE FROM source_candidates WHERE tmdb_id IN ({target},{control});\n"
                   f"DELETE FROM magnet_cache WHERE tmdb_id IN ({target},{control});")
    assert rig.DB.query(f"SELECT COUNT(*) FROM source_candidates WHERE {where}") == "0"
    assert rig.DB.query(f"SELECT COUNT(*) FROM materialised_state WHERE {where}") == "0"
    # This trigger OBSERVES real worker writes in the disposable DB. It never
    # alters NEW/OLD or any production table, and captures promotion before claim.
    rig.DB.execute(f"""
        CREATE TABLE rig_availability_events (seq BIGSERIAL PRIMARY KEY, observed JSONB NOT NULL);
        CREATE FUNCTION rig_observe_availability() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          INSERT INTO rig_availability_events(observed) VALUES (to_jsonb(NEW));
          RETURN NEW;
        END $$;
        CREATE TRIGGER rig_availability_observer AFTER UPDATE ON availability_items
        FOR EACH ROW WHEN (NEW.tmdb_id={target} AND NEW.type='{kind}')
        EXECUTE FUNCTION rig_observe_availability();
    """)
    before = snapshot(where)
    (rig.OUT / "stale-before.json").write_text(json.dumps(before, indent=2))
    channel_name = "Phantom Movies" if movie else "Phantom Shows"
    channel = next(x["Id"] for x in rig.api("/Channels")["Items"] if x["Name"] == channel_name)
    assert_excluded(rig.api("/Rig/Availability/Visible"), rig.browse(channel), kind, target, control)
    print(f"STALE_EXCLUDED {kind} repository=true REST=true control={control} candidates=0", flush=True)
    before_policy = rig.api("/Rig/Availability/Policy")
    tick = rig.api("/Rig/Availability/Tick", post=True)
    assert tick["Ticks"] == 1, tick
    after_policy = rig.api("/Rig/Availability/Policy")
    (rig.OUT / "channel-versions.json").write_text(json.dumps({"before": before_policy, "after": after_policy}, indent=2))
    after = snapshot(where)
    events = json.loads(rig.DB.query("SELECT COALESCE(json_agg(observed ORDER BY seq),'[]') FROM rig_availability_events;"))
    candidates = json.loads(rig.DB.query(f"SELECT COALESCE(json_agg(s),'[]') FROM source_candidates s WHERE {where};"))
    receipt = {"before": before, "after": after, "events": events, "candidates": candidates, "tick": tick}
    (rig.OUT / "reprobe-observations.json").write_text(json.dumps(receipt, indent=2))
    assert_reprobe(before, after, events, candidates, ("a" if movie else "b") * 40, now)
    print(f"EAGER_REPROBE {kind} ticks=1 priority={after['priority']} generation={before['probe_generation']}->{after['probe_generation']} candidates={len(candidates)}", flush=True)
    visible = rig.api("/Rig/Availability/Visible")
    assert target in visible["Movies" if movie else "Series"], visible
    print(f"REPOSITORY_RESTORED {kind} versions_before={before_policy} versions_after={after_policy}", flush=True)
    restored = wait_restored(channel, target)
    if not movie:
        season_item = next(x for x in rig.browse(channel, restored["Id"]) if x["Name"] == "Season 1")
        ep = next(x for x in rig.browse(channel, season_item["Id"]) if x["IndexNumber"] == 1)
        assert ep["Type"] == "Episode" and ep["ParentIndexNumber"] == 1, ep
    assert rig.api("/Rig/Availability/Policy")["Ticks"] == 1
    print(f"VISIBILITY_RESTORED {kind} repository=true REST=true", flush=True)
    receipt = rig.DB.assert_backend()
    receipt.update(scenario=kind, result="passed")
    (rig.OUT / "result.json").write_text(json.dumps(receipt, indent=2))


if __name__ == "__main__":
    try:
        main(sys.argv[1])
    except subprocess.CalledProcessError as error:
        print(error.stderr, file=sys.stderr)
        raise
