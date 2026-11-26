/* Phantom Library — in-player stream stats overlay (ROI P14, p14-player-stats-overlay).
 *
 * Injected like the other web shims:
 *
 *     <script src="/Plugins/PhantomLibrary/playerstats.js" defer></script>
 *
 * and additionally bootstrapped by badges.js (so every jellyfin-web index.html
 * that already carries the badges.js tag picks this up without a new tag).
 * Loading it twice is a no-op (window.__phantomPlayerStatsLoaded).
 *
 * What it shows: while the Jellyfin video player is open on a Phantom movie or
 * episode, a small, collapsible panel with the current source (release, size,
 * indexer), live peers/seeders, download rate, buffered-ahead and gostream's
 * stream state.
 *
 * Data: GET /Plugins/PhantomLibrary/PlayerStats/Current (authenticated with the
 * user's own Jellyfin session via ApiClient). The SERVER resolves the caller's
 * own now-playing item and proxies gostream with its library token — the
 * browser never sees a gostream token or URL.
 *
 * Polling lifecycle (the contract scripts/tests/p14-player-stats-overlay.test.sh
 * enforces):
 *   - polls ONLY while the player is open (#/video route or a
 *     <video class="htmlvideoplayer"> element present), the page is visible,
 *     and the panel is not collapsed;
 *   - every pollIntervalMs from the server, clamped to 2000–5000 ms, with at
 *     most one request in flight and stale responses discarded;
 *   - stops immediately when the player closes, the page is backgrounded
 *     (visibilitychange → hidden), or the user collapses the panel;
 *   - stops when the server reports nothing playing (after playback was seen,
 *     or after a short start-up grace) or a non-phantom item; the <video>
 *     element's next `loadstart`/`playing` (autoplay of the next episode) re-arms it.
 *
 * Layout: desktop = compact card top-right; mobile (≤720 px wide or coarse
 * pointer) = full-width strip under the top OSD bar with 44 px touch toggle.
 * All dynamic text is set with textContent (release names are untrusted).
 *
 * No external dependencies. No build step. Pure browser JS.
 */
