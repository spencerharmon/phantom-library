/*
 * p14-player-stats-overlay — DOM/API/polling-lifecycle evidence for the in-player
 * stream stats overlay (src/.../Configuration/phantomPlayerStats.js).
 *
 * Executes the REAL shim (and the real phantomBadges.js bootstrap) in node:vm
 * against a minimal faithful DOM (the shim deliberately limits itself to
 * getElementsByTagName/getElementById/createElement/textContent/style, so no
 * jsdom/npm dependency is needed — same approach as phantom-kebab-mobile-dom.mjs).
 *
 * Offline unit harness (live coverage is browser/player-stats.mjs):
 *
 *   node tools/rig-scenarios/phantom-player-stats-overlay-dom.mjs
 *       OFFLINE (default; CI/sandbox): a mocked ApiClient answering the real
 *       PlayerStatsResponse wire shape (src/.../Playback/PlayerStatsService.cs),
 *       a VIRTUAL clock. Asserts: no polling unless the player is open; polling at
 *       the server's 2–5 s cadence (clamped); stop on player close / backgrounding
 *       / collapse toggle / server "not playing"; re-arm on the next <video>
 *       loadstart; one request in flight; movie + episode render; desktop vs
 *       mobile layout; untrusted text via textContent; badges.js bootstrap.
 *
 * Exits non-zero on any failed assertion.
 */

import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const OVERLAY = new URL('../../src/Jellyfin.Plugin.PhantomLibrary/Configuration/phantomPlayerStats.js', import.meta.url);
const BADGES = new URL('../../src/Jellyfin.Plugin.PhantomLibrary/Configuration/phantomBadges.js', import.meta.url);
const overlaySrc = readFileSync(OVERLAY, 'utf8');
const badgesSrc = readFileSync(BADGES, 'utf8');

/* ------------------------------------------------------------------ fake DOM */

class FakeEvents {
    constructor() { this._l = {}; }
    addEventListener(t, fn) { (this._l[t] ||= []).push(fn); }
    removeEventListener(t, fn) { this._l[t] = (this._l[t] || []).filter((f) => f !== fn); }
    dispatch(t, ev = {}) { for (const fn of [...(this._l[t] || [])]) fn({ type: t, target: this, ...ev }); }
}

class FakeElement extends FakeEvents {
    constructor(tag, doc) {
        super();
        this.tagName = String(tag).toUpperCase();
        this.ownerDocument = doc;
        this.children = [];
        this.parentNode = null;
        this.attributes = {};
        this.style = {};
        this.className = '';
        this._text = '';
        this.id = '';
        this.nodeType = 1;
    }
    get textContent() { return this._text + this.children.map((c) => c.textContent).join(''); }
    set textContent(v) { this.children = []; this._text = String(v); }
    get innerHTML() { throw new Error('overlay must not read innerHTML'); }
    set innerHTML(v) { throw new Error('overlay must not write innerHTML (XSS): ' + v); }
    appendChild(c) {
        if (c.parentNode) c.parentNode.removeChild(c);
        c.parentNode = this;
        this.children.push(c);
        this.ownerDocument._mutated(c);
        return c;
    }
    removeChild(c) { this.children = this.children.filter((x) => x !== c); c.parentNode = null; return c; }
    remove() { if (this.parentNode) this.parentNode.removeChild(this); }
    setAttribute(k, v) { this.attributes[k] = String(v); if (k === 'id') this.id = String(v); }
    getAttribute(k) { return Object.prototype.hasOwnProperty.call(this.attributes, k) ? this.attributes[k] : null; }
    *walk() { yield this; for (const c of this.children) yield* c.walk(); }
}

