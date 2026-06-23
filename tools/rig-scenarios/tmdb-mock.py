#!/usr/bin/env python3
"""TMDB v3 mock server for the Phantom Library test rig.

Listens on 127.0.0.1:18099 (override via $TMDB_MOCK_PORT). Serves a
fixed set of canned responses for /3/configuration, /3/discover/movie,
/3/discover/tv, /3/movie/{id}, /3/tv/{id}, plus the trending /
similar / recommendations endpoints.

Logs every request to /tmp/jf-rig/logs/tmdb-mock.log so scenarios can
assert on call sequence + parameters.

Fixtures live in /tmp/jf-rig/fixtures/tmdb/*.json. The fixture set is
deliberately small (3 movies + 2 series) with deterministic ids and
NON-fuzzy-matchable titles so the Jellyfin scanner's TmdbProvider
cannot rescue our items if the plugin's ProviderIds stamp gets
stripped — that's the discriminator between "our stamp survived" and
"scanner re-resolved it for us".
"""
from __future__ import annotations
import json, os, sys, time, urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(os.environ.get("JF_RIG_ROOT", "/tmp/jf-rig"))
FIX = ROOT / "fixtures" / "tmdb"
LOG = ROOT / "logs" / "tmdb-mock.log"
PORT = int(os.environ.get("TMDB_MOCK_PORT", "18099"))

# Deterministic fixture set. Ids chosen to be HIGH so they don't
# collide with real TMDB ids in the operator's prod DB; titles chosen
# to be unique strings the scanner cannot fuzzy-match to real TMDB
# entries.
FIXTURES = {
    "movies": [
        {"id": 99000001, "title": "Phantom Rig Alpha", "release_date": "2024-01-15",
         "overview": "Deterministic test movie alpha.", "poster_path": "/alpha.jpg",
         "backdrop_path": "/alpha-bd.jpg", "vote_average": 7.5, "vote_count": 100,
         "original_title": "Phantom Rig Alpha", "genre_ids": [28]},
        {"id": 99000002, "title": "Phantom Rig Bravo", "release_date": "2024-02-15",
         "overview": "Deterministic test movie bravo.", "poster_path": "/bravo.jpg",
         "backdrop_path": "/bravo-bd.jpg", "vote_average": 6.8, "vote_count": 80,
         "original_title": "Phantom Rig Bravo", "genre_ids": [18]},
        {"id": 99000003, "title": "Phantom Rig Charlie", "release_date": "2024-03-15",
         "overview": "Deterministic test movie charlie.", "poster_path": "/charlie.jpg",
         "backdrop_path": "/charlie-bd.jpg", "vote_average": 7.0, "vote_count": 50,
         "original_title": "Phantom Rig Charlie", "genre_ids": [35]},
    ],
    "series": [
        {"id": 99100001, "name": "Phantom Rig Delta", "first_air_date": "2024-04-01",
         "overview": "Deterministic test series delta.", "poster_path": "/delta.jpg",
         "backdrop_path": "/delta-bd.jpg", "vote_average": 7.2, "vote_count": 70,
         "original_name": "Phantom Rig Delta", "genre_ids": [18, 80]},
        {"id": 99100002, "name": "Phantom Rig Echo", "first_air_date": "2024-05-01",
         "overview": "Deterministic test series echo.", "poster_path": "/echo.jpg",
         "backdrop_path": "/echo-bd.jpg", "vote_average": 8.0, "vote_count": 200,
         "original_name": "Phantom Rig Echo", "genre_ids": [10765]},
    ],
}

def _movie(i):
    f = FIXTURES["movies"][i]
    return {**f, "runtime": 95, "genres": [{"id": 28, "name": "Action"}],
            "status": "Released", "tagline": "", "imdb_id": f"tt9900000{i+1}",
            "budget": 0, "revenue": 0}

def _series(i):
    f = FIXTURES["series"][i]
    return {**f, "genres": [{"id": 18, "name": "Drama"}], "status": "Returning Series",
            "number_of_seasons": 1, "number_of_episodes": 8,
            "origin_country": ["US"], "imdb_id": f"tt9910000{i+1}"}


def _season(series_tmdb_id: int, season: int):
    series = SPECIAL_SERIES.get(series_tmdb_id) or next((s for s in FIXTURES["series"] if s["id"] == series_tmdb_id), None)
    if series is None or season != 1:
        return None
    title = series["name"]
    return {
        "id": series_tmdb_id + season,
        "name": "Season 1",
        "overview": f"Season {season} overview for {title}.",
        "poster_path": f"/{series_tmdb_id}-s{season:02d}-poster.jpg",
        "air_date": "2024-04-01",
        "season_number": season,
        "episodes": [
            {
                "id": series_tmdb_id + (season * 100) + e,
                "name": f"{title} Episode {e}",
                "overview": f"Deterministic test episode {e} for {title}.",
                "episode_number": e,
                "season_number": season,
                "air_date": f"2024-04-{e:02d}",
                "still_path": f"/{series_tmdb_id}-s{season:02d}e{e:02d}.jpg",
                "runtime": 42,
                "vote_average": 7.0,
            }
            for e in range(1, 9)
        ],
    }

SPECIAL_SERIES = {
    85552: {
        "id": 85552,
        "name": "Euphoria",
        "first_air_date": "2019-06-16",
        "overview": "Real-id fixture for episode materialisation retry testing.",
        "poster_path": "/euphoria.jpg",
        "backdrop_path": "/euphoria-bd.jpg",
        "vote_average": 8.3,
        "vote_count": 1000,
        "original_name": "Euphoria",
        "genre_ids": [18],
        "imdb_id": "tt8772296",
        "number_of_seasons": 1,
        "number_of_episodes": 8,
    },
}

