/* Phantom Library — list-view + detail-page badge overlay.
 *
 * Loaded via Custom JS in Jellyfin's branding settings, alongside
 * phantomKebab.js:
 *
 *     <script src="/Plugins/PhantomLibrary/badges.js" defer></script>
 *
 * Decorates Jellyfin UI with a small badge identifying items as
 * Phantom / Materialised / Unavailable. Items absent from the
 * plugin's phantom_items table (regular library content, gostream-
 * direct items, anything not registered as a phantom) are left
 * untouched.
 *
 * Badge placement
 * ---------------
 * Three placement contexts, each with its own DOM strategy:
 *
 *   1. Card grids (.card[data-id], e.g. Home, library posters):
 *      bottom-left corner of .cardImageContainer. Deliberately NOT
 *      inside .cardOverlayContainer so the hover overlay (favorite,
 *      mark-played, play buttons) renders on top and the badge does
 *      not block hover interactions on rest, and the badge is hidden
 *      under the overlay during hover.
 *
 *   2. List rows (.listItem[data-id], e.g. Episodes pane):
 *      bottom-left over .listItemImage when the row has an image
 *      thumbnail. Rows without an image are skipped (we deliberately
 *      do not stamp the badge onto the title text — that was the
 *      original v0.1 attempt and was unreadable).
 *
 *   3. Detail page (#/details?id=<guid>): exactly ONE inline pill
 *      appended to the .itemMiscInfo-primary metadata strip (next to
 *      rating / runtime / year). The detail page contains many
 *      [data-id] elements that reference the same item id (the page
 *      header, play button, favorite button, etc.); decorating each
 *      one duplicated the badge over the user-data buttons. This
 *      pass treats the misc-info strip as the single canonical
 *      insertion point and ignores everything else on the detail
 *      page.
 *
 * Server endpoint: POST /Plugins/PhantomLibrary/States
 *   request:  { "ids": ["<guid32>", ...] }
 *   response: { "<guid32>": "Phantom" | "Virtual" | "Materialised" | "Unavailable", ... }
 * Only GUIDs present in phantom_items are returned. Authentication: any
 * logged-in Jellyfin user (the host's DefaultPolicy).
 *
 * No external dependencies. No build step. Pure browser JS.
 */
