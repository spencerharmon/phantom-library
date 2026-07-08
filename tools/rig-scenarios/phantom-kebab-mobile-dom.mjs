/*
 * Mobile-viewport DOM/API test evidence for the Phantom Library source-management
 * UX (REQ-M14-MOBILE).
 *
 * A mobile *browser* loads the exact same jellyfin-web SPA as desktop, so the exact
 * same custom-JS shim (`src/.../Configuration/phantomKebab.js`) runs and injects the
 * same source controls. There is no separate "mobile" code path — the mobile concern
 * is purely (a) touch-sized controls, (b) a responsive layout at a phone viewport,
 * and (c) tap-driven flows (the kebab/"..." action sheet is the primary mobile
 * affordance). This harness proves those by *executing the real shim* against a
 * faithful minimal DOM sized to a phone viewport and asserting the resulting DOM +
 * the API calls each tap fires.
 *
 * Self-contained: no npm deps, no network, no Jellyfin/.NET build. Run directly:
 *
 *     node tools/rig-scenarios/phantom-kebab-mobile-dom.mjs
 *
 * Exits non-zero (and prints the failing assertions) on any regression.
 *
 * It deliberately mocks Jellyfin's ApiClient with the real endpoint shapes from
 * src/.../Api/PhantomLibraryController.cs and the DTOs in
 * src/.../Sources/PhantomSourceManager.cs, so the asserted request URLs/bodies match
 * what the server actually exposes.
 */

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const SHIM_URL = new URL(
    '../../src/Jellyfin.Plugin.PhantomLibrary/Configuration/phantomKebab.js',
    import.meta.url,
);
const SHIM_SRC = readFileSync(fileURLToPath(SHIM_URL), 'utf8');

/* --- tiny assertion harness ------------------------------------------------ */
const failures = [];
let checks = 0;
function ok(cond, msg) {
    checks++;
    if (!cond) {
        failures.push(msg);
        console.error('  FAIL: ' + msg);
    } else {
        console.log('  ok:   ' + msg);
    }
}

/* --- minimal but faithful DOM --------------------------------------------- */
/* Implements exactly the browser surface phantomKebab.js touches. */

function parseCompound(sel) {
    // Supports: tag, #id, .class (repeatable), [attr], [attr="v"], [attr='v'].
    const tokens = [];
    const re = /([a-zA-Z][\w-]*)|#([\w-]+)|\.([\w-]+)|\[([^\]=]+)(?:=(?:"([^"]*)"|'([^']*)'))?\]/g;
    let m;
    while ((m = re.exec(sel)) !== null) {
        if (m[1]) tokens.push({ t: 'tag', v: m[1].toLowerCase() });
        else if (m[2]) tokens.push({ t: 'id', v: m[2] });
        else if (m[3]) tokens.push({ t: 'class', v: m[3] });
        else if (m[4]) tokens.push({ t: 'attr', name: m[4], v: m[5] ?? m[6] ?? null });
    }
    return tokens;
}

function matchesCompound(el, tokens) {
    if (el.nodeType !== 1) return false;
    for (const tk of tokens) {
        if (tk.t === 'tag' && el.tagName.toLowerCase() !== tk.v) return false;
        if (tk.t === 'id' && el.id !== tk.v) return false;
        if (tk.t === 'class' && !el.classList.contains(tk.v)) return false;
        if (tk.t === 'attr') {
            if (!el.attributes.has(tk.name)) return false;
            if (tk.v !== null && el.attributes.get(tk.name) !== tk.v) return false;
        }
    }
    return true;
}

function matchesSelector(el, selector) {
    return selector.split(',').some((g) => {
        const toks = parseCompound(g.trim());
        return toks.length > 0 && matchesCompound(el, toks);
    });
}

class El {
    constructor(tagName, doc) {
        this.tagName = String(tagName).toUpperCase();
        this.ownerDocument = doc;
        this.nodeType = 1;
        this.childNodes = [];
        this.parentNode = null;
        this.id = '';
        this._classSet = new Set();
        this.dataset = {};
        this.attributes = new Map();
        this.style = {};
        this._ownText = '';
        this._listeners = {};
        this.disabled = false;
        this.value = '';
        this.type = '';
    }

