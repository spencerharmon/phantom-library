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
 * No external dependencies. No build step. Pure browser JS.
 */
(function () {
    'use strict';

    var TAG = '[PhantomLibrary]';
    var MATERIALISE_LABEL = 'Materialise (Phantom Library)';
    var RESET_LABEL = 'Reset Phantom';
    var REJECT_LABEL = 'Reject current source (Phantom Library)';
    var MATERIALISE_DATA_ID = 'phantom-materialise';
    var RESET_DATA_ID = 'phantom-reset';
    var REJECT_DATA_ID = 'phantom-reject-current-source';
    var SECTION_ID = 'phantom-source-section';
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
            if (!item) { return null; }
            if (item.Type && item.Type !== 'Movie' && item.Type !== 'Episode') { return null; }
            var parsed = parsePhantomExternalId(item.ExternalId);
            if (parsed) {
                return { item: item, externalId: item.ExternalId, parsed: parsed };
            }

            return resolveExternalId(currentItemId()).then(function (externalId) {
                parsed = parsePhantomExternalId(externalId);
                return parsed ? { item: item, externalId: externalId, parsed: parsed } : null;
            });
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

    function resolveExternalId(itemId) {
        if (!itemId) { return Promise.resolve(null); }
        var url = apiUrl('Plugins/PhantomLibrary/Items/ResolveExternalId/' + encodeURIComponent(itemId));
        if (!url) { return Promise.resolve(null); }
        return ajaxJson({
            type: 'GET',
            url: url,
            dataType: 'json'
        }).then(function (result) {
            return (result && (result.ExternalId || result.externalId)) || null;
        }, function (err) {
            warn('external id resolve failed', err);
            return null;
        });
    }

    function fetchItemActions(itemId) {
        var url = apiUrl('Items/' + encodeURIComponent(itemId) + '/Actions');
        if (!url) { return Promise.resolve([]); }
        return ajaxJson({
            type: 'GET',
            url: url,
            dataType: 'json'
        }).then(function (result) {
            return Array.isArray(result) ? result : [];
        }, function (err) {
            warn('item actions lookup failed', err);
            return [];
        });
    }

    function fireItemAction(itemId, actionId) {
        var url = apiUrl('Items/' + encodeURIComponent(itemId) + '/Actions/' + encodeURIComponent(actionId));
        if (!url) {
            alert('Phantom Library: ApiClient not found. Reload page and try again.');
            return Promise.reject(new Error('ApiClient not found'));
        }
        return ajaxJson({
            type: 'POST',
            url: url,
            dataType: 'json',
            contentType: 'application/json',
            data: JSON.stringify({})
        }).then(function (result) {
            reportOutcome(actionId, result);
            return result;
        }, function (err) {
            warn('item action failed', actionId, err);
            alert('Phantom Library: action failed\n' + extractErrorMessage(err));
            throw err;
        });
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

    function fireReset(externalId) {
        var url = apiUrl('Plugins/PhantomLibrary/Items/' + encodeURIComponent(externalId) + '/Sources/Reset');
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
            reportOutcome('reset phantom', result);
            return result;
        }, function (err) {
            warn('reset phantom failed', err);
            alert('Phantom Library: reset phantom failed\n' + extractErrorMessage(err));
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
        var status = (result && (result.Status || result.status || result.Code || result.code)) || 'Unknown';
        var fuse = (result && (result.FusePath || result.fusePath)) || '';
        var err = (result && (result.Error || result.error || result.Message || result.message)) || '';
        var msg = 'Phantom Library: ' + action + ' — ' + status;
        if (err) { msg += '\n' + err; }
        if (fuse) { msg += '\n' + fuse; }
        alert(msg);
    }

    function shouldRefreshItem(result) {
        if (!result) { return false; }
        if (result.RefreshItem !== undefined) { return !!result.RefreshItem; }
        if (result.refreshItem !== undefined) { return !!result.refreshItem; }
        if (result.RefreshItemAfterInvoke !== undefined) { return !!result.RefreshItemAfterInvoke; }
        if (result.refreshItemAfterInvoke !== undefined) { return !!result.refreshItemAfterInvoke; }
        return false;
    }

    function refreshClientAfterAction(result) {
        return refreshSourceSection().then(function () {
            if (!shouldRefreshItem(result)) { return; }
            window.setTimeout(function () {
                window.location.reload();
            }, 150);
        });
    }

    function ensureStyles() {
        if (document.getElementById(STYLE_ID)) { return; }
        var style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent = [
            '#phantom-source-section{margin:1.25em 0;padding:1em;border:1px solid rgba(255,255,255,.18);border-radius:10px;}',
            '#phantom-source-section h2{margin:.1em 0 .75em;font-size:1.2em;}',
            '.phantom-source-summary{margin:.4em 0 .9em;opacity:.92;}',
            '.phantom-source-row{display:flex;gap:.6em;align-items:center;flex-wrap:wrap;margin:.6em 0;}',
            '.phantom-source-row label{font-weight:600;}',
            '.phantom-source-select{min-height:44px;min-width:16em;max-width:100%;}',
            '.phantom-source-button{min-height:44px;padding:.65em 1em;border-radius:8px;touch-action:manipulation;}',
            '.phantom-source-button+ .phantom-source-button{margin-left:.4em;}',
            '@media (max-width: 600px){.phantom-source-row{display:block}.phantom-source-select,.phantom-source-button{width:100%;margin:.35em 0}.phantom-source-button+ .phantom-source-button{margin-left:0}}'
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

    function canResetState(state) {
        if (!state) { return false; }
        var status = state.Status || state.status || '';
        return isMaterialisedState(state) || status === 'materialised' || status === 'unavailable';
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

        var reset = document.createElement('button');
        reset.type = 'button';
        reset.className = 'raised phantom-source-button';
        reset.textContent = 'Reset Phantom';
        reset.disabled = !canResetState(state);
        reset.addEventListener('click', function () {
            if (!window.confirm('Reset Phantom state for this item? This does not reject the current source.')) { return; }
            reset.disabled = true;
            fireReset(ctx.externalId).then(refreshSourceSection, function () {
                reset.disabled = false;
            });
        });
        actions.appendChild(reset);

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

        fetchItemActions(itemId).then(function (actions) {
            if (!actions.length) {
                sheet.dataset.phantomInjected = '1';
                return;
            }
            actions.forEach(function (action) {
                var actionId = action.Id || action.id;
                var label = action.Name || action.name || actionId;
                var icon = action.Icon || action.icon || 'extension';
                var enabled = action.IsEnabled !== false && action.isEnabled !== false;
                var confirmText = action.ConfirmationText || action.confirmationText;
                if (!actionId || !enabled) { return; }
                injectButton(sheet, content, label, 'phantom-action-' + actionId.replace(/[^a-zA-Z0-9_-]/g, '-'), icon, function () {
                    closeSheet(sheet);
                    if (confirmText && !window.confirm(confirmText)) { return; }
                    fireItemAction(itemId, actionId).then(refreshClientAfterAction, function () { /* alert already shown */ });
                });
            });
            sheet.dataset.phantomInjected = '1';
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
        prehydratePhantomSeasonChildren();
        window.addEventListener('hashchange', function () {
            window.setTimeout(refreshSourceSection, 50);
            window.setTimeout(prehydratePhantomSeasonChildren, 50);
            window.setTimeout(prehydratePhantomSeasonChildren, 500);
        });
        var scheduled = false;
        var observer = new MutationObserver(function (mutations) {
            var sawExternalDom = false;
            for (var i = 0; i < mutations.length; i++) {
                var target = mutations[i].target;
                if (target && target.nodeType === 1 && target.closest && target.closest('#' + SECTION_ID)) {
                    continue;
                }
                var added = mutations[i].addedNodes;
                for (var j = 0; j < added.length; j++) {
                    var node = added[j];
                    if (node.nodeType !== 1) { continue; }
                    if (node.id === SECTION_ID || (node.closest && node.closest('#' + SECTION_ID))) {
                        continue;
                    }
                    sawExternalDom = true;
                    if (node.classList && node.classList.contains('actionSheet')) {
                        injectIntoSheet(node);
                    } else if (node.querySelector) {
                        var sheets = node.querySelectorAll('.actionSheet');
                        for (var k = 0; k < sheets.length; k++) {
                            injectIntoSheet(sheets[k]);
                        }
                    }
                }
            }
            if (sawExternalDom && !scheduled) {
                scheduled = true;
                window.setTimeout(function () {
                    scheduled = false;
                    refreshSourceSection();
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
