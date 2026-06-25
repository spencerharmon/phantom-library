'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');

const repoRoot = path.resolve(__dirname, '..', '..');
const scriptPath = path.join(repoRoot, 'src/Jellyfin.Plugin.PhantomLibrary/Configuration/phantomKebab.js');
const script = fs.readFileSync(scriptPath, 'utf8');

const itemId = 'df7a034eaf3de44e189609d6b04e52b3';
const userId = '8EB11AC1-9939-4621-896C-31D5CBA4951C';

function actionSheetHtml() {
  return `
    <div class="actionSheet">
      <div class="actionSheetContent">
        <div class="actionSheetScroller scrollY">
          <button is="emby-button" type="button" class="listItem listItem-button actionSheetMenuItem" data-id="play">
            <span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons play_arrow" aria-hidden="true"></span>
            <div class="listItemBody actionsheetListItemBody">
              <div class="listItemBodyText actionSheetItemText">Play</div>
              <div class="listItemBodyText secondary">Existing secondary text</div>
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
    getItem: async () => ({ Id: itemId, Type: 'Episode', ExternalId: 'episode_246_s2e4', ChannelId: '40ab6e9af516a84f46dcea7140855d88' }),
    getUrl: (urlPath) => `http://127.0.0.1:8096/${urlPath}`,
    ajax: async (options) => {
      requests.push({ type: options.type || 'GET', url: options.url, data: options.data || null });
      const url = options.url;
      if (url.includes(`/Items/${encodeURIComponent(itemId)}/Actions?userId=${encodeURIComponent(userId)}`)) {
        return [
          {
            Id: 'phantom.materialise',
            Name: 'Materialise (Phantom Library)',
            Icon: 'download',
            IsEnabled: true,
            ConfirmationText: 'Materialise selected Phantom item?'
          },
          {
            Id: 'phantom.reset',
            Name: 'Reset Phantom',
            Icon: 'restart_alt',
            IsEnabled: true
          }
        ];
      }
      if (url.includes(`/Items/${encodeURIComponent(itemId)}/Actions/phantom.materialise?userId=${encodeURIComponent(userId)}`)) {
        return { Status: 'Queued', RefreshItem: false };
      }
      if (url.includes(`/Items/${encodeURIComponent(itemId)}/Actions/phantom.reset?userId=${encodeURIComponent(userId)}`)) {
        return { Status: 'Reset', RefreshItem: false };
      }
      if (url.includes('/Plugins/PhantomLibrary/Items/episode_246_s2e4/Sources')) {
        return { Status: 'available', Candidates: [] };
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

  return { dom, requests, alerts, confirms, cleanup };
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

async function testExistingSheetInjectsIntoScrollerAndUsesItemActionApi() {
  const { dom, requests, alerts, confirms, cleanup } = createHarness(actionSheetHtml());
  try {
    const document = dom.window.document;
    const directButton = await waitFor(
      () => document.querySelector('#phantom-item-actions-section [data-action-id="phantom.materialise"]'),
      'direct Phantom item action button'
    );
    assert.equal(directButton.textContent.includes('Materialise (Phantom Library)'), true);

    const button = await waitFor(
      () => document.querySelector('.actionSheetScroller [data-id="phantom-action-phantom-materialise"]'),
      'Phantom materialise button inside action sheet scroller'
    );

    assert.equal(button.textContent.includes('Materialise (Phantom Library)'), true);
    assert.equal(document.querySelector('.actionSheetContent > [data-id="phantom-action-phantom-materialise"]'), null, 'button must not be appended outside scroller');
    assert.equal(document.querySelectorAll('[data-id="phantom-action-phantom-materialise"]').length, 1, 'button injected exactly once');
    assert.equal(requests.some((request) => request.url.includes(`/Items/${itemId}/Actions?userId=${encodeURIComponent(userId)}`)), true, 'GET /Items/{id}/Actions uses current user id');
    assert.equal(requests.some((request) => request.url.includes('/Plugins/PhantomLibrary/Items/') && request.url.includes('/Sources') && request.type === 'GET'), true, 'detail source panel can still refresh independently');

    button.dispatchEvent(new dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
    await waitFor(
      () => requests.some((request) => request.type === 'POST' && request.url.includes(`/Items/${itemId}/Actions/phantom.materialise?userId=${encodeURIComponent(userId)}`)),
      'POST /Items/{id}/Actions/{actionId}'
    );
    await waitFor(
      () => alerts.some((message) => message.includes('Phantom Library: phantom.materialise — Queued')),
      'action success alert'
    );
    await waitFor(
      () => requests.filter((request) => request.url.includes('/Plugins/PhantomLibrary/Items/') && request.url.includes('/Sources')).length >= 2,
      'source section refresh after action'
    );
    await new Promise((resolve) => setTimeout(resolve, 50));

    assert.deepEqual(confirms, ['Materialise selected Phantom item?']);
  } finally {
    cleanup();
  }
}

async function testDirectActionSectionWorksWithoutJellyfinKebab() {
  const { dom, requests, cleanup } = createHarness('');
  try {
    const document = dom.window.document;
    const button = await waitFor(
      () => document.querySelector('#phantom-item-actions-section [data-action-id="phantom.reset"]'),
      'direct Phantom reset action button without Jellyfin action sheet'
    );
    assert.equal(button.textContent.includes('Reset Phantom'), true);
    button.dispatchEvent(new dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
    await waitFor(
      () => requests.some((request) => request.type === 'POST' && request.url.includes(`/Items/${itemId}/Actions/phantom.reset?userId=${encodeURIComponent(userId)}`)),
      'direct action POST /Items/{id}/Actions/phantom.reset'
    );
    await waitFor(
      () => requests.filter((request) => request.url.includes(`/Items/${itemId}/Actions?userId=${encodeURIComponent(userId)}`)).length >= 2,
      'direct action section refresh after reset'
    );
    await new Promise((resolve) => setTimeout(resolve, 50));
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
      'Phantom reset button after dynamic action sheet insertion'
    );
  } finally {
    cleanup();
  }
}

(async () => {
  await testExistingSheetInjectsIntoScrollerAndUsesItemActionApi();
  await testDirectActionSectionWorksWithoutJellyfinKebab();
  await testDynamicallyAddedSheetInjectedByObserver();
  console.log('phantom kebab jsdom tests passed');
})().catch((err) => {
  console.error(err);
  process.exitCode = 1;
});
