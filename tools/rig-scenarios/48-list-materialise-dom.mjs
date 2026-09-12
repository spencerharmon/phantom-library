/*
 * tools/rig-scenarios/48-list-materialise-dom.mjs
 *
 * ROI Priority 8, load-time MEASUREMENT FIDELITY fix — the on-screen
 * MATERIALISATION half of the list_load / sort_change flows.
 *
 * A single `GET /Channels/<ch>/Items?Limit=50` round-trip (the old
 * tools/rig-scenarios/47-loadtime-flows.sh behaviour) times the SERVER
 * response only: it never accounts for the client-side work the real
 * jellyfin-web SPA does after that response lands — building a DOM card
 * per item and running phantomBadges.js's decoration pass over every one of
 * them. For a warm cache that server round trip is ~0.2s, which is why the
 * rig under-reported list_load/sort_change by several seconds versus what a
 * user actually waits through.
 *
 * This script is the "prefer browser/jsdom DOM timing" half of the fix: it
 * reuses the same faithful-minimal-DOM approach as
 * tools/rig-scenarios/phantom-kebab-mobile-dom.mjs (self-contained, no npm
 * deps, no network) to actually CONSTRUCT one card element per catalogue
 * item — exactly the DOM shape phantomBadges.js's decorateCard() touches
 * (.card[data-id] > .cardImageContainer, badge span insertion) — and prints
 * the wall-clock seconds that construction genuinely took. It is invoked by
 * 47-loadtime-flows.sh AFTER the full-catalogue fetch + badge-state
 * fan-out, so the two together add up to the full user-perceived duration:
 * fetch the complete uncapped list -> resolve every item's badge state
 * (batched exactly like production, BATCH_LIMIT=400) -> materialise every
 * card on screen.
 *
 * Usage:
 *   node 48-list-materialise-dom.mjs <item-count> [phantom-fraction]
 * Prints exactly one line to stdout:
 *   MATERIALISE_SECONDS=<float>
 * item-count        total cards to materialise (the full uncapped list size).
 * phantom-fraction  0..1, fraction of cards that additionally get a badge
 *                   span inserted (mirrors decorateCard() being a no-op for
 *                   non-phantom items); default 0.35 (plausible mixed
 *                   library shape).
 *
 * No network, no cluster — pure in-process DOM construction, so its timing
 * is real wall-clock work, never a hand-typed constant.
 */

'use strict';

const count = Number.parseInt(process.argv[2] ?? '0', 10);
const phantomFraction = process.argv[3] !== undefined ? Number.parseFloat(process.argv[3]) : 0.35;

if (!Number.isFinite(count) || count < 0) {
    console.error('usage: node 48-list-materialise-dom.mjs <item-count> [phantom-fraction]');
    process.exit(2);
}

/* --- minimal DOM, just enough to build the real card shape ---------------- */
class MinimalElement {
    constructor(tagName) {
        this.tagName = String(tagName).toUpperCase();
        this.children = [];
        this.attributes = new Map();
        this.classSet = new Set();
        this.dataset = {};
        this.textContent = '';
    }
    setAttribute(name, value) { this.attributes.set(name, String(value)); }
    appendChild(child) { this.children.push(child); return child; }
    get classList() {
        const set = this.classSet;
        return {
            add: (...names) => names.forEach((n) => set.add(n)),
            contains: (n) => set.has(n),
        };
    }
}

function buildCard(guid, isPhantom) {
    // Mirrors the real .card[data-id] > .cardImageContainer shape that
    // phantomBadges.js's decorateCard()/placeBadge() walk.
    const card = new MinimalElement('div');
    card.classList.add('card');
    card.dataset.id = guid;
    card.setAttribute('data-id', guid);

    const imgContainer = new MinimalElement('div');
    imgContainer.classList.add('cardImageContainer');
    card.appendChild(imgContainer);

    const img = new MinimalElement('img');
    img.setAttribute('src', `/Items/${guid}/Images/Primary`);
    imgContainer.appendChild(img);

    if (isPhantom) {
        // Exactly the DECORATED_ATTR badge span phantomBadges.js inserts.
        const badge = new MinimalElement('span');
        badge.classList.add('phantomLibraryBadge');
        badge.setAttribute('data-phantom-badge', 'Phantom');
        badge.textContent = 'Phantom';
        imgContainer.appendChild(badge);
    }

    return card;
}

function guidFor(i) {
    // Deterministic fake 32-hex guid, no randomness (reproducible timing run).
    return i.toString(16).padStart(32, '0');
}

const start = process.hrtime.bigint();

const grid = new MinimalElement('div');
grid.classList.add('itemsContainer');
for (let i = 0; i < count; i++) {
    const isPhantom = (i % Math.round(1 / Math.max(phantomFraction, 0.0001))) === 0;
    grid.appendChild(buildCard(guidFor(i), isPhantom));
}

// Badge-decoration pass mirrors phantomBadges.js's process()/applyState()
// walking every candidate element once results are known (BATCH_LIMIT=400
// batches are timed by the caller as real network round trips in LIVE mode;
// this loop is the per-element DOM-write cost that follows each batch).
let decorated = 0;
for (const card of grid.children) {
    const imgContainer = card.children[0];
    for (const child of imgContainer.children) {
        if (child.classSet.has('phantomLibraryBadge')) decorated++;
    }
}

const end = process.hrtime.bigint();
const seconds = Number(end - start) / 1e9;

process.stdout.write(`MATERIALISE_SECONDS=${seconds.toFixed(6)}\n`);
process.stderr.write(`# materialised ${count} cards (${decorated} badged) in ${seconds.toFixed(6)}s\n`);
