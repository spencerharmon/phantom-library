// p14-native-episode-web-queue: real Chromium + pinned official jellyfin-web.
// A regular user logs in (Home loads its Latest rows), opens a materialised
// channel episode and presses the ordinary Play button. Passes only if web's
// own all-series queue request lists the episode, Jellyfin issues
// PlaybackInfo, the <video> element advances and stream responses carry
// nonzero media bytes - on a desktop AND a mobile viewport.
import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';

const spec = JSON.parse(readFileSync(process.argv[2], 'utf8'));
// Chromium's profile sockets do not fit under long nested worktree paths.
process.env.TMPDIR = process.env.PHANTOM_RIG_BROWSER_TMPDIR || '/var/tmp';
const browser = await chromium.launch({
    executablePath: process.env.PHANTOM_RIG_CHROMIUM || undefined,
    args: ['--no-sandbox', '--autoplay-policy=no-user-gesture-required'],
});
const redact = s => String(s).replace(/(api_key=|ApiKey=|Token=|X-Emby-Token=)[^&\s"',]+/gi, '$1<redacted>');
const profiles = [
    ['desktop', { viewport: { width: 1280, height: 900 } }],
    ['mobile', { viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, isMobile: true, hasTouch: true,
        userAgent: 'Mozilla/5.0 (Linux; Android 14; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Mobile Safari/537.36' }],
];
const receipts = [];
try {
    for (const [name, options] of profiles) {
        const context = await browser.newContext(options);
        const page = await context.newPage();
        const cdp = await context.newCDPSession(page);
        await cdp.send('Network.enable');
        const media = new Map(); // requestId -> {url, status, bytes}
        cdp.on('Network.responseReceived', e => {
            if (/\/(Videos|Audio)\/[^/]+\/(stream|master|main|hls1|[^/]+\.(ts|mp4|m4s))/i.test(e.response.url)) {
                media.set(e.requestId, { url: redact(new URL(e.response.url).pathname), status: e.response.status, bytes: 0 });
            }
        });
        cdp.on('Network.dataReceived', e => { const m = media.get(e.requestId); if (m) m.bytes += e.dataLength; });
        const queues = [], playbackInfo = [], errors = [], consoleLog = [];
        page.on('pageerror', e => errors.push(redact(e.message)));
        page.on('console', m => consoleLog.push(redact(m.text())));
        page.on('response', async r => {
            const url = r.url();
            try {
                if (url.includes(`/Shows/${spec.series}/Episodes`)) {
                    const body = await r.json();
                    queues.push({ query: redact(new URL(url).search), status: r.status(), ids: body.Items.map(i => i.Id), total: body.TotalRecordCount });
                }
                if (url.includes('/PlaybackInfo')) playbackInfo.push({ status: r.status(), method: r.request().method() });
            } catch (e) { errors.push(redact(e.message)); }
        });
        try {
            const index = await page.goto(spec.api + '/web/index.html');
            assert.equal(index.status(), 200);
            await page.locator('#txtManualName').waitFor({ timeout: 60000 });
            await page.locator('#txtManualName').fill(spec.user);
            await page.locator('#txtManualPassword').fill(spec.password);
            await page.locator('button[type=submit]').click();
            await page.waitForURL(/#\/home/, { timeout: 60000 });
            // Home issues its per-library Latest requests (incl. Phantom Shows).
            // Recorded, not assumed: the REST scenario already proves the core
            // latest-refresh path; this records whether web's Home issued it too.
            const homeLatest = await page.waitForResponse(r => /\/Items\/Latest/.test(r.url()), { timeout: 60000 })
                .then(r => ({ seen: true, status: r.status() }), e => ({ seen: false, reason: redact(e.message) }));
            await page.waitForTimeout(3000);
            await page.goto(spec.api + '/web/index.html#/details?id=' + spec.item);
            const play = page.locator('.btnPlay').filter({ visible: true }).first();
            await play.waitFor({ timeout: 60000 });
            await play.click();
            await page.waitForFunction(() => {
                const v = document.querySelector('video');
                return v && (v.currentTime > 1 || v.ended);
            }, null, { timeout: 90000 });
            await page.waitForTimeout(2000);
            const video = await page.evaluate(() => {
                const v = document.querySelector('video');
                return { currentTime: v.currentTime, readyState: v.readyState, ended: v.ended, error: v.error && v.error.code };
            });
            const webQueue = queues.find(q => q.query.includes('startItemId=' + spec.item) && !/SeasonId=/i.test(q.query));
            assert.ok(webQueue, 'jellyfin-web did not request the all-series episode queue: ' + JSON.stringify(queues));
            assert.equal(webQueue.status, 200);
            assert.deepEqual(webQueue.ids, spec.expectedQueue, 'web all-series queue');
            assert.ok(playbackInfo.some(p => p.status === 200), 'no PlaybackInfo: ' + JSON.stringify(playbackInfo));
            const streams = [...media.values()];
            const bytes = streams.reduce((n, s) => n + (s.status < 400 ? s.bytes : 0), 0);
            assert.ok(bytes > 0, 'no stream bytes: ' + JSON.stringify(streams));
            assert.equal(video.error, null);
            assert.equal(await page.getByText('Unable to find a valid media source to play').count(), 0);
            await page.screenshot({ path: `${spec.out}/web-play-${name}.png` });
            const receipt = { profile: name, result: 'passed', homeLatest, webQueue, playbackInfo, streams, streamBytes: bytes, video };
            receipts.push(receipt);
            console.log('WEB_PLAY_OK', name, `queue=${webQueue.ids.length}`, `streamBytes=${bytes}`, `currentTime=${video.currentTime.toFixed(2)}`, `homeLatest=${homeLatest.seen}`);
        } finally {
            writeFileSync(`${spec.out}/web-debug-${name}.json`, JSON.stringify({ url: redact(page.url()), queues, playbackInfo, streams: [...media.values()], errors, consoleLog }, null, 2));
            await page.screenshot({ path: `${spec.out}/web-final-${name}.png` }).catch(() => null);
            await context.close();
        }
    }
} finally {
    writeFileSync(`${spec.out}/web-play.json`, JSON.stringify(receipts, null, 2));
    await browser.close();
}