function makeDom({ hash = '#/home.html', width = 1440, coarse = false, storage = {}, currentScriptSrc = null } = {}) {
    const doc = new FakeEvents();
    doc._observers = [];
    doc._mutated = (node) => { for (const o of doc._observers) o(node); };
    doc.readyState = 'complete';
    doc.hidden = false;
    doc.visibilityState = 'visible';
    doc.documentElement = new FakeElement('html', doc);
    doc.head = new FakeElement('head', doc);
    doc.body = new FakeElement('body', doc);
    doc.documentElement.children.push(doc.head, doc.body);
    doc.head.parentNode = doc.documentElement;
    doc.body.parentNode = doc.documentElement;
    doc.createElement = (t) => new FakeElement(t, doc);
    doc.createTextNode = (t) => { const n = new FakeElement('#text', doc); n.nodeType = 3; n._text = String(t); return n; };
    doc.getElementById = (id) => { for (const n of doc.documentElement.walk()) if (n.id === id) return n; return null; };
    doc.getElementsByTagName = (t) => [...doc.documentElement.walk()].filter((n) => n.tagName === String(t).toUpperCase());
    doc.querySelector = () => null;
    doc.querySelectorAll = () => [];
    doc.currentScript = currentScriptSrc ? { src: currentScriptSrc } : null;

    const win = new FakeEvents();
    win.document = doc;
    win.location = { hash, href: 'http://127.0.0.1:18096/web/index.html' + hash };
    win.innerWidth = width;
    const store = { ...storage };
    win.localStorage = {
        getItem: (k) => (Object.prototype.hasOwnProperty.call(store, k) ? store[k] : null),
        setItem: (k, v) => { store[k] = String(v); },
        removeItem: (k) => { delete store[k]; }
    };
    win.matchMedia = (q) => {
        const mw = /max-width:\s*(\d+)px/.exec(q);
        if (mw) return { matches: width <= Number(mw[1]) };
        if (/pointer:\s*coarse/.test(q)) return { matches: coarse };
        return { matches: false };
    };
    win.MutationObserver = class {
        constructor(cb) { this.cb = cb; }
        observe() { doc._observers.push(() => this.cb([])); }
        disconnect() {}
    };
    win.console = { warn: () => {}, log: () => {}, debug: () => {}, error: (...a) => console.error(...a) };
    win.NodeFilter = { SHOW_TEXT: 4 };
    doc.createTreeWalker = () => ({ nextNode: () => null });
    return { win, doc, store };
}

/* ------------------------------------------------------------------ clocks */

function virtualClock() {
    let now = 0;
    let seq = 0;
    const timers = new Map();
    return {
        now: () => now,
        setTimeout: (fn, ms) => { const id = ++seq; timers.set(id, { at: now + Math.max(0, Number(ms) || 0), fn }); return id; },
        clearTimeout: (id) => { timers.delete(id); },
        pending: () => timers.size,
        async advance(ms) {
            const end = now + ms;
            for (;;) {
                await flush();
                let next = null;
                for (const [id, t] of timers) if (t.at <= end && (!next || t.at < next[1].at || (t.at === next[1].at && id < next[0]))) next = [id, t];
                if (!next) break;
                timers.delete(next[0]);
                now = next[1].at;
                next[1].fn();
            }
            now = end;
            await flush();
        }
    };
}

async function flush() { for (let i = 0; i < 20; i++) await Promise.resolve(); }

/* ------------------------------------------------------------------ harness */

