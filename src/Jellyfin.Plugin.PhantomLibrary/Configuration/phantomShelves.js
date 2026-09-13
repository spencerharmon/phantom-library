/* Phantom Library — Home-screen Netflix-style shelves.
 *
 * Injected into jellyfin-web/index.html by the fork image (the same
 * mechanism as phantomKebab.js / phantomBadges.js — Jellyfin 10.11.x
 * BrandingOptions exposes CustomCss but no CustomJs, so a plugin-served
 * <script> is baked into index.html before </body>):
 *
 *     <script src="/Plugins/PhantomLibrary/shelves.js" defer></script>
 *
 * Renders the plugin's curated categories as titled horizontal scroll rows at
 * the top of the Home screen. Each category now yields a SEPARATE Movies rail
 * and TV rail (server-side, home-shelves-split-tv-movie), and the server
 * returns a per-user CURATED SUBSET of those rails ranked by what the user
 * normally watches (home-shelves-per-user-curation). A Movies/TV filter toggle
 * (persisted per browser) lets the user restrict the Home page to one media
 * type. Each card navigates to the item's native detail page
 * (#/details?id=<guid>), whose Play button drives the materialise-on-play path.
 *
 * Server endpoint: GET /Plugins/PhantomLibrary/Shelves
 *   response: { "Rows": [ { "Key", "Category", "Title", "MediaType"("Movie"|"Series"),
 *                           "Items": [ { "Id", "Name", "ImageUrl", "Type" } ] } ] }
 * Bounded (row-size capped, O(recent)) — never an O(catalogue) Home scan.
 * Authentication: any logged-in Jellyfin user (the host's DefaultPolicy).
 *
 * No external dependencies. No build step. Pure browser JS.
 */