    get className() {
        return Array.from(this._classSet).join(' ');
    }
    set className(v) {
        this._classSet = new Set(String(v).split(/\s+/).filter(Boolean));
    }
    get classList() {
        const set = this._classSet;
        return {
            contains: (c) => set.has(c),
            add: (...cs) => cs.forEach((c) => set.add(c)),
            remove: (...cs) => cs.forEach((c) => set.delete(c)),
        };
    }

    get firstChild() {
        return this.childNodes[0] || null;
    }

    get textContent() {
        return this._ownText + this.childNodes.map((c) => c.textContent).join('');
    }
    set textContent(v) {
        this.childNodes.slice().forEach((c) => this.removeChild(c));
        this._ownText = String(v);
    }

    set innerHTML(v) {
        // Only the empty-string clear form is used by the shim.
        this.childNodes.slice().forEach((c) => this.removeChild(c));
        this._ownText = String(v);
    }

    setAttribute(name, value) {
        this.attributes.set(name, String(value));
        if (name === 'id') this.id = String(value);
    }
    getAttribute(name) {
        return this.attributes.has(name) ? this.attributes.get(name) : null;
    }

    appendChild(child) {
        if (child.parentNode) child.parentNode.removeChild(child);
        child.parentNode = this;
        this.childNodes.push(child);
        if (this.ownerDocument) this.ownerDocument._recordMutation(this, [child], []);
        return child;
    }
    insertBefore(child, ref) {
        if (!ref) return this.appendChild(child);
        if (child.parentNode) child.parentNode.removeChild(child);
        const i = this.childNodes.indexOf(ref);
        child.parentNode = this;
        if (i < 0) this.childNodes.push(child);
        else this.childNodes.splice(i, 0, child);
        if (this.ownerDocument) this.ownerDocument._recordMutation(this, [child], []);
        return child;
    }
    removeChild(child) {
        const i = this.childNodes.indexOf(child);
        if (i >= 0) this.childNodes.splice(i, 1);
        child.parentNode = null;
        return child;
    }
    remove() {
        if (this.parentNode) this.parentNode.removeChild(this);
    }

    _walk(acc) {
        for (const c of this.childNodes) {
            if (c.nodeType === 1) {
                acc.push(c);
                c._walk(acc);
            }
        }
        return acc;
    }
    querySelectorAll(selector) {
        return this._walk([]).filter((el) => matchesSelector(el, selector));
    }
    querySelector(selector) {
        return this.querySelectorAll(selector)[0] || null;
    }
    closest(selector) {
        let n = this;
        while (n && n.nodeType === 1) {
            if (matchesSelector(n, selector)) return n;
            n = n.parentNode;
        }
        return null;
    }

    cloneNode() {
        const c = new El(this.tagName, this.ownerDocument);
        c.id = this.id;
        c._classSet = new Set(this._classSet);
        c.dataset = { ...this.dataset };
        c.attributes = new Map(this.attributes);
        c.style = { ...this.style };
        c._ownText = this._ownText;
        c.disabled = this.disabled;
        c.value = this.value;
        c.type = this.type;
        for (const ch of this.childNodes) {
            const cc = ch.cloneNode(true);
            cc.parentNode = c;
            c.childNodes.push(cc);
        }
        return c;
    }

    addEventListener(type, fn) {
        (this._listeners[type] = this._listeners[type] || []).push(fn);
    }
    _dispatch(type) {
        const ev = {
            type,
            preventDefault() {},
            stopPropagation() {},
            target: this,
        };
        (this._listeners[type] || []).slice().forEach((fn) => fn(ev));
    }
    click() {
        this._dispatch('click');
    }
}

class FakeDocument {
    constructor() {
        this.readyState = 'complete';
        this._observers = [];
        this.documentElement = new El('html', this);
        this.head = new El('head', this);
        this.body = new El('body', this);
        this.documentElement.appendChild(this.head);
        this.documentElement.appendChild(this.body);
    }
    createElement(tag) {
        return new El(tag, this);
    }
    _all() {
        return this.documentElement._walk([]);
    }
    getElementById(id) {
        return this._all().find((el) => el.id === id) || null;
    }
    querySelectorAll(selector) {
        return this._all().filter((el) => matchesSelector(el, selector));
    }
    querySelector(selector) {
        return this.querySelectorAll(selector)[0] || null;
    }
    addEventListener() {}
    _recordMutation(target, added, removed) {
        for (const entry of this._observers) {
            const inScope =
                entry.root === target ||
                (entry.opts.subtree && isAncestor(entry.root, target));
            if (!inScope) continue;
            entry.records.push({ type: 'childList', target, addedNodes: added, removedNodes: removed });
            if (!entry.scheduled) {
                entry.scheduled = true;
                queueMicrotask(() => {
                    entry.scheduled = false;
                    const recs = entry.records.splice(0);
                    if (recs.length) entry.obs.cb(recs, entry.obs);
                });
            }
        }
    }
}