function boot({ responder, clock, dom = makeDom(), apiClient = null, loadBadges = false }) {
    const { win, doc } = dom;
    const calls = [];
    win.setTimeout = clock.setTimeout;
    win.clearTimeout = clock.clearTimeout;
    win.setInterval = () => 0;
    win.clearInterval = () => {};
    win.ApiClient = apiClient || {
        getUrl: (p) => 'http://127.0.0.1:18096/' + p,
        ajax: (opts) => {
            calls.push({ ...opts, at: clock.now() });
            return Promise.resolve().then(() => responder(opts, calls.length));
        }
    };
    const ctx = vm.createContext({
        window: win, document: doc, console: win.console, Promise, Math, Number, String, Object, isNaN, JSON, Date,
        setTimeout: win.setTimeout, clearTimeout: win.clearTimeout, setInterval: win.setInterval, clearInterval: win.clearInterval,
        localStorage: win.localStorage, MutationObserver: win.MutationObserver, NodeFilter: win.NodeFilter, location: win.location
    });
    win.window = win;
    if (loadBadges) vm.runInContext(badgesSrc, ctx, { filename: 'phantomBadges.js' });
    vm.runInContext(overlaySrc, ctx, { filename: 'phantomPlayerStats.js' });
    const api = win.__phantomPlayerStats;
    return {
        win, doc, calls, ctx, api,
        root: () => doc.getElementById('phantom-player-stats'),
        field: (name) => [...doc.getElementById('phantom-player-stats').walk()].find((n) => n.getAttribute('data-field') === name)?.textContent,
        title: () => [...doc.getElementById('phantom-player-stats').walk()].find((n) => n.className === 'pps-title')?.textContent,
        openPlayer() {
            win.location.hash = '#/video';
            const v = doc.createElement('video');
            v.className = 'htmlvideoplayer moveUpSubtitles';
            const container = doc.createElement('div');
            container.className = 'videoPlayerContainer';
            container.appendChild(v);
            doc.body.appendChild(container);
            win.dispatch('hashchange');
            return v;
        },
        closePlayer() {
            for (const c of [...doc.body.children]) if (c.className === 'videoPlayerContainer') c.remove();
            win.location.hash = '#/details?id=abc';
            win.dispatch('hashchange');
        },
        setHidden(hidden) {
            doc.hidden = hidden;
            doc.visibilityState = hidden ? 'hidden' : 'visible';
            doc.dispatch('visibilitychange');
        }
    };
}

const movieOk = (over = {}) => ({
    status: 'ok', playing: true, isPhantom: true, itemId: 'aaaa', type: 'movie', tmdbId: 99000001, season: null, episode: null,
    playSessionId: null, mediaSourceId: 'aaaa', isPaused: false, positionTicks: 1, materialisedAt: '2026-11-26T08:00:00Z',
    source: { release: 'Phantom.Rig.Alpha.2024.1080p.WEB-DL.x264-RIG', releaseSource: 'candidate', sizeBytes: 2147483648, indexer: 'rig-indexer', infoHash: '1111111111111111111111111111111111111111', seedersAtSelection: 120 },
    stream: { state: 'streaming', peers: 17, seeders: 9, downloadRateBytesPerSec: 2621440, bytesCompleted: 1, torrentSize: 2147483648, readOffset: 1, bufferedAheadBytes: 33554432, bufferedAheadSeconds: 42, lastReadAt: null, torrentSampledAt: null, streamSampledAt: null },
    pollIntervalMs: 3000, ...over
});
const episodeOk = (over = {}) => movieOk({
    type: 'episode', tmdbId: 99100001, season: 1, episode: 1,
    source: { release: 'Phantom.Rig.Delta.S01E01.1080p.WEB-DL.x264-RIG', releaseSource: 'candidate', sizeBytes: 734003200, indexer: 'rig-tv-indexer', infoHash: '2222222222222222222222222222222222222222', seedersAtSelection: 45 },
    stream: { state: 'stalled', peers: 3, seeders: 0, downloadRateBytesPerSec: 0, bytesCompleted: 1, torrentSize: 734003200, readOffset: 1, bufferedAheadBytes: 0, bufferedAheadSeconds: null, lastReadAt: null, torrentSampledAt: null, streamSampledAt: null },
    ...over
});
const notPlaying = () => ({ status: 'not_playing', playing: false, isPhantom: false, pollIntervalMs: 3000 });

/* ------------------------------------------------------------------ offline cases */

