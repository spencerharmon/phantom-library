/* Phantom Library — item-detail source controls.
 *
 * Loaded via Custom JS in Jellyfin's branding settings:
 *
 *     <script src="/web/ConfigurationPage?name=PhantomKebab" defer></script>
 *
 * Adds Phantom source controls to Phantom movie / episode detail pages and
 * to the kebab (...) action sheet. Server state remains authoritative; the
 * shim only renders controls for channel items whose stable item.ExternalId
 * parses as movie_<tmdb> or episode_<tmdb>_s<season>e<episode>.
 *
 * Also adds a per-user show/hide control to every Phantom detail page (movie,
 * series, season, episode) and its action sheet: the calling user hides or
 * unhides a title for THEMSELVES via the [Authorize] User/Hidden endpoints.
 * Hiding is title-level and movie/TV symmetric — a movie maps to
 * {movie, <tmdb>}; a series/season/episode all map to {series, <series tmdb>}
 * (the first numeric group of the ExternalId), so hiding any TV node hides the
 * whole series for that user.
 *
 * No external dependencies. No build step. Pure browser JS.
 */
(function () {
    'use strict';

    var TAG = '[PhantomLibrary]';
    var MATERIALISE_LABEL = 'Materialise (Phantom Library)';
    var REJECT_LABEL = 'Reject current source (Phantom Library)';
    var MATERIALISE_DATA_ID = 'phantom-materialise';
    var REJECT_DATA_ID = 'phantom-reject-current-source';
    var HIDE_LABEL = 'Hide from my library (Phantom Library)';
    var UNHIDE_LABEL = 'Unhide from my library (Phantom Library)';
    var HIDE_DATA_ID = 'phantom-hide';
    var UNHIDE_DATA_ID = 'phantom-unhide';
    var SECTION_ID = 'phantom-source-section';
    var VIS_SECTION_ID = 'phantom-visibility-section';
    var STYLE_ID = 'phantom-source-styles';

    function log() {
        // Quiet by default; uncomment for debugging.
        // console.log.apply(console, [TAG].concat([].slice.call(arguments)));
    }

    function warn() {
        console.warn.apply(console, [TAG].concat([].slice.call(arguments)));
    }

    /* Pull current item id from URL hash. Jellyfin's detail page URL is
     * always of the form .../#/details?id=<guid>&... */
    function currentItemId() {
        var hash = window.location.hash || '';
        var m = hash.match(/[?&]id=([0-9a-fA-F-]{32,36})/);
        return m ? m[1] : null;
    }

    /* Find the SPA's ApiClient. Jellyfin-web exposes it on window. */
    function getApiClient() {
        if (window.ApiClient) {
            return window.ApiClient;
        }
        // Newer builds wrap it; check ConnectionManager.
        if (window.connectionManager && window.connectionManager.currentApiClient) {
            return window.connectionManager.currentApiClient();
        }
        return null;
    }

    function getCurrentItem() {
        var api = getApiClient();
        var itemId = currentItemId();
        if (!api || !itemId || typeof api.getItem !== 'function') {
            return Promise.resolve(null);
        }
        var userId = (typeof api.getCurrentUserId === 'function') ? api.getCurrentUserId() : null;
        if (!userId) {
            return Promise.resolve(null);
        }
        try {
            return api.getItem(userId, itemId).then(function (item) {
                return item || null;
            }, function () { return null; });
        } catch (_) {
            return Promise.resolve(null);
        }
    }

    function parsePhantomExternalId(externalId) {
        if (typeof externalId !== 'string') { return null; }
        if (/^movie_\d+$/.test(externalId)) {
            return { kind: 'movie', materialisable: true };
        }
        if (/^episode_\d+_s\d+e\d+$/.test(externalId)) {
            return { kind: 'episode', materialisable: true };
        }
        return null;
    }

    function getPlayablePhantomItem() {
        return getCurrentItem().then(function (item) {
            if (!item || !item.ExternalId) { return null; }
            var parsed = parsePhantomExternalId(item.ExternalId);
            if (!parsed) { return null; }
            if (item.Type && item.Type !== 'Movie' && item.Type !== 'Episode') { return null; }
            return { item: item, externalId: item.ExternalId, parsed: parsed };
        });
    }

    /* Map any Phantom ExternalId to the per-user hide target the User/Hidden
     * endpoints accept. Hiding is title-level and movie/TV symmetric:
     *   movie_<tmdb>                  -> { type:'movie',  tmdbId:<tmdb> }
     *   series_<tmdb>                 -> { type:'series', tmdbId:<tmdb> }
     *   season_<tmdb>_s<NN>           -> { type:'series', tmdbId:<tmdb> }
     *   episode_<tmdb>_s<NN>e<NN>     -> { type:'series', tmdbId:<tmdb> }
     * so hiding any TV node (series/season/episode) hides the whole series for
     * the calling user. orphan_<hex> has no tmdb and is not hideable. */
    function parsePhantomHideTarget(externalId) {
        if (typeof externalId !== 'string') { return null; }
        var movie = externalId.match(/^movie_(\d+)$/);
        if (movie) { return { type: 'movie', tmdbId: parseInt(movie[1], 10) }; }
        var series = externalId.match(/^series_(\d+)$/);
        if (series) { return { type: 'series', tmdbId: parseInt(series[1], 10) }; }
        var seasonMatch = externalId.match(/^season_(\d+)_s\d+$/);
        if (seasonMatch) { return { type: 'series', tmdbId: parseInt(seasonMatch[1], 10) }; }
        var episode = externalId.match(/^episode_(\d+)_s\d+e\d+$/);
        if (episode) { return { type: 'series', tmdbId: parseInt(episode[1], 10) }; }
        return null;
    }

    /* Like getPlayablePhantomItem, but for the show/hide surface: accepts every
     * Phantom detail node (movie, series, season, episode) since hiding is
     * title-level, not just the materialisable movie/episode leaves. */
    function getHideablePhantomItem() {
        return getCurrentItem().then(function (item) {
            if (!item || !item.ExternalId) { return null; }
            var target = parsePhantomHideTarget(item.ExternalId);
            if (!target) { return null; }
            if (item.Type
                && item.Type !== 'Movie' && item.Type !== 'Episode'
                && item.Type !== 'Series' && item.Type !== 'Season') { return null; }
            return { item: item, externalId: item.ExternalId, target: target };
        });
    }

    function getPhantomSeasonItem() {
        return getCurrentItem().then(function (item) {
            if (!item || item.Type !== 'Season' || !item.ChannelId || !item.ExternalId) { return null; }
            if (!/^season_\d+_s\d+$/.test(item.ExternalId)) { return null; }
            return item;
        });
    }

    function prehydratePhantomSeasonChildren() {
        return getPhantomSeasonItem().then(function (item) {
            if (!item) { return; }
            var key = 'phantom-season-prehydrated:' + item.Id;
            if (window.sessionStorage && window.sessionStorage.getItem(key)) { return; }
            var api = getApiClient();
            if (!api || typeof api.getUrl !== 'function' || typeof api.ajax !== 'function') { return; }
            var url = api.getUrl('Channels/' + item.ChannelId + '/Items', {
                FolderId: item.Id,
                Fields: 'Tags,ProviderIds,Overview,ExternalId,ProductionYear,PremiereDate',
                Limit: 300
            });
            return api.ajax({ type: 'GET', url: url, dataType: 'json' }).then(function () {
                if (window.sessionStorage) { window.sessionStorage.setItem(key, '1'); }
                refreshVisibleItemContainers();
            }, function (err) {
                warn('season child prehydrate failed', err);
            });
        });
    }

    function refreshVisibleItemContainers() {
        var containers = document.querySelectorAll('.itemsContainer, [is="emby-itemscontainer"]');
        for (var i = 0; i < containers.length; i++) {
            var c = containers[i];
            try {
                if (typeof c.notifyRefreshNeeded === 'function') {
                    c.notifyRefreshNeeded(true);
                } else if (typeof c.refreshItems === 'function') {
                    c.refreshItems();
                }
            } catch (_) { /* best-effort */ }
        }
    }

    function apiUrl(path) {
        var api = getApiClient();
        if (!api || typeof api.getUrl !== 'function') { return null; }
        return api.getUrl(path);
    }

    function ajaxJson(options) {
        var api = getApiClient();
        if (!api || typeof api.ajax !== 'function') {
            return Promise.reject(new Error('ApiClient not found'));
        }
        return api.ajax(options);
    }

    function fetchSources(externalId) {
        var url = apiUrl('Plugins/PhantomLibrary/Items/' + encodeURIComponent(externalId) + '/Sources');
        if (!url) { return Promise.resolve(null); }
        return ajaxJson({
            type: 'GET',
            url: url,
            dataType: 'json'
        }).then(function (result) {
            return result || null;
        }, function (err) {
            warn('source lookup failed', err);
            return null;
        });
    }

    function extractErrorMessage(err) {
        if (!err) { return 'unknown error'; }
        var body = err.responseJSON;
        if (!body && typeof err.responseText === 'string' && err.responseText.length > 0) {
            try { body = JSON.parse(err.responseText); } catch (_) { /* not JSON */ }
        }
        if (body && typeof body === 'object') {
            var status = body.Status || body.status;
            var error = body.Error || body.error || body.Message || body.message;
            if (status && error) { return status + ': ' + error; }
            if (error) { return error; }
            if (status) { return status; }
        }
        if (typeof err.responseText === 'string' && err.responseText.length > 0
            && err.responseText.length < 500) {
            return err.responseText;
        }
        if (err.message) { return err.message; }
        if (err.statusText) {
            return 'HTTP ' + err.status + ' ' + err.statusText;
        }
        if (err.status) { return 'HTTP ' + err.status; }
        return 'unknown error';
    }

    function fireMaterialise(itemId) {
        var url = apiUrl('Plugins/PhantomLibrary/Materialise/' + itemId);
        if (!url) {
            warn('ApiClient not found; cannot fire materialise.');
            alert('Phantom Library: ApiClient not found. Reload page and try again.');
            return Promise.reject(new Error('ApiClient not found'));
        }
        log('POST', url);
        return ajaxJson({
            type: 'POST',
            url: url,
            dataType: 'json'
        }).then(function (result) {
            reportOutcome('materialise', result);
            return result;
        }, function (err) {
            warn('materialise failed', err);
            var msg = extractErrorMessage(err);
            alert('Phantom Library: materialise failed\n' + msg);
            throw err;
        });
    }

    function fireMaterialiseCandidate(externalId, candidate) {
        var url = apiUrl('Plugins/PhantomLibrary/Items/' + encodeURIComponent(externalId) + '/Sources/MaterialiseCandidate');
        if (!url) {
            alert('Phantom Library: ApiClient not found. Reload page and try again.');
            return Promise.reject(new Error('ApiClient not found'));
        }
        return ajaxJson({
            type: 'POST',
            url: url,
            dataType: 'json',
            contentType: 'application/json',
            data: JSON.stringify(candidateRequest(candidate))
        }).then(function (result) {
            reportOutcome('materialise selected source', result);
            return result;
        }, function (err) {
            warn('materialise candidate failed', err);
            alert('Phantom Library: materialise selected source failed\n' + extractErrorMessage(err));
            throw err;
        });
    }

    function fireRejectCurrent(externalId) {
        var url = apiUrl('Plugins/PhantomLibrary/Items/' + encodeURIComponent(externalId) + '/Sources/RejectCurrent');
        if (!url) {
            alert('Phantom Library: ApiClient not found. Reload page and try again.');
            return Promise.reject(new Error('ApiClient not found'));
        }
        return ajaxJson({
            type: 'POST',
            url: url,
            dataType: 'json',
            contentType: 'application/json'
        }).then(function (result) {
            reportOutcome('reject current source', result);
            return result;
        }, function (err) {
            warn('reject current source failed', err);
            alert('Phantom Library: reject current source failed\n' + extractErrorMessage(err));
            throw err;
        });
    }

    function hiddenPath(target) {
        return 'Plugins/PhantomLibrary/User/Hidden/'
            + encodeURIComponent(target.type) + '/' + encodeURIComponent(target.tmdbId);
    }

    /* GET the calling user's hidden state for a title. Returns the parsed
     * { tmdbId, type, hidden } body, or null if the lookup fails (treated as
     * "state unknown" by callers, which then render nothing). */
    function fetchHiddenState(target) {
        var url = apiUrl(hiddenPath(target));
        if (!url) { return Promise.resolve(null); }
        return ajaxJson({
            type: 'GET',
            url: url,
            dataType: 'json'
        }).then(function (result) {
            return result || null;
        }, function (err) {
            warn('hidden-state lookup failed', err);
            return null;
        });
    }

    /* POST to hide this title for the calling user (idempotent, 204). No
     * dataType: the endpoint returns an empty 204 body, so asking for JSON
     * would reject on the empty parse. */
    function fireHide(target) {
        var url = apiUrl(hiddenPath(target));
        if (!url) {
            alert('Phantom Library: ApiClient not found. Reload page and try again.');
            return Promise.reject(new Error('ApiClient not found'));
        }
        return ajaxJson({
            type: 'POST',
            url: url
        }).then(function (result) {
            return result;
        }, function (err) {
            warn('hide failed', err);
            alert('Phantom Library: hide failed\n' + extractErrorMessage(err));
            throw err;
        });
    }

    /* DELETE to unhide this title for the calling user (idempotent, 204). */
    function fireUnhide(target) {
        var url = apiUrl(hiddenPath(target));
        if (!url) {
            alert('Phantom Library: ApiClient not found. Reload page and try again.');
            return Promise.reject(new Error('ApiClient not found'));
        }
        return ajaxJson({
            type: 'DELETE',
            url: url
        }).then(function (result) {
            return result;
        }, function (err) {
            warn('unhide failed', err);
            alert('Phantom Library: unhide failed\n' + extractErrorMessage(err));
            throw err;
        });
    }

    function isHiddenState(state) {
        return !!(state && (state.Hidden || state.hidden));
    }

    function candidateRequest(candidate) {
        return {
            magnet: candidate.Magnet || candidate.magnet,
            infoHash: candidate.InfoHash || candidate.infoHash,
            indexer: candidate.Indexer || candidate.indexer,
            title: candidate.Title || candidate.title,
            size: candidate.Size || candidate.size,
            seeders: candidate.Seeders || candidate.seeders
        };
    }

    function reportOutcome(action, result) {
        log(action, result);
        var status = (result && (result.Status || result.status)) || 'Unknown';
        var fuse = (result && (result.FusePath || result.fusePath)) || '';
        var err = (result && (result.Error || result.error)) || '';
        var msg = 'Phantom Library: ' + action + ' — ' + status;
        if (err) { msg += '\n' + err; }
        if (fuse) { msg += '\n' + fuse; }
        alert(msg);
    }

    function ensureStyles() {
        if (document.getElementById(STYLE_ID)) { return; }
        var style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent = [
            '#phantom-source-section,#phantom-visibility-section{margin:1.25em 0;padding:1em;border:1px solid rgba(255,255,255,.18);border-radius:10px;}',
            '#phantom-source-section h2,#phantom-visibility-section h2{margin:.1em 0 .75em;font-size:1.2em;}',
            '.phantom-source-summary{margin:.4em 0 .9em;opacity:.92;}',
            '.phantom-source-row{display:flex;gap:.6em;align-items:center;flex-wrap:wrap;margin:.6em 0;}',
            '.phantom-source-row label{font-weight:600;}',
            '.phantom-source-select{min-height:44px;min-width:16em;max-width:100%;}',
            '.phantom-source-button{min-height:44px;padding:.65em 1em;border-radius:8px;touch-action:manipulation;}',
            '.phantom-source-button+ .phantom-source-button{margin-left:.4em;}',
            // Mobile browser viewport: stack the row, let controls fill the width,
            // drop the desktop min-width so the <select> never overflows a narrow
            // phone, and pin the <select> font to 16px so iOS Safari does not
            // auto-zoom the whole page when the picker gains focus.
            '@media (max-width: 600px){.phantom-source-row{display:block}.phantom-source-select{width:100%;min-width:0;font-size:16px;margin:.35em 0}.phantom-source-button{width:100%;margin:.35em 0}.phantom-source-button+ .phantom-source-button{margin-left:0}}'
        ].join('\n');
        document.head.appendChild(style);
    }

    function sourceSummary(source) {
        if (!source) { return 'No current source'; }
        if (typeof source === 'string') { return source; }
        var parts = [];
        var name = source.Summary || source.summary || source.Title || source.title || source.Name || source.name;
        var indexer = source.Indexer || source.indexer;
        var seeders = source.Seeders || source.seeders;
        var size = source.Size || source.size || source.SizeBytes || source.sizeBytes;
        var path = source.FusePath || source.fusePath || source.Path || source.path;
        if (name) { parts.push(name); }
        if (indexer) { parts.push(indexer); }
        if (seeders !== undefined && seeders !== null) { parts.push(seeders + ' seeders'); }
        if (size) { parts.push(formatBytes(size)); }
        if (path && parts.length === 0) { parts.push(path); }
        return parts.length ? parts.join(' · ') : 'Current source present';
    }

    function candidateId(candidate) {
        return candidate.Magnet || candidate.magnet || candidate.CandidateId || candidate.candidateId || candidate.Id || candidate.id || '';
    }

    function candidateSummary(candidate) {
        return sourceSummary(candidate);
    }

    function formatBytes(value) {
        var n = Number(value);
        if (!isFinite(n) || n <= 0) { return ''; }
        var units = ['B', 'KB', 'MB', 'GB', 'TB'];
        var idx = 0;
        while (n >= 1024 && idx < units.length - 1) {
            n = n / 1024;
            idx++;
        }
        return (idx >= 3 ? n.toFixed(1) : Math.round(n).toString()) + ' ' + units[idx];
    }

    function isMaterialisedState(state) {
        return !!(state && (state.Materialised || state.materialised || state.Current || state.current || state.CurrentSource || state.currentSource));
    }

    function canRejectState(state) {
        return !!(state && (state.CanRejectCurrent || state.canRejectCurrent || isMaterialisedState(state)));
    }

    function canMaterialiseState(state) {
        if (!state) { return false; }
        if (state.CanMaterialiseSelected !== undefined) { return !!state.CanMaterialiseSelected; }
        if (state.canMaterialiseSelected !== undefined) { return !!state.canMaterialiseSelected; }
        if (state.CanMaterialise !== undefined) { return !!state.CanMaterialise; }
        if (state.canMaterialise !== undefined) { return !!state.canMaterialise; }
        return !isMaterialisedState(state);
    }

    function candidateList(state) {
        var list = state && (state.Candidates || state.candidates);
        return Array.isArray(list) ? list : [];
    }

    function renderSourceSection(ctx, state) {
        if (!ctx || !state) {
            removeSourceSection();
            return;
        }
        ensureStyles();
        var existing = document.getElementById(SECTION_ID);
        if (existing && existing.dataset.externalId !== ctx.externalId) {
            existing.remove();
            existing = null;
        }

        var section = existing || document.createElement('section');
        section.id = SECTION_ID;
        section.dataset.externalId = ctx.externalId;
        section.setAttribute('aria-label', 'Phantom Source');
        section.innerHTML = '';

        var heading = document.createElement('h2');
        heading.textContent = 'Phantom Source';
        section.appendChild(heading);

        var summary = document.createElement('div');
        summary.className = 'phantom-source-summary';
        summary.textContent = 'Current: ' + sourceSummary(state.CurrentSource || state.currentSource || state.Current || state.current);
        section.appendChild(summary);

        var candidates = candidateList(state);
        var row = document.createElement('div');
        row.className = 'phantom-source-row';

        var label = document.createElement('label');
        label.setAttribute('for', 'phantom-source-candidates');
        label.textContent = 'Candidate source';
        row.appendChild(label);

        var select = document.createElement('select');
        select.id = 'phantom-source-candidates';
        select.className = 'phantom-source-select';
        select.disabled = candidates.length === 0;
        if (candidates.length === 0) {
            var empty = document.createElement('option');
            empty.value = '';
            empty.textContent = 'No alternate candidates available';
            select.appendChild(empty);
        } else {
            candidates.forEach(function (candidate) {
                var option = document.createElement('option');
                option.value = candidateId(candidate);
                option.textContent = candidateSummary(candidate);
                select.appendChild(option);
            });
        }
        row.appendChild(select);
        section.appendChild(row);

        var actions = document.createElement('div');
        actions.className = 'phantom-source-row';

        var materialise = document.createElement('button');
        materialise.type = 'button';
        materialise.className = 'raised button-submit phantom-source-button';
        materialise.textContent = 'Materialise selected source';
        materialise.disabled = candidates.length === 0;
        materialise.addEventListener('click', function () {
            if (!select.value) { return; }
            var selected = candidates.filter(function (candidate) { return candidateId(candidate) === select.value; })[0];
            if (!selected) { return; }
            materialise.disabled = true;
            fireMaterialiseCandidate(ctx.externalId, selected).then(refreshSourceSection, function () {
                materialise.disabled = false;
            });
        });
        actions.appendChild(materialise);

        var reject = document.createElement('button');
        reject.type = 'button';
        reject.className = 'raised phantom-source-button';
        reject.textContent = 'Reject current source';
        reject.disabled = !canRejectState(state);
        reject.addEventListener('click', function () {
            reject.disabled = true;
            fireRejectCurrent(ctx.externalId).then(refreshSourceSection, function () {
                reject.disabled = false;
            });
        });
        actions.appendChild(reject);
        section.appendChild(actions);

        if (!existing) {
            var host = findDetailsHost();
            if (host.firstChild) {
                host.insertBefore(section, host.firstChild);
            } else {
                host.appendChild(section);
            }
        }
    }

    function removeSourceSection() {
        var existing = document.getElementById(SECTION_ID);
        if (existing) { existing.remove(); }
    }

    function findDetailsHost() {
        return document.querySelector('.detailPageContent')
            || document.querySelector('.itemDetails')
            || document.querySelector('.detailSection')
            || document.querySelector('.page')
            || document.body;
    }

    /* True when a node lives inside either Phantom-owned section, so the
     * MutationObserver ignores our own DOM writes and never self-triggers. */
    function isInPhantomSection(node) {
        if (!node || node.nodeType !== 1 || !node.closest) { return false; }
        return !!(node.closest('#' + SECTION_ID) || node.closest('#' + VIS_SECTION_ID));
    }

    function refreshSourceSection() {
        var seenItemId = currentItemId();
        if (!seenItemId) {
            removeSourceSection();
            return Promise.resolve();
        }
        return getPlayablePhantomItem().then(function (ctx) {
            if (!ctx || currentItemId() !== seenItemId) {
                removeSourceSection();
                return;
            }
            return fetchSources(ctx.externalId).then(function (state) {
                if (currentItemId() !== seenItemId) { return; }
                renderSourceSection(ctx, state);
            });
        });
    }

    /* The per-user show/hide surface. A standalone section (distinct from the
     * source section) so it renders for EVERY Phantom node — movie, series,
     * season, episode — not just the materialisable movie/episode leaves. It
     * reuses the .phantom-source-* classes so it inherits the same touch sizing
     * and mobile responsive layout. */
    function renderVisibilitySection(ctx, hidden) {
        if (!ctx) {
            removeVisibilitySection();
            return;
        }
        ensureStyles();
        var existing = document.getElementById(VIS_SECTION_ID);
        if (existing && existing.dataset.externalId !== ctx.externalId) {
            existing.remove();
            existing = null;
        }

        var section = existing || document.createElement('section');
        section.id = VIS_SECTION_ID;
        section.dataset.externalId = ctx.externalId;
        section.setAttribute('aria-label', 'Phantom Visibility');
        section.innerHTML = '';

        var heading = document.createElement('h2');
        heading.textContent = 'Phantom Visibility';
        section.appendChild(heading);

        var summary = document.createElement('div');
        summary.className = 'phantom-source-summary';
        summary.textContent = hidden
            ? 'Hidden from your library'
            : 'Visible in your library';
        section.appendChild(summary);

        var actions = document.createElement('div');
        actions.className = 'phantom-source-row';

        var toggle = document.createElement('button');
        toggle.type = 'button';
        toggle.className = 'raised phantom-source-button phantom-visibility-button';
        toggle.textContent = hidden ? 'Unhide from my library' : 'Hide from my library';
        toggle.setAttribute('data-id', hidden ? UNHIDE_DATA_ID : HIDE_DATA_ID);
        toggle.addEventListener('click', function () {
            toggle.disabled = true;
            var op = hidden ? fireUnhide(ctx.target) : fireHide(ctx.target);
            op.then(refreshVisibilitySection, function () {
                toggle.disabled = false;
            });
        });
        actions.appendChild(toggle);
        section.appendChild(actions);

        if (!existing) {
            var host = findDetailsHost();
            var src = document.getElementById(SECTION_ID);
            if (src && src.parentNode === host) {
                host.insertBefore(section, src.nextSibling || null);
            } else if (host.firstChild) {
                host.insertBefore(section, host.firstChild);
            } else {
                host.appendChild(section);
            }
        }
    }

    function removeVisibilitySection() {
        var existing = document.getElementById(VIS_SECTION_ID);
        if (existing) { existing.remove(); }
    }

    function refreshVisibilitySection() {
        var seenItemId = currentItemId();
        if (!seenItemId) {
            removeVisibilitySection();
            return Promise.resolve();
        }
        return getHideablePhantomItem().then(function (ctx) {
            if (!ctx || currentItemId() !== seenItemId) {
                removeVisibilitySection();
                return;
            }
            return fetchHiddenState(ctx.target).then(function (state) {
                if (currentItemId() !== seenItemId) { return; }
                if (!state) {
                    removeVisibilitySection();
                    return;
                }
                renderVisibilitySection(ctx, isHiddenState(state));
            });
        });
    }

    function closeSheet(sheet) {
        var close = sheet.querySelector('.actionSheetCloseButton');
        if (close) {
            close.click();
            return;
        }
        var bd = document.querySelector('.dialogBackdropOpened');
        if (bd) { bd.click(); }
    }

    /* Watch for the kebab action-sheet opening and inject source entries.
     * The sheet is rendered into document.body as
     * <div class="actionSheet ..."> with .actionSheetContent inside.
     * Each existing entry is a <button class="listItem ..."> with a
     * .listItemBody > .listItemBodyText for its label. */
    function injectIntoSheet(sheet) {
        if (!sheet || sheet.dataset.phantomInjected === '1') {
            return;
        }
        var content = sheet.querySelector('.actionSheetContent') || sheet.querySelector('.actionSheetScroller') || sheet;
        if (!content) {
            return;
        }

        var itemId = currentItemId();
        if (!itemId) {
            return;
        }

        getPlayablePhantomItem().then(function (ctx) {
            if (!ctx) {
                sheet.dataset.phantomInjected = '1';
                return;
            }
            return fetchSources(ctx.externalId).then(function (state) {
                if (!state) {
                    sheet.dataset.phantomInjected = '1';
                    return;
                }
                if (isMaterialisedState(state) && canRejectState(state)) {
                    injectButton(sheet, content, REJECT_LABEL, REJECT_DATA_ID, 'block', function () {
                        closeSheet(sheet);
                        fireRejectCurrent(ctx.externalId).then(refreshSourceSection, function () { /* alert already shown */ });
                    });
                } else if (canMaterialiseState(state)) {
                    injectButton(sheet, content, MATERIALISE_LABEL, MATERIALISE_DATA_ID, 'cloud_download', function () {
                        closeSheet(sheet);
                        fireMaterialise(itemId).then(refreshSourceSection, function () { /* alert already shown */ });
                    });
                }
                sheet.dataset.phantomInjected = '1';
            });
        });
    }

    /* Inject the per-user show/hide entry into the kebab action sheet. Separate
     * from injectIntoSheet (its own dataset guard) so a series/season sheet —
     * which has no source entry — still gets a hide/unhide entry, and a
     * movie/episode sheet gets both. */
    function injectVisibilityIntoSheet(sheet) {
        if (!sheet || sheet.dataset.phantomVisInjected === '1') {
            return;
        }
        var content = sheet.querySelector('.actionSheetContent') || sheet.querySelector('.actionSheetScroller') || sheet;
        if (!content) {
            return;
        }

        var itemId = currentItemId();
        if (!itemId) {
            return;
        }

        getHideablePhantomItem().then(function (ctx) {
            if (!ctx) {
                sheet.dataset.phantomVisInjected = '1';
                return;
            }
            return fetchHiddenState(ctx.target).then(function (state) {
                if (!state) {
                    sheet.dataset.phantomVisInjected = '1';
                    return;
                }
                if (isHiddenState(state)) {
                    injectButton(sheet, content, UNHIDE_LABEL, UNHIDE_DATA_ID, 'visibility', function () {
                        closeSheet(sheet);
                        fireUnhide(ctx.target).then(refreshVisibilitySection, function () { /* alert already shown */ });
                    });
                } else {
                    injectButton(sheet, content, HIDE_LABEL, HIDE_DATA_ID, 'visibility_off', function () {
                        closeSheet(sheet);
                        fireHide(ctx.target).then(refreshVisibilitySection, function () { /* alert already shown */ });
                    });
                }
                sheet.dataset.phantomVisInjected = '1';
            });
        });
    }

    function injectButton(sheet, content, label, dataId, iconText, onClick) {
        if (content.querySelector('[data-id="' + dataId + '"]')) { return; }

        var template = content.querySelector('.listItem');
        var button;
        if (template) {
            button = template.cloneNode(true);
            var clone = button.cloneNode(true);
            content.appendChild(clone);
            button = clone;
            var labelEl = button.querySelector('.listItemBodyText') || button;
            labelEl.textContent = label;
            var secondaries = button.querySelectorAll('.listItemBodyText.secondary');
            secondaries.forEach(function (s) { s.remove(); });
            var icon = button.querySelector('.listItemIcon');
            if (icon) {
                icon.textContent = iconText;
            }
        } else {
            button = document.createElement('button');
            button.type = 'button';
            button.className = 'listItem listItem-button actionSheetMenuItem';
            button.textContent = label;
            content.appendChild(button);
        }

        button.setAttribute('data-id', dataId);
        button.style.minHeight = '44px';
        button.style.touchAction = 'manipulation';
        button.addEventListener('click', function (ev) {
            ev.preventDefault();
            ev.stopPropagation();
            onClick();
        }, true);
        log('injected action sheet button', dataId);
    }

    /* Single MutationObserver on body. When an action sheet appears,
     * inject our entry. Also refresh the details section after Jellyfin's
     * SPA swaps detail-page DOM fragments. */
    function start() {
        ensureStyles();
        refreshSourceSection();
        refreshVisibilitySection();
        prehydratePhantomSeasonChildren();
        window.addEventListener('hashchange', function () {
            window.setTimeout(refreshSourceSection, 50);
            window.setTimeout(refreshVisibilitySection, 50);
            window.setTimeout(prehydratePhantomSeasonChildren, 50);
            window.setTimeout(prehydratePhantomSeasonChildren, 500);
        });
        var scheduled = false;
        var observer = new MutationObserver(function (mutations) {
            var sawExternalDom = false;
            for (var i = 0; i < mutations.length; i++) {
                var target = mutations[i].target;
                if (isInPhantomSection(target)) {
                    continue;
                }
                var added = mutations[i].addedNodes;
                for (var j = 0; j < added.length; j++) {
                    var node = added[j];
                    if (node.nodeType !== 1) { continue; }
                    if (node.id === SECTION_ID || node.id === VIS_SECTION_ID || isInPhantomSection(node)) {
                        continue;
                    }
                    sawExternalDom = true;
                    if (node.classList && node.classList.contains('actionSheet')) {
                        injectIntoSheet(node);
                        injectVisibilityIntoSheet(node);
                    } else if (node.querySelector) {
                        var sheets = node.querySelectorAll('.actionSheet');
                        for (var k = 0; k < sheets.length; k++) {
                            injectIntoSheet(sheets[k]);
                            injectVisibilityIntoSheet(sheets[k]);
                        }
                    }
                }
            }
            if (sawExternalDom && !scheduled) {
                scheduled = true;
                window.setTimeout(function () {
                    scheduled = false;
                    refreshSourceSection();
                    refreshVisibilitySection();
                    prehydratePhantomSeasonChildren();
                }, 150);
            }
        });
        observer.observe(document.body, { childList: true, subtree: true });
        log('observer started');
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