(function () {
    'use strict';

    if (window.__phantomPlayerStatsLoaded) return;
    window.__phantomPlayerStatsLoaded = true;

    var TAG = '[PhantomLibrary/playerstats]';
    var ENDPOINT = 'Plugins/PhantomLibrary/PlayerStats/Current';
    var ROOT_ID = 'phantom-player-stats';
    var CSS_ID = 'phantom-player-stats-css';
    var PREF_KEY = 'phantomPlayerStats.collapsed';
    var MIN_POLL_MS = 2000;
    var MAX_POLL_MS = 5000;
    var DEFAULT_POLL_MS = 3000;
    var STARTUP_GRACE_POLLS = 5;       // not_playing answers tolerated before the session reports Playing
    var MOBILE_MAX_WIDTH = 720;

    var STATUS_TEXT = {
        ok: null,
        materialising: 'Materialising…',
        not_materialised: 'Not materialised',
        no_source: 'Source unknown',
        invalid_hash: 'Source hash invalid',
        torrent_not_loaded: 'Torrent not loaded in gostream',
        gostream_unavailable: 'gostream unavailable'
    };

    var STREAM_STATE_TEXT = {
        warmup: 'Warming up',
        streaming: 'Streaming',
        stalled: 'Stalled',
        idle: 'Idle'
    };

    var st = {
        open: false,
        polling: false,
        timer: null,
        inflight: false,
        generation: 0,
        seenPlaying: false,
        notPlayingCount: 0,
        stoppedByServer: false,
        collapsed: readCollapsedPref(),
        root: null,
        fields: null,
        lastResponse: null,
        probed: false,
        requests: 0
    };

    function warn() {
        try { console.warn.apply(console, [TAG].concat([].slice.call(arguments))); } catch (e) { /* no console */ }
    }

    function getApiClient() {
        if (window.ApiClient) return window.ApiClient;
        if (window.connectionManager && window.connectionManager.currentApiClient) {
            return window.connectionManager.currentApiClient();
        }
        return null;
    }

    function readCollapsedPref() {
        try { return window.localStorage && window.localStorage.getItem(PREF_KEY) === '1'; } catch (e) { return false; }
    }

    function writeCollapsedPref(v) {
        try { if (window.localStorage) window.localStorage.setItem(PREF_KEY, v ? '1' : '0'); } catch (e) { /* private mode */ }
    }

    /* ---------- player detection ---------- */

    function findPlayerVideo() {
        var videos = document.getElementsByTagName('video');
        for (var i = 0; i < videos.length; i++) {
            var cls = ' ' + (videos[i].className || '') + ' ';
            if (cls.indexOf(' htmlvideoplayer ') !== -1) return videos[i];
        }
        return null;
    }

    function onVideoRoute() {
        var h = (window.location.hash || '').toLowerCase();
        return /^#!?\/video(\b|[/?]|$)/.test(h) || h.indexOf('/video?') !== -1;
    }

    function isPlayerOpen() {
        return onVideoRoute() || findPlayerVideo() !== null;
    }

    function pageHidden() {
        return document.hidden === true || document.visibilityState === 'hidden';
    }

    function isMobileLayout() {
        try {
            if (window.matchMedia) {
                if (window.matchMedia('(max-width: ' + MOBILE_MAX_WIDTH + 'px)').matches) return true;
                if (window.matchMedia('(pointer: coarse)').matches) return true;
            }
        } catch (e) { /* ignore */ }
        return typeof window.innerWidth === 'number' && window.innerWidth > 0 && window.innerWidth <= MOBILE_MAX_WIDTH;
    }

    /* ---------- formatting ---------- */

    function fmtBytes(n) {
        if (n === null || n === undefined || isNaN(n)) return null;
        var units = ['B', 'KB', 'MB', 'GB', 'TB'];
        var v = Number(n);
        var u = 0;
        while (v >= 1024 && u < units.length - 1) { v /= 1024; u++; }
        return (u === 0 ? String(Math.round(v)) : v.toFixed(v >= 100 ? 0 : 1)) + ' ' + units[u];
    }

    function fmtRate(n) {
        var b = fmtBytes(n);
        return b === null ? null : b + '/s';
    }

    function fmtSeconds(s) {
        if (s === null || s === undefined || isNaN(s)) return null;
        s = Math.max(0, Math.round(Number(s)));
        if (s < 60) return s + 's';
        return Math.floor(s / 60) + 'm ' + (s % 60) + 's';
    }

    function pad2(n) { return (n < 10 ? '0' : '') + n; }

    function dash(v) { return v === null || v === undefined || v === '' ? '—' : String(v); }

    /* ---------- DOM ---------- */

    function ensureStyles() {
        if (document.getElementById(CSS_ID)) return;
        var css = '' +
            '#' + ROOT_ID + '{position:fixed;z-index:100000;top:4.5em;right:1em;width:19em;max-width:calc(100vw - 2em);' +
            'background:rgba(10,14,39,.78);color:#e5e7eb;font:12px/1.35 system-ui,sans-serif;border-radius:8px;' +
            'box-shadow:0 2px 10px rgba(0,0,0,.4);pointer-events:auto;overflow:hidden}' +
            '#' + ROOT_ID + '.phantom-player-stats--mobile{top:calc(env(safe-area-inset-top,0px) + 3.75em);left:.5em;right:.5em;width:auto;max-width:none;font-size:11px}' +
            '#' + ROOT_ID + ' .pps-head{display:flex;align-items:center;justify-content:space-between;gap:.5em;padding:.35em .6em;cursor:pointer;color:#a78bfa;font-weight:600}' +
            '#' + ROOT_ID + ' .pps-toggle{background:none;border:0;color:inherit;font:inherit;cursor:pointer;min-width:2em;min-height:2em}' +
            '#' + ROOT_ID + '.phantom-player-stats--mobile .pps-toggle{min-width:44px;min-height:44px}' +
            '#' + ROOT_ID + ' .pps-body{display:grid;grid-template-columns:auto 1fr;gap:.15em .7em;padding:0 .6em .5em}' +
            '#' + ROOT_ID + '.phantom-player-stats--collapsed{width:auto;opacity:.6}' +
            '#' + ROOT_ID + '.phantom-player-stats--collapsed .pps-body{display:none}' +
            '#' + ROOT_ID + ' .pps-label{color:#9ca3af;white-space:nowrap}' +
            '#' + ROOT_ID + ' .pps-value{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}' +
            '#' + ROOT_ID + ' .pps-release{white-space:normal;word-break:break-all}' +
            '#' + ROOT_ID + ' .pps-stalled{color:#f87171}';
        var style = document.createElement('style');
        style.id = CSS_ID;
        style.textContent = css;
        (document.head || document.body).appendChild(style);
    }

    var FIELD_DEFS = [
        ['release', 'Release'],
        ['size', 'Size'],
        ['indexer', 'Indexer'],
        ['peers', 'Peers / seeders'],
        ['rate', 'Download'],
        ['buffer', 'Buffered ahead'],
        ['state', 'Stream']
    ];

    function ensureOverlay() {
        if (st.root && st.root.parentNode) return st.root;
        ensureStyles();
        var root = document.createElement('div');
        root.id = ROOT_ID;
        root.setAttribute('role', 'status');
        root.setAttribute('aria-live', 'polite');

        var head = document.createElement('div');
        head.className = 'pps-head';
        var title = document.createElement('span');
        title.className = 'pps-title';
        title.textContent = 'Phantom stream';
        var toggle = document.createElement('button');
        toggle.className = 'pps-toggle';
        toggle.setAttribute('type', 'button');
        toggle.setAttribute('aria-label', 'Toggle stream stats');
        head.appendChild(title);
        head.appendChild(toggle);
        head.addEventListener('click', function () { setCollapsed(!st.collapsed); });

        var body = document.createElement('div');
        body.className = 'pps-body';
        var fields = { title: title, toggle: toggle };
        for (var i = 0; i < FIELD_DEFS.length; i++) {
            var label = document.createElement('span');
            label.className = 'pps-label';
            label.textContent = FIELD_DEFS[i][1];
            var value = document.createElement('span');
            value.className = 'pps-value' + (FIELD_DEFS[i][0] === 'release' ? ' pps-release' : '');
            value.setAttribute('data-field', FIELD_DEFS[i][0]);
            value.textContent = '—';
            body.appendChild(label);
            body.appendChild(value);
            fields[FIELD_DEFS[i][0]] = value;
        }

        root.appendChild(head);
        root.appendChild(body);
        root.style.display = 'none';
        document.body.appendChild(root);
        st.root = root;
        st.fields = fields;
        applyLayout();
        return root;
    }

    function applyLayout() {
        if (!st.root) return;
        st.root.className = 'phantom-player-stats' +
            (isMobileLayout() ? ' phantom-player-stats--mobile' : '') +
            (st.collapsed ? ' phantom-player-stats--collapsed' : '');
        st.fields.toggle.textContent = st.collapsed ? '▸' : '▾';
        st.root.setAttribute('data-collapsed', st.collapsed ? '1' : '0');
    }

    function showOverlay() {
        ensureOverlay();
        applyLayout();
        st.root.style.display = '';
    }

    function hideOverlay() {
        if (st.root) st.root.style.display = 'none';
    }

    function setField(name, text) {
        if (st.fields && st.fields[name]) st.fields[name].textContent = dash(text);
    }

    function render(res) {
        showOverlay();
        var src = res.source || {};
        var stream = res.stream || {};
        var label = 'Phantom stream';
        if (res.type === 'episode' && res.season !== null && res.season !== undefined && res.episode !== null && res.episode !== undefined) {
            label += ' · S' + pad2(res.season) + 'E' + pad2(res.episode);
        }
        st.fields.title.textContent = label;
        setField('release', src.release);
        setField('size', fmtBytes(src.sizeBytes));
        setField('indexer', src.indexer);
        var peers = stream.peers === null || stream.peers === undefined ? null : stream.peers;
        var seeds = stream.seeders === null || stream.seeders === undefined ? null : stream.seeders;
        setField('peers', peers === null && seeds === null ? null : dash(peers) + ' / ' + dash(seeds));
        setField('rate', fmtRate(stream.downloadRateBytesPerSec));
        var buffer = fmtSeconds(stream.bufferedAheadSeconds);
        if (buffer === null && stream.bufferedAheadBytes !== null && stream.bufferedAheadBytes !== undefined) {
            buffer = fmtBytes(stream.bufferedAheadBytes);
        }
        setField('buffer', buffer);
        var stateText = STATUS_TEXT[res.status];
        if (stateText === undefined) stateText = res.status;
        if (stateText === null) stateText = STREAM_STATE_TEXT[stream.state] || stream.state || null;
        setField('state', stateText);
        var stateEl = st.fields.state;
        stateEl.className = 'pps-value' + (stream.state === 'stalled' ? ' pps-stalled' : '');
        st.root.setAttribute('data-status', res.status || '');
        st.root.setAttribute('data-stream-state', stream.state || '');
    }

    /* ---------- polling ---------- */

    function shouldPoll() {
        return st.open && !pageHidden() && !st.collapsed && !st.stoppedByServer;
    }

    function clampInterval(ms) {
        var n = Number(ms);
        if (!n || isNaN(n)) n = DEFAULT_POLL_MS;
        return Math.min(MAX_POLL_MS, Math.max(MIN_POLL_MS, n));
    }

    function startPolling() {
        if (st.polling) return;
        st.polling = true;
        st.generation++;
        tick(st.generation);
    }

    function stopPolling() {
        if (!st.polling && st.timer === null) return;
        st.polling = false;
        st.generation++;
        if (st.timer !== null) {
            clearTimeout(st.timer);
            st.timer = null;
        }
    }

    function schedule(gen, ms) {
        if (gen !== st.generation || !st.polling) return;
        st.timer = setTimeout(function () {
            st.timer = null;
            tick(gen);
        }, clampInterval(ms));
    }

    function tick(gen) {
        if (gen !== st.generation || !st.polling) return;
        if (!shouldPoll()) { stopPolling(); return; }
        if (st.inflight) { schedule(gen, DEFAULT_POLL_MS); return; }
        var api = getApiClient();
        if (!api || typeof api.ajax !== 'function') {
            schedule(gen, DEFAULT_POLL_MS);
            return;
        }
        st.inflight = true;
        st.requests++;
        var req;
        try {
            req = api.ajax({ type: 'GET', url: api.getUrl(ENDPOINT), dataType: 'json' });
        } catch (e) {
            st.inflight = false;
            warn('stats request threw', e);
            schedule(gen, DEFAULT_POLL_MS);
            return;
        }
        Promise.resolve(req).then(function (res) {
            st.inflight = false;
            if (gen !== st.generation || !st.polling) return; /* stale: player closed meanwhile */
            handle(res || {});
            schedule(gen, res && res.pollIntervalMs);
        }, function (err) {
            st.inflight = false;
            if (gen !== st.generation || !st.polling) return;
            warn('stats request failed', err && (err.status || err.message || err));
            schedule(gen, MAX_POLL_MS);
        });
    }

    function stopByServer() {
        st.stoppedByServer = true;
        stopPolling();
        hideOverlay();
    }

    function handle(res) {
        st.lastResponse = res;
        if (!res.playing) {
            st.notPlayingCount++;
            if (st.seenPlaying || st.notPlayingCount > STARTUP_GRACE_POLLS) {
                stopByServer();
            }
            return;
        }
        st.seenPlaying = true;
        st.notPlayingCount = 0;
        if (!res.isPhantom) {
            stopByServer();
            return;
        }
        render(res);
    }

    function setCollapsed(v) {
        st.collapsed = !!v;
        writeCollapsedPref(st.collapsed);
        applyLayout();
        reconcile();
    }

    /* ---------- lifecycle ---------- */

    function rearm() {
        st.stoppedByServer = false;
        st.seenPlaying = false;
        st.notPlayingCount = 0;
    }

    function hookVideo() {
        var v = findPlayerVideo();
        if (!v || v.__phantomPlayerStatsHooked) return;
        v.__phantomPlayerStatsHooked = true;
        var onMediaStart = function () {
            if (!st.open) return;
            if (st.stoppedByServer) {
                rearm();
                reconcile();
            }
        };
        v.addEventListener('loadstart', onMediaStart);
        v.addEventListener('playing', onMediaStart);
    }

    /* Collapsed + freshly opened player: ONE bounded request (never re-armed)
     * to learn whether this is a phantom item, so the collapsed pill can be
     * shown for re-expansion. Collapsed never polls. */
    function probeOnce() {
        var api = getApiClient();
        if (!api || typeof api.ajax !== 'function' || st.inflight) return;
        var gen = st.generation;
        st.inflight = true;
        st.probed = true;
        st.requests++;
        Promise.resolve(api.ajax({ type: 'GET', url: api.getUrl(ENDPOINT), dataType: 'json' })).then(function (res) {
            st.inflight = false;
            if (gen !== st.generation || !st.open) return;
            res = res || {};
            st.lastResponse = res;
            if (res.playing && res.isPhantom) render(res);
        }, function () {
            st.inflight = false;
        });
    }

    function reconcile() {
        if (shouldPoll()) {
            startPolling();
            return;
        }
        stopPolling();
        if (!(st.collapsed && st.open) || pageHidden()) return;
        if (st.lastResponse && st.lastResponse.playing && st.lastResponse.isPhantom) {
            showOverlay();
        } else if (!st.lastResponse && !st.probed) {
            probeOnce();
        }
    }

    function sync() {
        var open = isPlayerOpen();
        if (open && !st.open) {
            st.open = true;
            st.lastResponse = null;
            st.probed = false;
            rearm();
        } else if (!open && st.open) {
            st.open = false;
            stopPolling();
            hideOverlay();
        }
        if (st.open) hookVideo();
        if (st.root) applyLayout();
        reconcile();
    }

    var syncPending = null;
    function scheduleSync() {
        if (syncPending !== null) return;
        syncPending = setTimeout(function () {
            syncPending = null;
            sync();
        }, 150);
    }

    function start() {
        window.addEventListener('hashchange', sync);
        window.addEventListener('popstate', sync);
        window.addEventListener('resize', function () { if (st.root) applyLayout(); });
        document.addEventListener('visibilitychange', sync);
        if (typeof window.MutationObserver === 'function') {
            new window.MutationObserver(scheduleSync).observe(document.body, { childList: true, subtree: true });
        }
        sync();
    }

    /* Test/diagnostic surface (read-only state + a synchronous re-sync). */
    window.__phantomPlayerStats = {
        sync: sync,
        state: function () {
            return {
                open: st.open,
                polling: st.polling,
                inflight: st.inflight,
                collapsed: st.collapsed,
                stoppedByServer: st.stoppedByServer,
                requests: st.requests,
                visible: !!(st.root && st.root.style.display !== 'none'),
                timerArmed: st.timer !== null
            };
        },
        setCollapsed: setCollapsed
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
