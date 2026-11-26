# p14-native-episode-web-queue — channel episodes stay in the native web queue

## Symptom
A materialised Phantom Shows episode streamed over REST and appeared in the
season-scoped `GET /Shows/{series}/Episodes?SeasonId=`, but jellyfin-web's
episode Play action — `getEpisodes(SeriesId, {IsMissing:false,
IsVirtualUnaired:false, limit:100, startItemId})` (all-series, no SeasonId) —
got `Items=[]`/404 and showed "Unable to find a valid media source to play"
without issuing PlaybackInfo.

## Root cause (core channel refresh, not Series.GetEpisodes)
`Series.GetEpisodes` queries persisted BaseItems by `SeriesPresentationUniqueKey`
(+ Seasons), while `Season.GetEpisodes` for a channel season re-asks the
channel (`ChannelManager.GetChannelItemsInternal`), which re-parents the items
under the season. So the season view self-heals and the all-series view does not.

What breaks the persisted hierarchy is `ChannelManager`:
- `RefreshLatestChannelItems` (every Home "Latest in Phantom Shows" load, issued
  userless at the channel root) gets the plugin's O(recent) flat list of
  materialised episodes. `GetChannelItemEntityAsync` set `ParentId = channel`
  for those EXISTING episodes; the queued metadata save then re-derived
  `SeriesId/SeasonId = Empty`, `SeriesPresentationUniqueKey = null`; the
  root dead-id prune deleted every Series (with its Seasons/sibling episodes)
  not in that list — including the episode's own Series (`/Shows/{id}` 404).
- `RefreshChannelItemAsync` (patch 0002, post-materialise/eviction/source swap)
  also passed the channel as parent folder and moved the episode to the root.

Reproduced with REST alone on the isolated rig (queue = 8 → Latest → Series
404, episode `ParentId=channel`, `SeriesId=00000000…`).

## Fix — `scripts/jellyfin-patches/0007-Keep-channel-hierarchy-during-latest-and-item-refresh.patch`
- Latest refresh and per-item refresh are **non-authoritative** for hierarchy:
  an existing item keeps its current parent when that parent is still a live
  folder of the same channel (`IsValidChannelParent`). New items, stale/deleted
  or foreign parents, and ordinary folder browses behave as before.
- The latest prune keeps the parent chain (`GetParentChainIds`, bounded,
  cycle-safe) of every item it keeps, so the materialised episode's Season and
  Series survive. Series not related to a latest item are still pruned exactly
  as before (no new Home-load cost; plugin unchanged).
- Visible effect: Home "Latest in Phantom Shows" now shows the native Series
  tile (core groups episodes under `LatestItemsIndexContainer`) instead of a
  detached episode; movie Latest is unchanged (root-level items).
- Not changed: plugin, IsMissing filters, web queue, DBs, movie behaviour,
  missing/unavailable policy (channel episodes are Remote, never Missing).

## Verification
`./scripts/tests/native-episode-web-queue.test.sh` (mandatory; no skip):
`git apply --check` of 0001..0007 on pristine v10.11.9; 13 patched
`ChannelManager*` tests; then `isolated-playback-rig.py --backend sqlite
--scenario both --episode-web-queue`: movie parity, then episode with a
non-admin user — unscoped + season queues (exact order, Series/Season ids,
IsMissing=true empty), persisted hierarchy after materialise, after Home
Latest, after a second native-open without re-browse, after a second Latest;
pinned sha256 jellyfin-web 10.11.11 in Chromium (desktop 1280x900 and mobile
390x844): login → Home → details → Play → web's own all-series queue = 8,
PlaybackInfo 200, `<video>` advances, nonzero stream bytes. Evidence retained in
`artifacts/playback-parity-*/` (build/patch/web/plugin hashes, raw responses,
`tv-rows-*.json`, `web-play.json`, screenshots). Unpatched server fails at the
first Home Latest (Series pruned).

Deployment: production needs the rebuilt patched `Jellyfin.LiveTv.dll`
(`install.sh --build` / image pipeline that applies `scripts/jellyfin-patches`).