function isAncestor(root, node) {
    let n = node.parentNode;
    while (n) {
        if (n === root) return true;
        n = n.parentNode;
    }
    return false;
}

class FakeMutationObserver {
    constructor(cb) {
        this.cb = cb;
    }
    observe(root, opts) {
        root.ownerDocument._observers.push({ obs: this, root, opts: opts || {}, records: [], scheduled: false });
    }
    disconnect() {}
}

function makeSessionStorage() {
    const m = new Map();
    return {
        getItem: (k) => (m.has(k) ? m.get(k) : null),
        setItem: (k, v) => m.set(k, String(v)),
    };
}

/* --- API mock (real endpoint shapes) -------------------------------------- */
function makeApi(itemGuid, item, sourcesState) {
    const calls = [];
    return {
        calls,
        getCurrentUserId: () => 'user-0001',
        getItem: (userId, id) => {
            calls.push({ kind: 'getItem', userId, id });
            return Promise.resolve(item);
        },
        getUrl: (path, params) => {
            let u = '/jellyfin/' + path;
            if (params) {
                const q = Object.keys(params)
                    .map((k) => encodeURIComponent(k) + '=' + encodeURIComponent(params[k]))
                    .join('&');
                if (q) u += '?' + q;
            }
            return u;
        },
        ajax: (opts) => {
            calls.push({ kind: 'ajax', type: opts.type, url: opts.url, data: opts.data, contentType: opts.contentType });
            if (/\/Sources$/.test(opts.url)) return Promise.resolve(sourcesState);
            if (/\/Sources\/MaterialiseCandidate$/.test(opts.url)) {
                return Promise.resolve({ Status: 'materialised', Code: 'materialised', Message: 'Source materialised', FusePath: '/fuse/new.mkv' });
            }
            if (/\/Sources\/RejectCurrent$/.test(opts.url)) {
                return Promise.resolve({ Status: 'rejected', Code: 'rejected', Message: 'Current source rejected' });
            }
            if (/\/Materialise\//.test(opts.url)) {
                return Promise.resolve({ Status: 'queued', Message: 'Materialise queued', FusePath: '' });
            }
            if (/Channels\//.test(opts.url)) return Promise.resolve({ Items: [] });
            return Promise.resolve({});
        },
    };
}

/* --- environment factory (fresh per scenario) ----------------------------- */
function makeEnv({ guid, item, sourcesState }) {
    const document = new FakeDocument();
    // A phantom detail page host, as jellyfin-web renders it.
    const detailHost = document.createElement('div');
    detailHost.className = 'detailPageContent';
    document.body.appendChild(detailHost);

    const api = makeApi(guid, item, sourcesState);
    const alerts = [];
    const window = {
        // Phone viewport (iPhone 12-ish CSS px). matchMedia mirrors the shim's
        // `@media (max-width: 600px)` breakpoint so the mobile rules are the
        // active ones for this run.
        innerWidth: 390,
        innerHeight: 844,
        matchMedia: (q) => ({ media: q, matches: /max-width:\s*600px/.test(q) && 390 <= 600 }),
        location: { hash: '#/details?id=' + guid + '&context=home' },
        ApiClient: api,
        sessionStorage: makeSessionStorage(),
        addEventListener: () => {},
        setTimeout: (fn, ms) => setTimeout(fn, ms),
        clearTimeout: (h) => clearTimeout(h),
    };
    const alert = (m) => alerts.push(m);
    return { document, window, api, alerts, alert, detailHost };
}

function loadShim(env) {
    // Run the real shim IIFE with our fakes bound in place of the browser globals
    // it references (window, document, console, alert, navigator, MutationObserver).
    const runner = new Function(
        'window',
        'document',
        'console',
        'alert',
        'navigator',
        'MutationObserver',
        SHIM_SRC,
    );
    runner(env.window, env.document, console, env.alert, {}, FakeMutationObserver);
}

const delay = (ms) => new Promise((r) => setTimeout(r, ms));
async function settle(n = 12) {
    for (let i = 0; i < n; i++) await delay(0);
}

/* --- shared fixtures ------------------------------------------------------- */
function materialisedState(externalId, type, tmdb, season, episode) {
    return {
        ExternalId: externalId,
        Type: type,
        TmdbId: tmdb,
        Season: season ?? null,
        Episode: episode ?? null,
        Status: 'Materialised',
        CurrentSource: {
            Magnet: 'magnet:?xt=urn:btih:CURRENT',
            InfoHash: 'CURRENT',
            Indexer: 'Prowlarr',
            Seeders: 14,
            Size: 2147483648,
            StubPath: '/stubs/cur.mkv',
            FusePath: '/fuse/cur.mkv',
            MaterialisedAt: '2026-01-01T00:00:00Z',
        },
        Candidates: [
            {
                Magnet: 'magnet:?xt=urn:btih:AAA2160', InfoHash: 'AAA2160', Indexer: 'Prowlarr',
                Title: 'Alt 2160p HDR', Seeders: 42, Size: 8589934592, Rank: 1, IsCurrent: false, IsRejected: false,
            },
            {
                Magnet: 'magnet:?xt=urn:btih:BBB1080', InfoHash: 'BBB1080', Indexer: 'Torrentio',
                Title: 'Alt 1080p', Seeders: 9, Size: 2147483648, Rank: 2, IsCurrent: false, IsRejected: false,
            },
        ],
        CanRejectCurrent: true,
        CanMaterialiseSelected: true,
        Message: 'ok',
    };
}

function unmaterialisedState(externalId, type, tmdb, season, episode) {
    return {
        ExternalId: externalId,
        Type: type,
        TmdbId: tmdb,
        Season: season ?? null,
        Episode: episode ?? null,
        Status: 'Virtual',
        CurrentSource: null,
        Candidates: [
            {
                Magnet: 'magnet:?xt=urn:btih:CAND1', InfoHash: 'CAND1', Indexer: 'Prowlarr',
                Title: 'Only 1080p', Seeders: 20, Size: 2147483648, Rank: 1, IsCurrent: false, IsRejected: false,
            },
        ],
        CanRejectCurrent: false,
        CanMaterialiseSelected: true,
        Message: 'ok',
    };
}

/* Extract the `@media (max-width: 600px){ ... }` block from the shim's injected CSS. */
function mobileMediaBlock(css) {
    const at = css.indexOf('@media (max-width: 600px)');
    if (at < 0) return '';
    const open = css.indexOf('{', at);
    let depth = 0;
    for (let i = open; i < css.length; i++) {
        if (css[i] === '{') depth++;
        else if (css[i] === '}') {
            depth--;
            if (depth === 0) return css.slice(open + 1, i);
        }
    }
    return '';
}

/* --- scenarios ------------------------------------------------------------- */

async function detailSectionScenario(label, { guid, item, externalId, type, tmdb, season, episode }) {
    console.log('\n[' + label + '] detail-page source section @ 390px viewport');
    const state = materialisedState(externalId, type, tmdb, season, episode);
    const env = makeEnv({ guid, item, sourcesState: state });
    loadShim(env);
    await settle();

    const { document } = env;
    const section = document.getElementById('phantom-source-section');
    ok(!!section, label + ': source section is injected into the detail page');
    if (!section) return;
    ok(section.closest('.detailPageContent') !== null, label + ': section lives inside the detail page content host');
    ok(section.querySelector('h2') && section.querySelector('h2').textContent === 'Phantom Source', label + ': section has "Phantom Source" heading');
    ok(section.textContent.includes('Prowlarr'), label + ': current-source summary rendered');

    const select = document.getElementById('phantom-source-candidates');
    ok(!!select, label + ': candidate <select> is present');
    const options = select.querySelectorAll('option');
    ok(options.length === 2, label + ': both alternate candidates listed (' + options.length + ')');
    ok(options.some((o) => o.textContent.includes('Alt 2160p HDR')), label + ': candidate labels are human-readable');

    const buttons = section.querySelectorAll('.phantom-source-button');
    ok(buttons.length === 2, label + ': materialise + reject buttons rendered');
    const matBtn = buttons.find((b) => b.textContent === 'Materialise selected source');
    const rejBtn = buttons.find((b) => b.textContent === 'Reject current source');
    ok(!!matBtn && !!rejBtn, label + ': both action buttons present with labels');

    // --- touch sizing + responsive layout, read from the SHIPPED injected CSS ---
    const css = document.getElementById('phantom-source-styles').textContent;
    ok(/\.phantom-source-button\{[^}]*min-height:44px/.test(css), label + ': buttons declare a >=44px touch target');
    ok(/\.phantom-source-select\{[^}]*min-height:44px/.test(css), label + ': the <select> declares a >=44px touch target');
    ok(/\.phantom-source-button\{[^}]*touch-action:manipulation/.test(css), label + ': buttons set touch-action:manipulation (no 300ms tap delay / double-tap zoom)');
    const media = mobileMediaBlock(css);
    ok(media.length > 0, label + ': a max-width:600px mobile media query exists');
    ok(/\.phantom-source-row\{[^}]*display:block/.test(media), label + ': controls stack vertically on a phone viewport');
    ok(/\.phantom-source-select\{[^}]*width:100%/.test(media), label + ': the <select> fills the width on mobile');
    ok(/\.phantom-source-select\{[^}]*min-width:0/.test(media), label + ': the <select> min-width is dropped so it never overflows a narrow phone');
    ok(/\.phantom-source-select\{[^}]*font-size:16px/.test(media), label + ': the <select> is pinned to 16px so iOS Safari does not focus-zoom');
    ok(/\.phantom-source-button\{[^}]*width:100%/.test(media), label + ': buttons fill the width on mobile');

    // --- API flow: tapping "Materialise selected source" posts the chosen candidate ---
    select.value = 'magnet:?xt=urn:btih:BBB1080'; // user picks the 1080p alternate
    env.api.calls.length = 0;
    matBtn.click();
    await settle();
    const matCall = env.api.calls.find((c) => c.kind === 'ajax' && /\/Sources\/MaterialiseCandidate$/.test(c.url));
    ok(!!matCall, label + ': tapping materialise POSTs to the MaterialiseCandidate endpoint');
    if (matCall) {
        ok(matCall.type === 'POST', label + ': materialise uses POST');
        ok(matCall.url.includes('/Items/' + encodeURIComponent(externalId) + '/Sources/MaterialiseCandidate'), label + ': materialise targets this item by stable ExternalId');
        const body = JSON.parse(matCall.data || '{}');
        ok(body.magnet === 'magnet:?xt=urn:btih:BBB1080', label + ': the selected candidate magnet is sent');
        ok(body.indexer === 'Torrentio' && body.title === 'Alt 1080p', label + ': the selected candidate metadata is sent');
    }

    // --- API flow: tapping "Reject current source" posts to RejectCurrent ---
    env.api.calls.length = 0;
    rejBtn.click();
    await settle();
    const rejCall = env.api.calls.find((c) => c.kind === 'ajax' && /\/Sources\/RejectCurrent$/.test(c.url));
    ok(!!rejCall && rejCall.type === 'POST', label + ': tapping reject POSTs to the RejectCurrent endpoint');
    if (rejCall) {
        ok(rejCall.url.includes('/Items/' + encodeURIComponent(externalId) + '/Sources/RejectCurrent'), label + ': reject targets this item by stable ExternalId');
    }
}

function buildActionSheet(document) {
    // Mirrors jellyfin-web's action-sheet DOM: <div class="actionSheet"> with a
    // .actionSheetContent, a template .listItem row, and a close button.
    const sheet = document.createElement('div');
    sheet.className = 'actionSheet actionSheet-fullscreen';
    const content = document.createElement('div');
    content.className = 'actionSheetContent';
    sheet.appendChild(content);

    const template = document.createElement('button');
    template.className = 'listItem listItem-button actionSheetMenuItem';
    template.setAttribute('data-id', 'play');
    const icon = document.createElement('span');
    icon.className = 'listItemIcon material-icons';
    icon.textContent = 'play_arrow';
    template.appendChild(icon);
    const bodyText = document.createElement('div');
    bodyText.className = 'listItemBody actionsheetListItemBody';
    const primary = document.createElement('div');
    primary.className = 'listItemBodyText';
    primary.textContent = 'Play';
    bodyText.appendChild(primary);
    template.appendChild(bodyText);
    content.appendChild(template);

    const close = document.createElement('button');
    close.className = 'actionSheetCloseButton';
    let closed = false;
    close.addEventListener('click', () => { closed = true; });
    sheet.appendChild(close);

    return { sheet, wasClosed: () => closed };
}

async function actionSheetScenario(label, { guid, item, externalId, type, tmdb, season, episode }, mode) {
    console.log('\n[' + label + '] kebab (...) action sheet @ 390px viewport [' + mode + ']');
    const state = mode === 'materialised'
        ? materialisedState(externalId, type, tmdb, season, episode)
        : unmaterialisedState(externalId, type, tmdb, season, episode);
    const env = makeEnv({ guid, item, sourcesState: state });
    loadShim(env);
    await settle();

    const { document } = env;
    const { sheet, wasClosed } = buildActionSheet(document);
    document.body.appendChild(sheet); // triggers the shim's MutationObserver
    await settle();

    const dataId = mode === 'materialised' ? 'phantom-reject-current-source' : 'phantom-materialise';
    const expectLabel = mode === 'materialised'
        ? 'Reject current source (Phantom Library)'
        : 'Materialise (Phantom Library)';
    const injected = sheet.querySelector('[data-id="' + dataId + '"]');
    ok(!!injected, label + ' [' + mode + ']: a Phantom entry is injected into the action sheet');
    if (!injected) return;
    ok(injected.textContent.includes(expectLabel), label + ' [' + mode + ']: entry carries the correct label');
    ok(injected.style.minHeight === '44px', label + ' [' + mode + ']: injected entry is a >=44px touch target');
    ok(injected.style.touchAction === 'manipulation', label + ' [' + mode + ']: injected entry sets touch-action:manipulation');

    env.api.calls.length = 0;
    injected.click();
    await settle();
    ok(wasClosed(), label + ' [' + mode + ']: tapping the entry closes the action sheet');
    if (mode === 'materialised') {
        const call = env.api.calls.find((c) => c.kind === 'ajax' && /\/Sources\/RejectCurrent$/.test(c.url));
        ok(!!call && call.type === 'POST', label + ' [' + mode + ']: tap fires RejectCurrent POST');
    } else {
        const call = env.api.calls.find((c) => c.kind === 'ajax' && /\/Materialise\//.test(c.url) && c.type === 'POST');
        ok(!!call, label + ' [' + mode + ']: tap fires Materialise POST');
    }
}

async function nonPhantomScenario() {
    console.log('\n[control] a non-phantom item gets no source controls');
    const guid = 'ffffffff-1111-2222-3333-444444444444';
    const item = { Id: guid, Name: 'Some Real Movie', Type: 'Movie', ExternalId: 'imdb_tt1234567' };
    const env = makeEnv({ guid, item, sourcesState: {} });
    loadShim(env);
    await settle();
    ok(env.document.getElementById('phantom-source-section') === null, 'control: no section injected for a non-phantom item');
}

/* --- run ------------------------------------------------------------------- */
const MOVIE = {
    guid: '11111111-2222-3333-4444-555555555555',
    externalId: 'movie_603',
    type: 'Movie',
    tmdb: 603,
    item: { Id: '11111111-2222-3333-4444-555555555555', Name: 'The Matrix', Type: 'Movie', ExternalId: 'movie_603' },
};
const EPISODE = {
    guid: '99999999-8888-7777-6666-555555555555',
    externalId: 'episode_1399_s1e1',
    type: 'Episode',
    tmdb: 1399,
    season: 1,
    episode: 1,
    item: { Id: '99999999-8888-7777-6666-555555555555', Name: 'Winter Is Coming', Type: 'Episode', ExternalId: 'episode_1399_s1e1' },
};

async function main() {
    console.log('=== Phantom Library mobile-viewport source-management DOM/API evidence ===');
    // Movie + TV episode parity (AGENTS.md movie/TV parity rule).
    await detailSectionScenario('movie', MOVIE);
    await detailSectionScenario('episode', EPISODE);
    await actionSheetScenario('movie', MOVIE, 'materialised');
    await actionSheetScenario('episode', EPISODE, 'materialised');
    await actionSheetScenario('movie', MOVIE, 'unmaterialised');
    await actionSheetScenario('episode', EPISODE, 'unmaterialised');
    await nonPhantomScenario();

    console.log('\n=== ' + (failures.length ? 'FAILED' : 'PASSED') + ': ' + (checks - failures.length) + '/' + checks + ' checks ===');
    if (failures.length) {
        console.error('\nFailures:');
        failures.forEach((f) => console.error(' - ' + f));
        process.exit(1);
    }
}

main().catch((err) => {
    console.error(err);
    process.exit(2);
});
