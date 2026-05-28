# Design: one shared provider torrent per season-split pack

## Problem

Season-split grabs fan one real pack into **N sibling torrents** in rdt-client —
one per season (and, for partial seasons, one per episode). Each sibling row has:

- a unique synthetic `Hash`,
- the same `SeasonSplitRealHash` (the real pack's infohash),
- its own `IncludeRegex` (the season/episode slice it wants).

Today each sibling is handled **independently** against the provider:

1. **Add** — `TorrentRunner` dequeues each sibling → `Torrents.DequeueFromDebridQueue`
   → `IDebridClient.AddTorrentMagnet` → TorBox `AddMagnetAsync` (on the
   rate-limited `TORBOX_CLIENT_SLOW` client). **N adds for one pack.**
2. **Poll** — `UpdateData` calls TorBox `GetInfo(RdId)` per sibling each tick.
   **N status polls for one pack.**
3. **Request links** — each sibling calls `RequestDownloadAsync` for its files.

On **Real-Debrid** this is mostly free: RD dedupes by infohash, so all N adds
return the *same* RD torrent id, and `SelectFiles` already detects the shared
torrent (selects all files once, siblings skip re-selecting — see
`RealDebridDebridClient.SelectFiles`). RD's API is also far more permissive.

On **TorBox** it is not free: even though TorBox dedupes the torrent server-side,
rdt-client still issues **N AddMagnet + N status-poll calls per pack**, all on the
slow/strict client. A big multi-season spree (e.g. 8 Parts Unknown episodes = 8
sibling torrents) multiplies API traffic and trips TorBox's request limit →
`429` storm → 24h account **cooldown**. Per-episode grabbing (correct for import)
makes N larger and the problem worse.

## Goal

Interact with the provider **once per real pack**, not once per sibling:

- **add the real pack to TorBox once**,
- **poll its status once per tick** and fan the result out to all siblings,
- keep each sibling's local scope (`IncludeRegex`) for picking which files it
  downloads/imports.

Target: collapse per-pack provider API calls from `O(N siblings)` to `O(1)`.
Keep Sonarr's per-season/per-episode queue items intact (those are local, and
correct — see the per-episode import fix).

## Key invariant

`SeasonSplitRealHash` is the unit of provider interaction. All provider calls
(add, status, delete) are keyed by it and done once; everything downstream of
"which files does THIS sibling download/import" stays per-sibling via
`IncludeRegex` + the local downloader.

## Design

### Phase 1 — add once (the big win, lowest risk)

In the add-to-provider path, before calling `AddTorrentMagnet`, look for a
sibling (same `SeasonSplitRealHash`, non-empty) that already has an `RdId`:

- **found** → copy its `RdId` + `RdAdded` onto this torrent and **skip the
  provider add call entirely**. The sibling is now attached to the shared
  provider torrent.
- **not found** (first sibling) → add to the provider as today, store `RdId`.

Where: `Torrents.DequeueFromDebridQueue` (or a guard just before it in
`TorrentRunner`'s `torrentsToAddToProvider` loop). Prefer the service layer so it
is provider-agnostic (RD also benefits from skipping redundant adds).

**Concurrency:** if several siblings dequeue in the same tick, all may see "no
RdId yet" and all add. Two options:
- (a) **dequeue throttle per real hash** — in the `torrentsToAddToProvider`
  selection, allow at most one sibling *without* an `RdId` per `SeasonSplitRealHash`
  per tick; once it has an `RdId`, the rest copy it next tick. Simple, no locks.
- (b) a `SemaphoreSlim` keyed by `SeasonSplitRealHash` around the add. Faster
  convergence, slightly more code.

Recommend (a): it's a one-line `GroupBy(SeasonSplitRealHash)` filter on the
existing dequeue list and naturally serializes the first add.

**First-add failure:** if the first sibling's add errors (infringing / hard
fail), the others must not wait forever. The erroring torrent is marked complete
with the error as today; the next tick a *different* sibling becomes "first" and
retries. If the pack is genuinely bad, each sibling eventually errors out (same
as today, just serialized). Acceptable.

### Phase 2 — poll once

`UpdateData(torrent, torrentClientTorrent)` already accepts a pre-fetched
`DebridClientTorrent`. In `TorrentRunner`'s update loop, **group active torrents
by `RdId`, call `GetInfo(RdId)` once per group, and pass the cached result to
every sibling's `UpdateData`**. Each sibling still keeps its synthetic `RdName`
(already guarded) and computes its own completion from its `IncludeRegex` slice.

This cuts status polls from N→1 per pack per tick — the second-largest call
source after adds.

### Phase 3 — delete with reference counting

`FinishedAction` (RemoveRealDebrid / RemoveAllTorrents) must **not** delete the
shared provider torrent while another sibling still needs it. Before deleting on
the provider, check for any other torrent with the same `SeasonSplitRealHash`
that is not yet complete; only the **last** sibling actually issues the provider
delete. (`RealDebridDebridClient.Delete` already has sibling-aware guarding to
generalize from.)

## What stays per-sibling (unchanged)

- `IncludeRegex` and `DownloadableFileFilter` — each sibling still downloads only
  its slice of the shared pack locally.
- The synthetic `RdName` / queue item per season/episode — Sonarr still sees one
  item per season/episode and imports each independently (per-episode fix).
- `RequestDownloadAsync` is per (torrentId, fileId); siblings need different
  files, so the union is bounded by files actually wanted — not an N multiplier.
  Optional later optimization: dedup link requests across siblings that overlap
  on a file.

## Edge cases & risks

- **Add race** — handled by per-real-hash dequeue throttle (Phase 1a).
- **Sibling added before the fix** (existing N-RdId torrents) keep working; the
  change only affects newly-dequeued siblings. **No DB migration needed** — reuse
  `SeasonSplitRealHash` / `RdId` / `IncludeRegex`.
- **DownloadLimit accounting** — `MaxParallelDownloads` currently counts torrents;
  N siblings of one pack count as N but are one provider torrent. Optionally count
  "downloading" by distinct `RdId` so a single pack doesn't eat N slots. Minor;
  can defer.
- **Provider that does NOT dedupe by hash** — design is still correct (we add
  once and reference it); if a future provider needs the magnet re-sent, the
  first-sibling add covers it.
- **Status divergence** — sibling "done" = its filtered files downloaded locally,
  not the whole pack. Keep completion local; only the provider-side status is
  shared. Already how RD behaves.

## Test plan

- Unit: dequeue-throttle picks exactly one sibling per `SeasonSplitRealHash` when
  none has an `RdId`; copies `RdId` to the rest once set.
- Unit: delete reference-count — provider delete only on the last sibling.
- Integration (mock debrid): grab a 4-episode partial season →
  - exactly **1** `AddTorrentMagnet` call,
  - 4 local torrents sharing one `RdId`,
  - **1** `GetInfo` per tick,
  - each sibling downloads only its `IncludeRegex` file.
- Regression: single (non-season-split) torrents unaffected (no
  `SeasonSplitRealHash` → all branches no-op).

## Rollout

Ship Phase 1 alone first (biggest rate-limit relief, smallest surface), verify
TorBox add-call count drops to 1/pack in logs, then add Phases 2–3. All phases
are behind the `SeasonSplitRealHash != null` check, so non-split torrents are
untouched.
