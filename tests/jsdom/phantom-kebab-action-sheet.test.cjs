'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');

const repoRoot = path.resolve(__dirname, '..', '..');
const scriptPath = path.join(repoRoot, 'src/Jellyfin.Plugin.PhantomLibrary/Configuration/phantomKebab.js');
const script = fs.readFileSync(scriptPath, 'utf8');

const itemId = 'df7a034eaf3de44e189609d6b04e52b3';
const dashedItemId = 'df7a034e-af3d-e44e-1896-09d6b04e52b3';
const userId = '8EB11AC1-9939-4621-896C-31D5CBA4951C';
const channelId = '40ab6e9af516a84f46dcea7140855d88';
const mediaSourceId = '988fa383-1d88-426d-eaa4-8d2c2838110f';

function actionSheetHtml() {
  return `
    <div class="actionSheet">
      <div class="actionSheetContent">
        <div class="actionSheetScroller scrollY">
          <button is="emby-button" type="button" class="listItem listItem-button actionSheetMenuItem" data-id="metadata">
            <span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons edit" aria-hidden="true"></span>
            <div class="listItemBody actionsheetListItemBody">
              <div class="listItemBodyText actionSheetItemText">Edit metadata</div>
              <div class="listItemBodyText secondary">Default Jellyfin command</div>
            </div>
          </button>
        </div>
      </div>
    </div>`;
}

function createHarness(html) {
  const requests = [];
  const alerts = [];
  const confirms = [];
  const channelItem = {
    Id: dashedItemId,
    Type: 'Episode',
    Name: 'The Swamp',
    ExternalId: 'episode_246_s2e4',
    ChannelId: channelId,
    ServerId: 'test-server',
    MediaSources: [{ Id: mediaSourceId }]
  };
  const dom = new JSDOM(`<!doctype html><html><head></head><body><main class="detailPageContent"></main>${html}</body></html>`, {
    url: `http://127.0.0.1:8096/web/index.html#/details?id=${itemId}`,
    runScripts: 'dangerously',
    pretendToBeVisual: true
  });

  const intervals = [];
  const timeouts = [];
  const originalSetInterval = dom.window.setInterval.bind(dom.window);
  const originalSetTimeout = dom.window.setTimeout.bind(dom.window);
  dom.window.setInterval = (callback, delay, ...args) => {
    const id = originalSetInterval(callback, delay, ...args);
    intervals.push(id);
    return id;
  };
  dom.window.setTimeout = (callback, delay, ...args) => {
    const id = originalSetTimeout(callback, delay, ...args);
    timeouts.push(id);
    return id;
  };
  dom.window.alert = (message) => alerts.push(String(message));
  dom.window.confirm = (message) => {
    confirms.push(String(message));
    return true;
  };
  dom.window.console = console;
  dom.window.ApiClient = {
    getCurrentUserId: () => userId,
    getItem: async () => {
      throw new Error('native /Users/{userId}/Items/{channelItemId} lookup should be bypassed after channel cache');
    },
    getUrl: (urlPath) => `http://127.0.0.1:8096/${urlPath}`,
    ajax: async (options) => {
      requests.push({ type: options.type || 'GET', url: options.url, data: options.data || null });
      const url = options.url;
      if (url.includes(`/Channels/${channelId}/Items`)) {
        return { Items: [channelItem], TotalRecordCount: 1 };
      }
      if (url.includes(`/Items/${encodeURIComponent(itemId)}/Actions?userId=${encodeURIComponent(userId)}`)) {
        return [
          {
            Id: 'phantom.materialise',
            Name: 'Materialise Phantom',
            Icon: 'download',
            IsEnabled: true
          },
          {
            Id: 'phantom.reset',
            Name: 'Reset Phantom',
            Icon: 'restart_alt',
            IsEnabled: true,
            RequiresConfirmation: true,
            ConfirmationText: 'Reset Phantom state?'
          },
          {
            Id: 'phantom.rejectCurrent',
            Name: 'Reject current Phantom source',
            Icon: 'block',
            IsEnabled: true,
            RequiresConfirmation: true,
            ConfirmationText: 'Reject current source?'
          }
        ];
      }
      if (url.includes(`/Items/${encodeURIComponent(itemId)}/Actions/phantom.reset?userId=${encodeURIComponent(userId)}`)) {
        return { Status: 'Reset', RefreshItem: false };
      }
      if (url.includes(`/Items/${encodeURIComponent(itemId)}/Actions/phantom.rejectCurrent?userId=${encodeURIComponent(userId)}`)) {
        return { Status: 'Rejected', RefreshItem: false };
      }
      throw new Error(`Unexpected ajax URL: ${url}`);
    }
  };

  dom.window.eval(script);
  if (dom.window.document.readyState === 'loading') {
    dom.window.document.dispatchEvent(new dom.window.Event('DOMContentLoaded'));
  }

  const cleanup = () => {
    intervals.forEach((id) => dom.window.clearInterval(id));
    timeouts.forEach((id) => dom.window.clearTimeout(id));
    dom.window.close();
  };

  return { dom, requests, alerts, confirms, channelItem, cleanup };
}

