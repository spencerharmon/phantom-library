/* Phantom Library — Home-screen Netflix-style shelves.
 *
 * Injected into jellyfin-web/index.html by the fork image (the same
 * mechanism as phantomKebab.js / phantomBadges.js — Jellyfin 10.11.x
 * BrandingOptions exposes CustomCss but no CustomJs, so a plugin-served
 * <script> is baked into index.html before </body>):
 *
 *     <script src="/Plugins/PhantomLibrary/shelves.js" defer></script>
 *
 * Renders the plugin's curated categories (Available now, New releases,
 * Trending, Popular, genre rows, …) as titled horizontal scroll rows at
 * the top of the Home screen — replacing the rejected clickable category
 * FOLDERS inside the Phantom Movies / Phantom Shows channels. Each card
 * navigates to the item's native detail page (#/details?id=<guid>), whose
 * Play button drives the normal materialise-on-play path.
 *
 * Server endpoint: GET /Plugins/PhantomLibrary/Shelves
 *   response: { "Rows": [ { "Key", "Title",
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

    function log() { try { console.log.apply(console, [TAG].concat([].slice.call(arguments))); } catch (e) {} }
    function warn() { try { console.warn.apply(console, [TAG].concat([].slice.call(arguments))); } catch (e) {} }

    function getApiClient() {
        if (window.ApiClient) { return window.ApiClient; }
        if (window.connectionManager && window.connectionManager.currentApiClient) {
            return window.connectionManager.currentApiClient();
        }
        return null;
    }

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
            '#' + SENTINEL_ID + ' .phantomShelfRow { margin: 0 0 1.6em 0; }' +
            '#' + SENTINEL_ID + ' .phantomShelfTitle {' +
            '  margin: 0 0 .4em 3.3%; font-size: 1.25em; font-weight: 600; }' +
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
        var section = document.createElement('div');
        section.className = 'phantomShelfRow';
        section.setAttribute('data-phantom-row', row.Key || '');

        var title = document.createElement('h2');
        title.className = 'phantomShelfTitle';
        title.textContent = row.Title || '';
        section.appendChild(title);

        var scroller = document.createElement('div');
        scroller.className = 'phantomShelfScroller';
        for (var i = 0; i < row.Items.length; i++) {
            scroller.appendChild(buildCard(row.Items[i]));
        }
        section.appendChild(scroller);
        return section;
    }

    function render(container, payload) {
        // Idempotent: never inject twice into the same container.
        if (container.querySelector('#' + SENTINEL_ID)) { return; }
        var rows = (payload && payload.Rows) || [];
        if (!rows.length) { return; }

        ensureStyles();
        var wrap = document.createElement('div');
        wrap.id = SENTINEL_ID;
        var built = 0;
        for (var i = 0; i < rows.length; i++) {
            var el = buildRow(rows[i]);
            if (el) { wrap.appendChild(el); built++; }
        }
        if (!built) { return; }

        // Insert above the native home sections (Continue Watching / Next Up /
        // Latest) so the curated shelves lead the Home screen.
        container.insertBefore(wrap, container.firstChild);
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