const results = [];
async function test(name, fn) {
    try {
        await fn();
        results.push([true, name]);
        console.log(`  PASS ${name}`);
    } catch (e) {
        results.push([false, name]);
        console.log(`  FAIL ${name}\n       ${e && e.stack ? e.stack.split('\n').slice(0, 3).join('\n       ') : e}`);
    }
}
function assert(cond, msg) { if (!cond) throw new Error(msg); }
function eq(a, b, msg) { if (a !== b) throw new Error(`${msg}: expected ${JSON.stringify(b)}, got ${JSON.stringify(a)}`); }

async function offline() {
    await test('collapsed media start during pending probe coalesces one recovery request', async () => {
        const clock = virtualClock();
        let resolve;
        const pending = new Promise(r => { resolve = r; });
        const h = boot({ responder: (_, n) => n === 1 ? pending : episodeOk(), clock,
            dom: makeDom({ storage: { 'phantomPlayerStats.collapsed': '1' } }) });
        const video = h.openPlayer();
        await flush();
        video.dispatch('loadstart');
        video.dispatch('playing');
        resolve(notPlaying());
        await clock.advance(30000);
        eq(h.calls.length, 2, 'exactly one event-driven followup');
        eq(h.field('release'), episodeOk().source.release, 'resolved next episode');
        eq(h.api.state().timerArmed, false, 'no collapsed polling timer');
    });
    for (const failure of ['not_playing', 'error']) {
        await test(`collapsed ${failure} startup recovers on playing, episode transition, no timer`, async () => {
            const clock = virtualClock();
            let response = failure;
            const h = boot({ responder: () => {
                if (response === 'error') throw new Error('temporary failure');
                return response === 'not_playing' ? notPlaying() : response;
            }, clock, dom: makeDom({ storage: { 'phantomPlayerStats.collapsed': '1' } }) });
            const video = h.openPlayer();
            await clock.advance(1000);
            response = movieOk();
            video.dispatch('loadstart');
            await flush();
            video.dispatch('playing');
            await clock.advance(30000);
            assert(h.root() && h.api.state().visible, 'recoverable expansion control');
            eq(h.api.state().timerArmed, false, 'collapsed has no timer');
            const count = h.calls.length;
            await clock.advance(30000);
            eq(h.calls.length, count, 'no collapsed polling');
            response = episodeOk();
            video.dispatch('loadstart');
            await flush();
            eq(h.field('release'), response.source.release, 'next episode replaces old source');
            h.setHidden(true);
            video.dispatch('playing');
            await clock.advance(30000);
            eq(h.calls.length, count + 1, 'no background event probes');
            h.closePlayer();
            video.dispatch('playing');
            await clock.advance(30000);
            eq(h.calls.length, count + 1, 'no close event probes');
        });
    }
    console.log('== offline: real phantomPlayerStats.js, mocked ApiClient, virtual clock');

    await test('no player open → zero requests, no timer, no overlay', async () => {
        const clock = virtualClock();
        const h = boot({ responder: () => movieOk(), clock });
        await clock.advance(60000);
        eq(h.calls.length, 0, 'requests while player closed');
        eq(h.api.state().polling, false, 'polling');
        eq(h.root(), null, 'overlay element');
    });

    await test('movie: player open → polls /PlayerStats/Current and renders source + stream', async () => {
        const clock = virtualClock();
        const h = boot({ responder: () => movieOk(), clock });
        h.openPlayer();
        await clock.advance(0);
        eq(h.calls.length, 1, 'immediate first poll');
        eq(h.calls[0].type, 'GET', 'method');
        assert(h.calls[0].url.endsWith('/Plugins/PhantomLibrary/PlayerStats/Current'), 'endpoint: ' + h.calls[0].url);
        eq(h.root().style.display, '', 'overlay visible');
        eq(h.title(), 'Phantom stream', 'movie title (no SxxEyy)');
        eq(h.field('release'), 'Phantom.Rig.Alpha.2024.1080p.WEB-DL.x264-RIG', 'release');
        eq(h.field('size'), '2.0 GB', 'size');
        eq(h.field('indexer'), 'rig-indexer', 'indexer');
        eq(h.field('peers'), '17 / 9', 'peers/seeders');
        eq(h.field('rate'), '2.5 MB/s', 'download rate');
        eq(h.field('buffer'), '42s', 'buffered-ahead seconds');
        eq(h.field('state'), 'Streaming', 'gostream stream state');
        eq(h.root().getAttribute('data-status'), 'ok', 'data-status');
        assert(!/phantom-player-stats--mobile/.test(h.root().className), 'desktop layout');
    });

    await test('episode parity: S01E01 label, stalled state flagged, null seconds falls back to bytes', async () => {
        const clock = virtualClock();
        const h = boot({ responder: () => episodeOk(), clock });
        h.openPlayer();
        await clock.advance(0);
        eq(h.title(), 'Phantom stream · S01E01', 'episode title');
        eq(h.field('release'), 'Phantom.Rig.Delta.S01E01.1080p.WEB-DL.x264-RIG', 'release');
        eq(h.field('indexer'), 'rig-tv-indexer', 'indexer');
        eq(h.field('peers'), '3 / 0', 'peers/seeders (measured zero shown as 0)');
        eq(h.field('rate'), '0 B/s', 'rate');
        eq(h.field('buffer'), '0 B', 'buffer bytes fallback');
        eq(h.field('state'), 'Stalled', 'state');
        eq(h.root().getAttribute('data-stream-state'), 'stalled', 'stalled marker');
    });

    await test('cadence: server pollIntervalMs honoured and clamped to 2000–5000 ms', async () => {
        for (const [asked, expected] of [[3000, 3000], [100, 2000], [60000, 5000], [4000, 4000], [undefined, 3000]]) {
            const clock = virtualClock();
            const h = boot({ responder: () => movieOk({ pollIntervalMs: asked }), clock });
            h.openPlayer();
            await clock.advance(0);
            await clock.advance(expected - 1);
            eq(h.calls.length, 1, `no early poll (asked ${asked})`);
            await clock.advance(1);
            eq(h.calls.length, 2, `poll at ${expected} ms (asked ${asked})`);
            await clock.advance(expected * 3);
            eq(h.calls.length, 5, `steady cadence (asked ${asked})`);
        }
    });

    await test('player close → polling stops immediately, overlay hidden, late response discarded', async () => {
        const clock = virtualClock();
        let release;
        let n = 0;
        const h = boot({
            responder: () => { n++; return n === 2 ? new Promise((r) => { release = r; }) : movieOk(); },
            clock
        });
        h.openPlayer();
        await clock.advance(3000);
        eq(h.calls.length, 2, 'second poll in flight');
        h.closePlayer();
        eq(h.api.state().polling, false, 'polling flag after close');
        eq(h.api.state().timerArmed, false, 'no timer after close');
        eq(h.root().style.display, 'none', 'overlay hidden after close');
        release(movieOk());
        await clock.advance(0);
        eq(h.root().style.display, 'none', 'late in-flight response must not re-show overlay');
        await clock.advance(120000);
        eq(h.calls.length, 2, 'zero requests after player closed');
    });

    await test('background (visibilitychange hidden) stops polling; foreground resumes', async () => {
        const clock = virtualClock();
        const h = boot({ responder: () => movieOk(), clock });
        h.openPlayer();
        await clock.advance(0);
        h.setHidden(true);
        await clock.advance(60000);
        eq(h.calls.length, 1, 'no requests while backgrounded');
        h.setHidden(false);
        await clock.advance(0);
        eq(h.calls.length, 2, 'immediate poll on return');
        await clock.advance(3000);
        eq(h.calls.length, 3, 'cadence resumes');
    });

    await test('toggle: collapse stops polling + persists; pill stays visible; expand resumes', async () => {
        const clock = virtualClock();
        const dom = makeDom();
        const h = boot({ responder: () => movieOk(), clock, dom });
        h.openPlayer();
        await clock.advance(0);
        const head = [...h.root().walk()].find((n) => n.className === 'pps-head');
        head.dispatch('click');
        eq(h.api.state().collapsed, true, 'collapsed');
        eq(dom.store['phantomPlayerStats.collapsed'], '1', 'persisted preference');
        assert(/phantom-player-stats--collapsed/.test(h.root().className), 'collapsed class');
        eq(h.root().style.display, '', 'collapsed pill still visible for re-expansion');
        await clock.advance(60000);
        eq(h.calls.length, 1, 'no polling while collapsed');
        head.dispatch('click');
        await clock.advance(0);
        eq(h.calls.length, 2, 'expand polls immediately');
        await clock.advance(3000);
        eq(h.calls.length, 3, 'cadence resumes after expand');
    });

    await test('collapsed preference on a fresh player: exactly ONE probe, then no polling', async () => {
        const clock = virtualClock();
        const h = boot({ responder: () => movieOk(), clock, dom: makeDom({ storage: { 'phantomPlayerStats.collapsed': '1' } }) });
        h.openPlayer();
        await clock.advance(60000);
        eq(h.calls.length, 1, 'single probe while collapsed');
        eq(h.root().style.display, '', 'collapsed pill shown for phantom item');
    });

    await test('server says playback stopped → polling stops; next <video> loadstart re-arms', async () => {
        const clock = virtualClock();
        let playing = true;
        const h = boot({ responder: () => (playing ? episodeOk() : notPlaying()), clock });
        const video = h.openPlayer();
        await clock.advance(3000);
        eq(h.calls.length, 2, 'two playing polls');
        playing = false;
        await clock.advance(3000);
        eq(h.calls.length, 3, 'not_playing observed');
        eq(h.api.state().polling, false, 'stopped by server');
        eq(h.root().style.display, 'none', 'overlay hidden when not playing');
        await clock.advance(120000);
        eq(h.calls.length, 3, 'no polling after stop');
        playing = true;
        video.dispatch('loadstart');
        await clock.advance(0);
        eq(h.calls.length, 4, 're-armed by next item loadstart (autoplay next episode)');
        eq(h.root().style.display, '', 'overlay back');
    });

    await test('start-up grace: not_playing before the session reports Playing is tolerated, then gives up', async () => {
        const clock = virtualClock();
        let n = 0;
        const h = boot({ responder: () => (++n <= 2 ? notPlaying() : movieOk()), clock });
        h.openPlayer();
        await clock.advance(6000);
        eq(h.calls.length, 3, 'kept polling through 2 early not_playing answers');
        eq(h.root().style.display, '', 'rendered once playing');

        const clock2 = virtualClock();
        const h2 = boot({ responder: () => notPlaying(), clock: clock2 });
        h2.openPlayer();
        await clock2.advance(120000);
        eq(h2.calls.length, 6, 'gives up after grace (1 + 5 tolerated)');
        eq(h2.api.state().polling, false, 'stopped');
    });

    await test('non-phantom playing item → overlay never shown, polling stops', async () => {
        const clock = virtualClock();
        const h = boot({ responder: () => ({ status: 'not_phantom', playing: true, isPhantom: false, pollIntervalMs: 3000 }), clock });
        h.openPlayer();
        await clock.advance(60000);
        eq(h.calls.length, 1, 'one request');
        assert(!h.root() || h.root().style.display === 'none', 'overlay hidden');
    });

    await test('at most one request in flight (slow server)', async () => {
        const clock = virtualClock();
        const pending = [];
        const h = boot({ responder: () => new Promise((r) => pending.push(r)), clock });
        h.openPlayer();
        await clock.advance(30000);
        eq(h.calls.length, 1, 'no request pile-up while one is in flight');
        pending[0](movieOk());
        await clock.advance(3000);
        eq(h.calls.length, 2, 'next poll after the slow one returned');
    });

    await test('non-ok statuses render a label, not a crash', async () => {
        for (const [status, text] of [['materialising', 'Materialising…'], ['torrent_not_loaded', 'Torrent not loaded in gostream'], ['gostream_unavailable', 'gostream unavailable']]) {
            const clock = virtualClock();
            const h = boot({ responder: () => movieOk({ status, stream: null }), clock });
            h.openPlayer();
            await clock.advance(0);
            eq(h.field('state'), text, status);
            eq(h.field('peers'), '—', `${status} peers placeholder`);
        }
    });

    await test('mobile layout (narrow viewport + coarse pointer): mobile class, 44px toggle CSS', async () => {
        const clock = virtualClock();
        const dom = makeDom({ width: 390, coarse: true });
        const h = boot({ responder: () => episodeOk(), clock, dom });
        h.openPlayer();
        await clock.advance(0);
        assert(/phantom-player-stats--mobile/.test(h.root().className), 'mobile class: ' + h.root().className);
        const css = dom.doc.getElementById('phantom-player-stats-css').textContent;
        assert(/--mobile \.pps-toggle\{min-width:44px;min-height:44px\}/.test(css), 'touch-sized toggle');
        eq(h.field('release'), 'Phantom.Rig.Delta.S01E01.1080p.WEB-DL.x264-RIG', 'same data on mobile');
    });

    await test('untrusted release text is rendered as text (no HTML injection)', async () => {
        const clock = virtualClock();
        const evil = '<img src=x onerror=alert(1)>Evil.Release';
        const h = boot({ responder: () => movieOk({ source: { ...movieOk().source, release: evil } }), clock });
        h.openPlayer();
        await clock.advance(0);
        eq(h.field('release'), evil, 'raw text preserved via textContent');
        assert(!h.doc.getElementsByTagName('img').length, 'no <img> element created');
    });

    await test('badges.js bootstraps playerstats.js exactly once; overlay double-load is a no-op', async () => {
        const clock = virtualClock();
        const dom = makeDom({ currentScriptSrc: 'https://jf.example.com/Plugins/PhantomLibrary/badges.js' });
        dom.win.__phantomPlayerStatsLoaded = false;
        boot({ responder: () => movieOk(), clock, dom, loadBadges: true });
        const tag = dom.doc.getElementById('phantom-player-stats-script');
        // The overlay itself ran (boot loaded it after badges) so the badges
        // bootstrap may or may not have appended; re-run badges now: must be a no-op.
        const before = dom.doc.getElementsByTagName('script').length;
        vm.runInContext(badgesSrc, vm.createContext({ window: dom.win, document: dom.doc, console: dom.win.console, setTimeout: clock.setTimeout, clearTimeout: clock.clearTimeout, MutationObserver: dom.win.MutationObserver, NodeFilter: dom.win.NodeFilter, location: dom.win.location }));
        eq(dom.doc.getElementsByTagName('script').length, before, 'no second bootstrap tag');
        assert(tag, 'bootstrap <script> appended by badges.js');
        eq(tag.src, 'https://jf.example.com/Plugins/PhantomLibrary/playerstats.js', 'bootstrap resolves relative to badges.js');
        const firstApi = dom.win.__phantomPlayerStats;
        vm.runInContext(overlaySrc, vm.createContext({ window: dom.win, document: dom.doc, console: dom.win.console, setTimeout: clock.setTimeout, clearTimeout: clock.clearTimeout }));
        eq(dom.win.__phantomPlayerStats, firstApi, 'second overlay load did not re-initialise');
    });
}

/* ------------------------------------------------------------------ main */
// Live testing lives ONLY in browser/player-stats.mjs (real installed web +
// Chromium), never in this simulated-DOM harness.
if (process.argv.length > 2) throw new Error('No live/skip mode; run scenario 52 for real browser playback');
await offline();
const failed = results.filter((r) => !r[0]).length;
console.log(`\n${results.length - failed} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