ROUTES = {}

def route(path):
    def deco(fn):
        ROUTES[path] = fn
        return fn
    return deco

@route("/3/configuration")
def _cfg(q):
    return 200, {"images": {"secure_base_url": "https://image.tmdb.org/t/p/",
                            "poster_sizes": ["original"], "backdrop_sizes": ["original"]}}

@route("/3/discover/movie")
def _disc_m(q):
    page = int(q.get("page", "1"))
    items = [_movie(i) for i in range(len(FIXTURES["movies"]))] if page == 1 else []
    return 200, {"page": page, "results": items, "total_pages": 1, "total_results": len(items)}

@route("/3/discover/tv")
def _disc_t(q):
    page = int(q.get("page", "1"))
    items = [_series(i) for i in range(len(FIXTURES["series"]))] if page == 1 else []
    return 200, {"page": page, "results": items, "total_pages": 1, "total_results": len(items)}

@route("/3/search/movie")
def _search_m(q):
    query = (q.get("query") or "").casefold()
    year = q.get("year") or q.get("primary_release_year")
    results = []
    for i in range(len(FIXTURES["movies"])):
        movie = _movie(i)
        if query and query not in movie["title"].casefold():
            continue
        if year and not movie.get("release_date", "").startswith(str(year)):
            continue
        results.append(movie)
    return 200, {"page": 1, "results": results, "total_pages": 1, "total_results": len(results)}

@route("/3/trending/movie/day")
@route("/3/trending/movie/week")
def _trend_m(q):
    return 200, {"page": 1, "results": [_movie(i) for i in range(len(FIXTURES["movies"]))],
                 "total_pages": 1, "total_results": len(FIXTURES["movies"])}

@route("/3/trending/tv/day")
@route("/3/trending/tv/week")
def _trend_t(q):
    return 200, {"page": 1, "results": [_series(i) for i in range(len(FIXTURES["series"]))],
                 "total_pages": 1, "total_results": len(FIXTURES["series"])}

def _movie_by_id(tmdb_id, q):
    for i, m in enumerate(FIXTURES["movies"]):
        if m["id"] == tmdb_id:
            return 200, _movie(i)
    return 404, {"status_code": 34, "status_message": "not found"}

def _series_by_id(tmdb_id, q):
    if tmdb_id in SPECIAL_SERIES:
        s = SPECIAL_SERIES[tmdb_id]
        return 200, {**s, "genres": [{"id": 18, "name": "Drama"}], "status": "Returning Series",
                     "origin_country": ["US"]}
    for i, s in enumerate(FIXTURES["series"]):
        if s["id"] == tmdb_id:
            return 200, _series(i)
    return 404, {"status_code": 34, "status_message": "not found"}

class H(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        # Suppress stderr; we have our own log.
        pass

    def _log_req(self, status):
        ts = time.strftime("%Y-%m-%d %H:%M:%S")
        with LOG.open("a") as f:
            f.write(f"{ts} {self.command} {self.path} -> {status}\n")

    def do_GET(self):
        u = urllib.parse.urlparse(self.path)
        q = dict(urllib.parse.parse_qsl(u.query))
        status, body = self._resolve(u.path, q)
        raw = json.dumps(body).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)
        self._log_req(status)

    def _resolve(self, path, q):
        # Direct route?
        if path in ROUTES:
            return ROUTES[path](q)
        # /3/movie/{id}, /3/tv/{id}
        parts = path.strip("/").split("/")
        if len(parts) == 3 and parts[0] == "3" and parts[1] == "movie":
            try: return _movie_by_id(int(parts[2]), q)
            except ValueError: pass
        if len(parts) == 3 and parts[0] == "3" and parts[1] == "tv":
            try: return _series_by_id(int(parts[2]), q)
            except ValueError: pass
        # /3/tv/{id}/season/{season}
        if len(parts) == 5 and parts[0] == "3" and parts[1] == "tv" and parts[3] == "season":
            try:
                season = _season(int(parts[2]), int(parts[4]))
                if season is not None:
                    return 200, season
                return 404, {"status_code": 34, "status_message": "not found"}
            except ValueError: pass
        # /3/movie/{id}/external_ids, /3/tv/{id}/external_ids
        if len(parts) == 4 and parts[0] == "3" and parts[3] == "external_ids":
            try:
                tid = int(parts[2])
                if parts[1] == "tv":
                    if tid in SPECIAL_SERIES:
                        return 200, {"imdb_id": SPECIAL_SERIES[tid]["imdb_id"]}
                    for i, s in enumerate(FIXTURES["series"]):
                        if s["id"] == tid:
                            return 200, {"imdb_id": f"tt9910000{i+1}"}
                return 200, {"imdb_id": f"tt9900000{tid % 10}"}
            except ValueError: pass
        # Movie/Series similar/recommendations -> empty
        if len(parts) == 4 and parts[0] == "3" and parts[3] in ("similar", "recommendations", "images"):
            if parts[3] == "images":
                return 200, {"posters": [], "backdrops": [], "logos": []}
            return 200, {"page": 1, "results": [], "total_pages": 1, "total_results": 0}
        return 404, {"status_code": 34, "status_message": f"mock has no route for {path}"}

def main():
    LOG.parent.mkdir(parents=True, exist_ok=True)
    LOG.write_text(f"# tmdb-mock started {time.strftime('%Y-%m-%d %H:%M:%S')} on port {PORT}\n")
    srv = ThreadingHTTPServer(("127.0.0.1", PORT), H)
    print(f"tmdb-mock listening on 127.0.0.1:{PORT}", flush=True)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass

if __name__ == "__main__":
    main()
