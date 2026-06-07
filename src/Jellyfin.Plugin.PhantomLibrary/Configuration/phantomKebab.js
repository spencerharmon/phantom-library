/* Phantom Library — item-detail "Materialise" button shim.
 *
 * Loaded via Custom JS in Jellyfin's branding settings:
 *
 *     <script src="/web/ConfigurationPage?name=PhantomKebab" defer></script>
 *
 * Adds a "Materialise" entry to the item kebab (...) action sheet on any
 * Movie / Episode detail page. Clicking it POSTs to
 *     /Plugins/PhantomLibrary/Materialise/{itemId}
 * with the user's existing API token from the SPA's apiclient.
 *
 * No external dependencies. No build step. Pure browser JS.
 */
(function () {
    'use strict';

    var TAG = '[PhantomLibrary]';
    var ACTION_LABEL = 'Materialise (Phantom Library)';
    var ACTION_DATA_ID = 'phantom-materialise';

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

    function fireMaterialise(itemId) {
        var api = getApiClient();
        if (!api) {
            warn('ApiClient not found; cannot fire materialise.');
            alert('Phantom Library: ApiClient not found. Reload page and try again.');
            return;
        }
        var url = api.getUrl('Plugins/PhantomLibrary/Materialise/' + itemId);
        log('POST', url);
        api.ajax({
            type: 'POST',
            url: url,
            dataType: 'json'
        }).then(function (result) {
            log('result', result);
            var status = (result && result.Status) || 'Unknown';
            var fuse = (result && result.FusePath) || '';
            alert('Phantom Library: ' + status + (fuse ? '\n' + fuse : ''));
        }, function (err) {
            warn('materialise failed', err);
            var msg = (err && err.statusText) || ('HTTP ' + (err && err.status));
            alert('Phantom Library: materialise failed (' + msg + ')');
        });
    }

    /* Watch for the kebab action-sheet opening and inject our entry.
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

        // Skip if no item id (sheet might be opened for non-item contexts).
        var itemId = currentItemId();
        if (!itemId) {
            return;
        }

        // Find an existing entry to clone for consistent styling.
        var template = content.querySelector('.listItem');
        var button;
        if (template) {
            button = template.cloneNode(true);
            // Strip event handlers + reset any state.
            var clone = button.cloneNode(true);
            content.appendChild(clone);
            button = clone;
            // Replace label.
            var labelEl = button.querySelector('.listItemBodyText') || button;
            labelEl.textContent = ACTION_LABEL;
            // Remove any secondary lines (artist, runtime, etc.) the template
            // may have brought along.
            var secondaries = button.querySelectorAll('.listItemBodyText.secondary');
            secondaries.forEach(function (s) { s.remove(); });
            // Replace icon if present with a generic download glyph.
            var icon = button.querySelector('.listItemIcon');
            if (icon) {
                icon.textContent = 'cloud_download';
            }
        } else {
            // No template — build a minimal button.
            button = document.createElement('button');
            button.type = 'button';
            button.className = 'listItem listItem-button actionSheetMenuItem';
            button.textContent = ACTION_LABEL;
            content.appendChild(button);
        }

        button.setAttribute('data-id', ACTION_DATA_ID);
        button.addEventListener('click', function (ev) {
            ev.preventDefault();
            ev.stopPropagation();
            // Close the sheet first so user sees the alert.
            var close = sheet.querySelector('.actionSheetCloseButton');
            if (close) {
                close.click();
            } else {
                // Click overlay backdrop if present.
                var bd = document.querySelector('.dialogBackdropOpened');
                if (bd) { bd.click(); }
            }
            fireMaterialise(itemId);
        }, true);

        sheet.dataset.phantomInjected = '1';
        log('injected materialise button into action sheet');
    }

    /* Single MutationObserver on body. When an action sheet appears,
     * inject our entry. */
    function start() {
        var observer = new MutationObserver(function (mutations) {
            for (var i = 0; i < mutations.length; i++) {
                var added = mutations[i].addedNodes;
                for (var j = 0; j < added.length; j++) {
                    var node = added[j];
                    if (node.nodeType !== 1) { continue; }
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