(function () {
    'use strict';

    var TAG = '[PhantomLibrary/badges]';
    var DECORATED_ATTR = 'data-phantom-badge';        // value = state once applied
    var DETAIL_ATTR = 'data-phantom-detail-badge';    // misc-info strip marker (value = guid32)
    var DEBOUNCE_MS = 120;
    var BATCH_LIMIT = 400;                            // server splits at 500; keep margin
    var GUID_RE = /^[0-9a-fA-F]{32}$|^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;

    /* In-session cache: guid (32-hex, lowercase) → state string or null
     * (null = looked up, not a phantom). Avoids re-querying as cards
     * scroll in and out. */
    var stateCache = Object.create(null);

    /* Pending DOM elements awaiting decoration, keyed by guid. */
    var pending = Object.create(null);
    var pendingTimer = null;

    /* Pending detail-page injections: guid → true when we still need
     * to decorate the misc-info strip for that detail page. */
    var pendingDetail = Object.create(null);

    function warn() {
        console.warn.apply(console, [TAG].concat([].slice.call(arguments)));
    }

    function log() {
        // Quiet by default; flip to console.log for debugging.
        // console.log.apply(console, [TAG].concat([].slice.call(arguments)));
    }

    function getApiClient() {
        if (window.ApiClient) {
            return window.ApiClient;
        }
        if (window.connectionManager && window.connectionManager.currentApiClient) {
            return window.connectionManager.currentApiClient();
        }
        return null;
    }

    function normaliseGuid(s) {
        if (!s) return null;
        if (!GUID_RE.test(s)) return null;
        return s.replace(/-/g, '').toLowerCase();
    }

    /* Extract the current detail page item id from the URL hash.
     * Jellyfin detail URLs are #/details?id=<guid>&... */
    function currentDetailItemId() {
        var hash = window.location.hash || '';
        if (hash.indexOf('/details') === -1) return null;
        var m = hash.match(/[?&]id=([0-9a-fA-F-]{32,36})/);
        return m ? normaliseGuid(m[1]) : null;
    }

    /* Inject the CSS rules once. */
    function ensureStyles() {
        if (document.getElementById('phantom-library-badge-css')) return;
        var css = '' +
            /* Corner badge (cards + list-item images). Positioned
             * bottom-left so it sits clear of the play button
             * (bottom-right) and user-data buttons (top-right). z-index
             * deliberately low so .cardOverlayContainer (z=2) covers it
             * on hover, and pointer-events: none so it never steals
             * clicks from the underlying image link. */
            '.phantom-badge {' +
            '  position: absolute;' +
            '  bottom: 6px;' +
            '  left: 6px;' +
            '  z-index: 1;' +
            '  padding: 2px 6px;' +
            '  font-size: 10px;' +
            '  font-weight: 600;' +
            '  line-height: 1.2;' +
            '  letter-spacing: 0.4px;' +
            '  text-transform: uppercase;' +
            '  border-radius: 3px;' +
            '  color: #fff;' +
            '  background: rgba(124, 58, 237, 0.92);' + /* violet */
            '  box-shadow: 0 1px 2px rgba(0,0,0,0.4);' +
            '  pointer-events: none;' +
            '  user-select: none;' +
            '}' +
            '.phantom-badge.phantom-state-Materialised {' +
            '  background: rgba(16, 185, 129, 0.92);' + /* emerald */
            '}' +
            '.phantom-badge.phantom-state-Unavailable {' +
            '  background: rgba(107, 114, 128, 0.92);' + /* slate-gray */
            '}' +
            /* Inline pill for the detail-page misc-info strip. Stays
             * in normal flow so it sits next to the rating / runtime
             * spans. */
            '.phantom-badge--inline {' +
            '  position: static;' +
            '  display: inline-flex;' +
            '  align-items: center;' +
            '  margin: 0 0.6em;' +
            '  vertical-align: middle;' +
            '  pointer-events: auto;' +
            '  cursor: default;' +
            '}';
        var style = document.createElement('style');
        style.id = 'phantom-library-badge-css';
        style.type = 'text/css';
        style.appendChild(document.createTextNode(css));
        document.head.appendChild(style);
    }

    function labelFor(state) {
        switch (state) {
            case 'Materialised':
                return 'Materialised';
            case 'Unavailable':
                return 'Unavailable';
            case 'Phantom':
            case 'Virtual':
            default:
                return 'Phantom';
        }
    }

    function makeBadge(state, inline) {
        var badge = document.createElement('span');
        badge.className = 'phantom-badge phantom-state-' + state +
            (inline ? ' phantom-badge--inline' : '');
        badge.textContent = labelFor(state);
        badge.title = 'Phantom Library: ' + labelFor(state);
        return badge;
    }

    /* Place a corner badge on a card or list-item element. */
    function placeBadge(el, state) {
        // Already decorated with the right state? Skip.
        var current = el.getAttribute(DECORATED_ATTR);
        if (current === state) return;

        // Remove any stale badge before reinserting (handles state
        // transitions, e.g. Phantom → Materialised after install).
        var stale = el.querySelectorAll(':scope .phantom-badge');
        for (var i = 0; i < stale.length; i++) stale[i].remove();

        var host = null;
        if (el.classList && el.classList.contains('card')) {
            // Inside .cardImageContainer but NOT .cardOverlayContainer:
            // the overlay holds the user-data + play buttons and must
            // stay visually on top during hover. We pick the first
            // .cardImageContainer that isn't itself the overlay.
            var containers = el.querySelectorAll(':scope .cardImageContainer');
            for (var j = 0; j < containers.length; j++) {
                var c = containers[j];
                if (c.classList.contains('cardOverlayContainer')) continue;
                if (c.closest('.cardOverlayContainer')) continue;
                host = c;
                break;
            }
        } else if (el.classList && el.classList.contains('listItem')) {
            // Only decorate when there is an image thumbnail; injecting
            // anything into .listItemBody overlaps the title text and
            // is what the previous revision got wrong.
            host = el.querySelector(':scope .listItemImage');
        }

        if (!host) {
            // No suitable host — record the lookup result so we don't
            // re-evaluate this element repeatedly, but don't draw.
            el.setAttribute(DECORATED_ATTR, state);
            return;
        }

        // Ensure host can anchor an absolutely positioned child.
        var pos = window.getComputedStyle(host).position;
        if (pos === 'static' || !pos) {
            host.style.position = 'relative';
        }
        host.appendChild(makeBadge(state, /*inline*/ false));
        el.setAttribute(DECORATED_ATTR, state);
    }

    /* Place a single inline badge into the detail-page misc-info
     * strip for the given guid. Returns true on successful injection,
     * false if the strip is not yet in the DOM. */
    function placeDetailBadge(guid, state) {
        // Find a misc-info strip that is currently visible and tagged
        // for this guid (or untagged). Jellyfin keeps detail pages
        // mounted under .page.itemDetailPage; only the active one has
        // .is-active so we restrict our search to active pages to
        // avoid decorating stale background pages.
        var pages = document.querySelectorAll('.page.itemDetailPage:not(.hide), .itemDetailPage:not(.hide)');
        var found = false;
        for (var p = 0; p < pages.length; p++) {
            var page = pages[p];
            var strip = page.querySelector('.itemMiscInfo-primary')
                || page.querySelector('.itemMiscInfo.itemMiscInfo-primary')
                || page.querySelector('.itemMiscInfo');
            if (!strip) continue;

            // Already decorated this strip for this guid?
            if (strip.getAttribute(DETAIL_ATTR) === guid) {
                // Confirm the badge actually exists; if the strip was
                // re-rendered Jellyfin may have wiped our child.
                if (strip.querySelector(':scope > .phantom-badge--inline')) {
                    found = true;
                    continue;
                }
            }

            // Wipe any prior badge on this strip (stale guid or
            // re-render) before injecting.
            var prior = strip.querySelectorAll(':scope > .phantom-badge--inline');
            for (var q = 0; q < prior.length; q++) prior[q].remove();

            strip.appendChild(makeBadge(state, /*inline*/ true));
            strip.setAttribute(DETAIL_ATTR, guid);
            found = true;
        }
        return found;
    }

    /* Walks a subtree and collects every card / list-item with a
     * GUID-shaped data-id. Deliberately narrow: we do NOT scoop
     * arbitrary [data-id] elements (action buttons, emby-userdata
     * controls, etc.) — those share the detail page's item id and
     * caused the duplicate-badge-over-buttons bug. */
    function collectCandidates(root, sink) {
        if (!root || root.nodeType !== 1) return;
        var SEL = '.card[data-id], .listItem[data-id]';
        if (root.matches && root.matches(SEL)) {
            sink.push(root);
        }
        if (root.querySelectorAll) {
            var nodes = root.querySelectorAll(SEL);
            for (var i = 0; i < nodes.length; i++) {
                sink.push(nodes[i]);
            }
        }
    }

    /* Queue candidate elements for batched lookup + decoration. */
    function process(els) {
        var needFetch = false;
        for (var i = 0; i < els.length; i++) {
            var el = els[i];
            var raw = el.getAttribute('data-id');
            var guid = normaliseGuid(raw);
            if (!guid) continue;
            var existing = el.getAttribute(DECORATED_ATTR);
            if (guid in stateCache) {
                var cached = stateCache[guid];
                if (cached && existing !== cached) {
                    placeBadge(el, cached);
                } else if (!cached && existing) {
                    // Was decorated, but state is now "no phantom" —
                    // strip it. (Rare; cache resets on reload.)
                    var old = el.querySelectorAll(':scope .phantom-badge');
                    for (var k = 0; k < old.length; k++) old[k].remove();
                    el.removeAttribute(DECORATED_ATTR);
                }
                continue;
            }
            if (!pending[guid]) pending[guid] = [];
            pending[guid].push(el);
            needFetch = true;
        }
        if (needFetch) scheduleFlush();
    }

    /* Try to decorate the current detail page; queues a lookup if
     * the state isn't cached yet. */
    function processDetail() {
        var guid = currentDetailItemId();
        if (!guid) return;
        if (guid in stateCache) {
            var state = stateCache[guid];
            if (state) {
                placeDetailBadge(guid, state);
            }
            return;
        }
        pendingDetail[guid] = true;
        // Push into the regular pending bucket so it joins the next
        // batched POST.
        if (!pending[guid]) pending[guid] = [];
        scheduleFlush();
    }

    function scheduleFlush() {
        if (pendingTimer) return;
        pendingTimer = setTimeout(flush, DEBOUNCE_MS);
    }

    function flush() {
        pendingTimer = null;
        var ids = Object.keys(pending);
        if (ids.length === 0) return;

        var api = getApiClient();
        if (!api) {
            pendingTimer = setTimeout(flush, 500);
            return;
        }

        for (var i = 0; i < ids.length; i += BATCH_LIMIT) {
            var chunk = ids.slice(i, i + BATCH_LIMIT);
            sendBatch(api, chunk);
        }
        pending = Object.create(null);
    }

    function sendBatch(api, chunk) {
        // Snapshot the pending element lists per id before clearing.
        var snapshot = Object.create(null);
        for (var i = 0; i < chunk.length; i++) {
            snapshot[chunk[i]] = (pending[chunk[i]] || []).slice();
        }

        var url = api.getUrl('Plugins/PhantomLibrary/States');
        api.ajax({
            type: 'POST',
            url: url,
            contentType: 'application/json',
            data: JSON.stringify({ ids: chunk }),
            dataType: 'json'
        }).then(function (result) {
            if (!result || typeof result !== 'object') {
                warn('unexpected /States response', result);
                return;
            }
            for (var i = 0; i < chunk.length; i++) {
                var g = chunk[i];
                var state = Object.prototype.hasOwnProperty.call(result, g) ? result[g] : null;
                stateCache[g] = state;
                if (state) {
                    var els = snapshot[g] || [];
                    for (var j = 0; j < els.length; j++) {
                        if (els[j].isConnected) {
                            placeBadge(els[j], state);
                        }
                    }
                    if (pendingDetail[g]) {
                        // Try to inject into detail page; if the strip
                        // isn't in the DOM yet, leave the flag so the
                        // mutation observer retries on next render.
                        if (placeDetailBadge(g, state)) {
                            delete pendingDetail[g];
                        }
                    }
                }
            }
        }, function (err) {
            warn('states lookup failed', err);
        });
    }

    /* Initial sweep + body-wide MutationObserver for new cards as the
     * user scrolls / navigates. */
    function start() {
        ensureStyles();

        var initial = [];
        collectCandidates(document.body, initial);
        if (initial.length) process(initial);
        processDetail();

        var observer = new MutationObserver(function (mutations) {
            var batch = [];
            var sawSubtreeChange = false;
            for (var i = 0; i < mutations.length; i++) {
                var m = mutations[i];
                if (m.type === 'childList') {
                    sawSubtreeChange = true;
                    var added = m.addedNodes;
                    for (var j = 0; j < added.length; j++) {
                        collectCandidates(added[j], batch);
                    }
                } else if (m.type === 'attributes' && m.attributeName === 'data-id') {
                    // data-id rewritten on an existing card (Jellyfin
                    // recycles card DOM nodes). Re-evaluate.
                    if (m.target && m.target.nodeType === 1) {
                        m.target.removeAttribute(DECORATED_ATTR);
                        var oldBadge = m.target.querySelectorAll(':scope .phantom-badge');
                        for (var k = 0; k < oldBadge.length; k++) oldBadge[k].remove();
                        batch.push(m.target);
                    }
                }
            }
            if (batch.length) process(batch);
            // Detail page may have just rendered or transitioned —
            // re-attempt detail-badge placement on any DOM mutation.
            if (sawSubtreeChange) processDetail();
        });
        observer.observe(document.body, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ['data-id']
        });

        // Hash navigation: re-trigger detail placement (and forget
        // the previous page's pending entry so we don't try to
        // decorate a strip that's been torn down).
        window.addEventListener('hashchange', function () {
            processDetail();
        });

        log('observer started');
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
