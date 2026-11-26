// Real Chromium + installed Jellyfin web. No local overlay evaluation or fake DOM.
import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { writeFileSync, readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';

const [item, kind, out] = process.argv.slice(2);
const base = process.env.PHANTOM_RIG_API;
assert.equal(new URL(base).port, '18096');
// Chromium Unix sockets cannot fit under long nested worktree paths. Its own
// profile lifecycle removes this directory; use an on-disk short temp root.
process.env.TMPDIR = process.env.PHANTOM_P14_BROWSER_TMPDIR || '/var/tmp';
const browser = await chromium.launch({ executablePath: process.env.PHANTOM_P14_CHROMIUM || undefined,
    args: ['--no-sandbox', '--autoplay-policy=no-user-gesture-required'] });
const page = await browser.newPage({ viewport: kind === 'episode' ? { width: 390, height: 844 } : { width: 1280, height: 900 } });
const stats = [], errors = [], assets = [], streams = [], playback = [], consoleLog = [], statsRequests = [];
page.on('request', request => {
    if (request.url().includes('/PlayerStats/Current')) statsRequests.push(Date.now());
});
page.on('console', m => consoleLog.push(m.text().replace(/(api_key=|Token=)[^&\s"']+/gi, '$1<redacted>')));
page.on('pageerror', e => errors.push(e.message));
page.on('response', async response => {
    const url = response.url();
    try {
        if (/\/PlaybackInfo|\/Shows\/|\/Users\/.*\/Items/.test(url) && !url.includes('/Images/')) playback.push({ path: new URL(url).pathname, status: response.status(), body: await response.json() });
        if (url.includes('/PlayerStats/Current')) stats.push({ at: Date.now(), status: response.status(), body: await response.json() });
        if (url.includes('/playerstats.js')) assets.push({ status: response.status(), body: await response.text() });
        if (/\/Videos\/.*(stream|master)/i.test(url)) streams.push({ url: new URL(url).pathname, status: response.status() });
    } catch (e) { errors.push(e.message); }
});
try {
    const index = await page.goto(base + '/web/index.html');
    assert.equal(index.status(), 200);
    assert.match(await index.text(), /Plugins\/PhantomLibrary\/badges.js/);
    await page.waitForFunction(() => window.__phantomPlayerStatsLoaded === true);
    // Login through the real web app; the overlay uses its real ApiClient/device.
    await page.locator('#txtManualName').waitFor({ timeout: 30000 });
    await page.locator('#txtManualName').fill('a');
    await page.locator('#txtManualPassword').fill('a');
    await page.locator('button[type=submit]').click();
    await page.waitForURL(/#\/home/);
    await page.waitForFunction(() => window.ApiClient && window.ApiClient.getCurrentUserId());
    await page.goto(base + '/web/index.html#/details?id=' + item);
    await page.locator('.btnPlay').filter({ visible: true }).first().click({ timeout: 30000 });
    await page.waitForFunction(() => {
        const root = document.getElementById('phantom-player-stats');
        return root && root.getAttribute('data-status') === 'ok' && root.style.display !== 'none';
    }, null, { timeout: 60000 });
    // A media response header alone is not playback: require the real browser
    // to decode bytes and advance while the installed overlay polls.
    await page.waitForFunction(() => {
        const video = document.querySelector('video.htmlvideoplayer');
        return video && video.currentTime > 1 && !video.paused && video.readyState >= 2;
    });
    await page.waitForTimeout(7000);
    const ok = stats.filter(x => x.body.status === 'ok');
    assert.ok(ok.length >= 3, JSON.stringify(stats));
    const hash = kind === 'movie' ? '1'.repeat(40) : '2'.repeat(40);
    for (const { status, body } of ok) {
        assert.equal(status, 200);
        assert.equal(body.type, kind);
        assert.equal(body.source.infoHash, hash);
        assert.ok(body.source.release && body.source.indexer && body.source.sizeBytes > 0);
        assert.equal(body.stream.state, 'streaming');
        assert.ok(body.stream.peers > 0 && body.stream.seeders > 0 && body.stream.downloadRateBytesPerSec > 0);
        assert.ok(Object.hasOwn(body.stream, 'bufferedAheadSeconds'));
    }
    for (let i = 1; i < ok.length; i++) assert.ok(ok[i].at - ok[i-1].at >= 1800 && ok[i].at - ok[i-1].at <= 5500);
    assert.ok(assets.length > 0 && assets.every(x => x.status === 200));
    const local = readFileSync(new URL('../../../src/Jellyfin.Plugin.PhantomLibrary/Configuration/phantomPlayerStats.js', import.meta.url), 'utf8');
    assert.equal(assets[0].body, local, 'served asset must be this build');
    assert.ok(streams.some(x => x.status === 200 || x.status === 206), JSON.stringify(streams));
    if (kind === 'episode') assert.match(await page.locator('#phantom-player-stats').getAttribute('class'), /mobile/);
    await page.screenshot({ path: out + '/overlay-' + kind + '.png' });
    // Browser Back closes Jellyfin's actual player and must report Stopped;
    // no direct Sessions mutation or fake DOM removal can satisfy this assertion.
    await Promise.all([
        page.waitForResponse(r => r.url().includes('/Sessions/Playing/Stopped') && r.status() === 204),
        page.goBack()
    ]);
    await page.waitForFunction(() => !window.__phantomPlayerStats.state().polling, null, { timeout: 15000 });
    await page.waitForTimeout(1000); // settle a possible response already in flight
    const count = statsRequests.length;
    await page.waitForTimeout(7000);
    assert.equal(statsRequests.length, count, 'zero requests after stop (including failed/pending requests)');
    const state = await page.evaluate(() => window.__phantomPlayerStats.state());
    assert.equal(state.timerArmed, false);
    assert.equal(state.visible, false);
    const receipt = { kind, result: 'passed', okPolls: ok.length, streams, afterStopPolls: statsRequests.length-count,
        overlaySha256: createHash('sha256').update(assets[0].body).digest('hex'), state };
    writeFileSync(out + '/browser-' + kind + '.json', JSON.stringify(receipt, null, 2));
    console.log('PLAYER_STATS_BROWSER_OK', JSON.stringify(receipt));
} finally {
    writeFileSync(out + '/browser-debug-' + kind + '.json', JSON.stringify({ url: page.url(), stats, statsRequests, errors, streams, playback, consoleLog }, null, 2));
    await page.screenshot({ path: out + '/browser-final-' + kind + '.png' });
    writeFileSync(out + '/browser-final-' + kind + '.html', await page.content());
    await browser.close();
}