(function () {
    'use strict';

    var TAG = '[PhantomLibrary/shelves]';
    var SENTINEL_ID = 'phantom-shelves';         // our injected container id
    var HOME_CONTAINER = '.homeSectionsContainer'; // exists only on the Home view
    var REINJECT_DEBOUNCE_MS = 200;
    var CACHE_TTL_MS = 60 * 1000;                 // reuse the payload across quick re-nav
    var FILTER_KEY = 'phantom-shelves-filter';    // 'all' | 'movie' | 'tv'

    function log() { try { console.log.apply(console, [TAG].concat([].slice.call(arguments))); } catch (e) {} }
    function warn() { try { console.warn.apply(console, [TAG].concat([].slice.call(arguments))); } catch (e) {} }

    function getApiClient() {
        if (window.ApiClient) { return window.ApiClient; }
        if (window.connectionManager && window.connectionManager.currentApiClient) {
            return window.connectionManager.currentApiClient();
        }
        return null;
    }

    /* Persisted Movies/TV filter (per browser). */
    function getFilter() {
        try {
            var v = window.localStorage.getItem(FILTER_KEY);
            if (v === 'movie' || v === 'tv') { return v; }
        } catch (e) {}
        return 'all';
    }

    function setFilter(v) {
        try { window.localStorage.setItem(FILTER_KEY, v); } catch (e) {}
    }

    /* MediaType ("Movie"|"Series") -> filter token ('movie'|'tv'). */
    function mediaToken(mediaType) { return mediaType === 'Series' ? 'tv' : 'movie'; }
    function mediaLabel(mediaType) { return mediaType === 'Series' ? 'TV' : 'Movies'; }

    /* In-session cache of the shelves payload (short TTL). */
    var payloadCache = null;
    var payloadCacheAt = 0;
    var inFlight = null;

    function fetchShelves() {
        var now = Date.now();
        if (payloadCache && (now - payloadCacheAt) < CACHE_TTL_MS) {
            return Promise.resolve(payloadCache);
        }
        if (inFlight) { return inFlight; }
        var api = getApiClient();
        if (!api) { return Promise.reject(new Error('no ApiClient')); }
        var url = api.getUrl('Plugins/PhantomLibrary/Shelves');
        inFlight = api.ajax({ type: 'GET', url: url, dataType: 'json' }).then(function (result) {
            payloadCache = result;
            payloadCacheAt = Date.now();
            inFlight = null;
            return result;
        }, function (err) {
            inFlight = null;
            throw err;
        });
        return inFlight;
    }

    function ensureStyles() {
        if (document.getElementById('phantom-shelves-css')) { return; }
        var css = '' +
            '#' + SENTINEL_ID + ' { margin: 0 0 1em 0; }' +
            '#' + SENTINEL_ID + ' .phantomShelfFilter {' +
            '  display: flex; gap: .4em; margin: .2em 3.3% 1.1em; }' +
            '#' + SENTINEL_ID + ' .phantomShelfFilterBtn {' +
            '  appearance: none; border: 1px solid rgba(255,255,255,.25); background: transparent;' +
            '  color: inherit; font: inherit; font-size: .82em; padding: .32em .95em;' +
            '  border-radius: 2em; cursor: pointer; opacity: .78; transition: all .12s ease; }' +
            '#' + SENTINEL_ID + ' .phantomShelfFilterBtn:hover { opacity: 1; }' +
            '#' + SENTINEL_ID + ' .phantomShelfFilterBtn.is-active {' +
            '  background: #00a4dc; border-color: #00a4dc; color: #fff; opacity: 1; font-weight: 600; }' +
            '#' + SENTINEL_ID + ' .phantomShelfRow { margin: 0 0 1.6em 0; }' +
            '#' + SENTINEL_ID + ' .phantomShelfRow.is-hidden { display: none; }' +
            '#' + SENTINEL_ID + ' .phantomShelfTitle {' +
            '  margin: 0 0 .4em 3.3%; font-size: 1.25em; font-weight: 600;' +
            '  display: flex; align-items: baseline; gap: .5em; }' +
            '#' + SENTINEL_ID + ' .phantomShelfType {' +
            '  font-size: .58em; font-weight: 600; letter-spacing: .04em; text-transform: uppercase;' +
            '  padding: .18em .55em; border-radius: 1em; opacity: .9; }' +
            '#' + SENTINEL_ID + ' .phantomShelfType--movie { background: rgba(0,164,220,.22); color: #4fc3e8; }' +
            '#' + SENTINEL_ID + ' .phantomShelfType--tv { background: rgba(170,120,255,.22); color: #b79bff; }' +
            '#' + SENTINEL_ID + ' .phantomShelfScroller {' +
            '  display: flex; flex-wrap: nowrap; overflow-x: auto; overflow-y: hidden;' +
            '  gap: .6em; padding: .2em 3.3%; scroll-behavior: smooth;' +
            '  scrollbar-width: thin; -webkit-overflow-scrolling: touch; }' +
            '#' + SENTINEL_ID + ' .phantomCard {' +
            '  flex: 0 0 auto; width: 8.6em; text-decoration: none; color: inherit;' +
            '  display: block; }' +
            '#' + SENTINEL_ID + ' .phantomCardPoster {' +
            '  width: 100%; aspect-ratio: 2 / 3; border-radius: .35em;' +
            '  background: #23262e center/cover no-repeat; box-shadow: 0 2px 6px rgba(0,0,0,.4);' +
            '  transition: transform .12s ease; }' +
            '#' + SENTINEL_ID + ' .phantomCard:hover .phantomCardPoster { transform: scale(1.045); }' +
            '#' + SENTINEL_ID + ' .phantomShelfEmpty {' +
            '  margin: .4em 3.3% 0; opacity: .6; font-size: .9em; }' +
            '#' + SENTINEL_ID + ' .phantomCardName {' +
            '  margin: .3em 0 0; font-size: .82em; line-height: 1.2; opacity: .85;' +
            '  overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }';
        var style = document.createElement('style');
        style.id = 'phantom-shelves-css';
        style.textContent = css;
        document.head.appendChild(style);
    }

    function serverId() {
        var api = getApiClient();
        try { return api && api.serverId ? api.serverId() : null; } catch (e) { return null; }
    }

    function detailsHref(id) {
        var sid = serverId();
        var href = '#/details?id=' + encodeURIComponent(id);
        if (sid) { href += '&serverId=' + encodeURIComponent(sid); }
        return href;
    }

    function buildCard(item) {
        var a = document.createElement('a');
        a.className = 'phantomCard';
        a.href = detailsHref(item.Id);
        a.setAttribute('title', item.Name || '');
        a.setAttribute('data-phantom-type', item.Type || '');

        var poster = document.createElement('div');
        poster.className = 'phantomCardPoster';
        if (item.ImageUrl) {
            poster.style.backgroundImage = 'url("' + String(item.ImageUrl).replace(/"/g, '%22') + '")';
        }
        a.appendChild(poster);

        var name = document.createElement('div');
        name.className = 'phantomCardName';
        name.textContent = item.Name || '';
        a.appendChild(name);
        return a;
    }

    function buildRow(row) {
        if (!row || !row.Items || !row.Items.length) { return null; }
        var token = mediaToken(row.MediaType);
        var section = document.createElement('div');
        section.className = 'phantomShelfRow';
        section.setAttribute('data-phantom-row', row.Key || '');
        section.setAttribute('data-media-type', token);

        var title = document.createElement('h2');
        title.className = 'phantomShelfTitle';
        var titleText = document.createElement('span');
        titleText.textContent = row.Title || '';
        title.appendChild(titleText);
        // Persistent Movies/TV pill so the two type variants of a category are
        // never ambiguous even in the unfiltered "All" view.
        var typeChip = document.createElement('span');
        typeChip.className = 'phantomShelfType phantomShelfType--' + token;
        typeChip.textContent = mediaLabel(row.MediaType);
        title.appendChild(typeChip);
        section.appendChild(title);

        var scroller = document.createElement('div');
        scroller.className = 'phantomShelfScroller';
        for (var i = 0; i < row.Items.length; i++) {
            scroller.appendChild(buildCard(row.Items[i]));
        }
        section.appendChild(scroller);
        return section;
    }

    /* Show/hide rails to match the active filter; report how many are visible. */
    function applyFilter(wrap, filter) {
        var rows = wrap.querySelectorAll('.phantomShelfRow');
        var visible = 0;
        for (var i = 0; i < rows.length; i++) {
            var t = rows[i].getAttribute('data-media-type');
            var show = filter === 'all' || t === filter;
            if (show) { rows[i].classList.remove('is-hidden'); visible++; }
            else { rows[i].classList.add('is-hidden'); }
        }
        var empty = wrap.querySelector('.phantomShelfEmpty');
        if (empty) { empty.style.display = visible === 0 ? '' : 'none'; }
        return visible;
    }

    function buildFilterBar(wrap) {
        var bar = document.createElement('div');
        bar.className = 'phantomShelfFilter';
        var current = getFilter();
        var opts = [['all', 'All'], ['movie', 'Movies'], ['tv', 'TV']];
        var buttons = [];

        function activate(val) {
            setFilter(val);
            for (var b = 0; b < buttons.length; b++) {
                if (buttons[b].getAttribute('data-filter') === val) { buttons[b].classList.add('is-active'); }
                else { buttons[b].classList.remove('is-active'); }
            }
            applyFilter(wrap, val);
        }

        for (var i = 0; i < opts.length; i++) {
            (function (val, label) {
                var btn = document.createElement('button');
                btn.type = 'button';
                btn.className = 'phantomShelfFilterBtn' + (val === current ? ' is-active' : '');
                btn.setAttribute('data-filter', val);
                btn.textContent = label;
                btn.addEventListener('click', function () { activate(val); });
                buttons.push(btn);
                bar.appendChild(btn);
            })(opts[i][0], opts[i][1]);
        }
        return bar;
    }

    function render(container, payload) {
        // Idempotent: never inject twice into the same container.
        if (container.querySelector('#' + SENTINEL_ID)) { return; }
        var rows = (payload && payload.Rows) || [];
        if (!rows.length) { return; }

        ensureStyles();
        var wrap = document.createElement('div');
        wrap.id = SENTINEL_ID;

        // Filter toggle leads the block.
        wrap.appendChild(buildFilterBar(wrap));

        var built = 0;
        var hasMovie = false;
        var hasTv = false;
        for (var i = 0; i < rows.length; i++) {
            var el = buildRow(rows[i]);
            if (el) {
                wrap.appendChild(el);
                built++;
                if (el.getAttribute('data-media-type') === 'tv') { hasTv = true; } else { hasMovie = true; }
            }
        }
        if (!built) { return; }

        // Empty-state note shown only when the active filter hides everything.
        var empty = document.createElement('div');
        empty.className = 'phantomShelfEmpty';
        empty.style.display = 'none';
        empty.textContent = 'No shelves for this filter.';
        wrap.appendChild(empty);

        // Hide the toggle entirely if only one media type is present (nothing to filter).
        if (!(hasMovie && hasTv)) {
            var bar = wrap.querySelector('.phantomShelfFilter');
            if (bar) { bar.style.display = 'none'; }
        }

        // Insert above the native home sections (Continue Watching / Next Up /
        // Latest) so the curated shelves lead the Home screen.
        container.insertBefore(wrap, container.firstChild);
        applyFilter(wrap, getFilter());
        log('rendered', built, 'shelves');
    }

    var reinjectTimer = null;
    function maybeInject() {
        var container = document.querySelector(HOME_CONTAINER);
        if (!container) { return; }                       // not on Home
        if (container.querySelector('#' + SENTINEL_ID)) { return; } // already done
        fetchShelves().then(function (payload) {
            // Re-resolve the container: the SPA may have re-rendered Home while
            // the request was in flight.
            var live = document.querySelector(HOME_CONTAINER);
            if (live) { render(live, payload); }
        }, function (err) {
            warn('shelves fetch failed', err);
        });
    }

    function scheduleInject() {
        if (reinjectTimer) { return; }
        reinjectTimer = setTimeout(function () {
            reinjectTimer = null;
            maybeInject();
        }, REINJECT_DEBOUNCE_MS);
    }

    function start() {
        // Initial attempt (covers a direct Home load).
        scheduleInject();

        // The SPA swaps views without a full reload; watch for the Home
        // container (re)appearing and re-inject. Bounded work: we bail
        // immediately once our sentinel is present.
        var observer = new MutationObserver(function () { scheduleInject(); });
        observer.observe(document.body, { childList: true, subtree: true });

        // Hash navigation back to Home.
        window.addEventListener('hashchange', function () { scheduleInject(); });

        log('shelves shim started');
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