async function waitFor(predicate, description) {
  const deadline = Date.now() + 1500;
  let lastError;
  while (Date.now() < deadline) {
    try {
      const value = predicate();
      if (value) {
        return value;
      }
    } catch (err) {
      lastError = err;
    }
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  if (lastError) {
    throw lastError;
  }
  throw new Error(`Timed out waiting for ${description}`);
}

async function testNativeActionSheetGetsOnlyResetAndRejectActions() {
  const { dom, requests, alerts, confirms, cleanup } = createHarness(actionSheetHtml());
  try {
    const document = dom.window.document;
    const reset = await waitFor(
      () => document.querySelector('.actionSheetScroller [data-id="phantom-action-phantom-reset"]'),
      'Phantom reset button inside native action sheet scroller'
    );
    const reject = await waitFor(
      () => document.querySelector('.actionSheetScroller [data-id="phantom-action-phantom-rejectCurrent"]'),
      'Phantom reject button inside native action sheet scroller'
    );

    assert.equal(document.querySelector('[data-id="metadata"]') !== null, true, 'default Jellyfin command remains present');
    assert.equal(document.querySelector('[data-id="phantom-action-phantom-materialise"]'), null, 'materialise action is not injected into item-page kebab');
    assert.equal(reset.textContent.includes('Reset Phantom'), true);
    assert.equal(reject.textContent.includes('Reject current Phantom source'), true);
    assert.equal(requests.some((request) => request.url.includes(`/Items/${itemId}/Actions?userId=${encodeURIComponent(userId)}`)), true, 'GET /Items/{id}/Actions uses current user id');

    reset.dispatchEvent(new dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
    await waitFor(
      () => requests.some((request) => request.type === 'POST' && request.url.includes(`/Items/${itemId}/Actions/phantom.reset?userId=${encodeURIComponent(userId)}`)),
      'POST /Items/{id}/Actions/phantom.reset'
    );
    await waitFor(
      () => alerts.some((message) => message.includes('Phantom Library: phantom.reset — Reset')),
      'action success alert'
    );

    assert.deepEqual(confirms, ['Reset Phantom state?']);
  } finally {
    cleanup();
  }
}

async function testDynamicallyAddedSheetInjectedByObserver() {
  const { dom, cleanup } = createHarness('');
  try {
    const document = dom.window.document;
    document.body.insertAdjacentHTML('beforeend', actionSheetHtml());
    await waitFor(
      () => document.querySelector('.actionSheetScroller [data-id="phantom-action-phantom-reset"]'),
      'Phantom reset button after dynamic native action sheet insertion'
    );
  } finally {
    cleanup();
  }
}

async function testChannelItemCacheMapsMediaSourceIdForNativeKebab() {
  const { dom, channelItem, cleanup } = createHarness('');
  try {
    const api = dom.window.ApiClient;
    await api.ajax({ type: 'GET', url: `http://127.0.0.1:8096/Channels/${channelId}/Items` });
    const item = await api.getItem(userId, mediaSourceId);
    assert.equal(item, channelItem);
  } finally {
    cleanup();
  }
}

(async () => {
  await testNativeActionSheetGetsOnlyResetAndRejectActions();
  await testDynamicallyAddedSheetInjectedByObserver();
  await testChannelItemCacheMapsMediaSourceIdForNativeKebab();
  console.log('phantom kebab jsdom tests passed');
})().catch((err) => {
  console.error(err);
  process.exitCode = 1;
});
