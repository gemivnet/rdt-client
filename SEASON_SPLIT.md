# rdt-client — seasonsplit fork

> This is a **fork of [rogerfar/rdt-client](https://github.com/rogerfar/rdt-client)**,
> the download-client side of the **[gemivnet/Sonarr](https://github.com/gemivnet/Sonarr)**
> season-split setup. Everything not described here is unchanged upstream
> rdt-client — see the [README](./README.md).

## What this fork is (and isn't) anymore

An earlier version of this fork added per-season **"sibling"** machinery: extra
`realMagnet` / `includeRegex` inputs on the qBittorrent `torrents/add` endpoint,
synthetic-hash bookkeeping, and a reference-counted shared-provider-torrent
delete — so one real pack could be fanned out into N per-season torrents inside
rdt-client. **That approach has been removed.** The Sonarr fork now grabs a
multi-season pack as a **single** download and lets Sonarr's per-file import
place each episode, so rdt-client no longer needs to know anything about seasons.

> If you find references to `x.realmagnet`, `x.includeseasons`,
> `ExtractSeasonSplitParams`, or a `SeasonSplitDeleteLock` in older docs or commit
> messages, that code no longer exists (`git grep` finds zero hits in `server/`).

## What the fork actually changes today

It is now a **TorBox-focused rdt-client with resilience hardening** for the large,
whole-pack downloads the season-split workflow produces. The additions over
upstream:

- **Stuck-state recovery** — torrents frozen at "Not Yet Added to Provider" are
  reconciled and recovered instead of hanging, and a single slow per-torrent
  provider lookup can no longer freeze the whole poll loop (`Services/Torrents.cs`).
- **Stall detection + stub guards** — downloads that stop progressing, or where
  the provider returns non-video stubs / fewer usable links than expected, are
  failed fast instead of sitting at 100% forever
  (`Services/DownloadClient.cs`, `Services/TorrentRunner.cs`).
- **Provider circuit breaker** — added to the provider resilience pipeline so a
  flapping provider backs off instead of hammering the API (`DiConfig.cs`).
- **Error visibility** — the underlying provider error is surfaced through the
  qBittorrent `TorrentInfo` (`rdt_error`) so Sonarr can show why a grab failed
  (`Helpers/TorrentDtoMapper.cs`).
- **`ssmetadata` endpoint** — returns a torrent's file list from the provider
  *without* adding a download, used to preview a pack's contents
  (`Web/Controllers/QBittorrentController.cs`).

Deployment is **TorBox-only**; the RealDebrid / AllDebrid / Premiumize clients
were reverted to upstream behaviour.

## Keeping up to date

```bash
git remote add upstream https://github.com/rogerfar/rdt-client.git   # one time
git fetch upstream
git rebase upstream/main seasonsplit
```

The build workflow pins the embedded version to upstream's latest release tag so
the in-app "update available" banner stays quiet.

## Docker image

Pushed on every commit to the `seasonsplit` branch by
[`.github/workflows/seasonsplit-image.yml`](.github/workflows/seasonsplit-image.yml):

- `ghcr.io/gemivnet/rdt-client-seasonsplit:latest`

Use it together with `ghcr.io/gemivnet/sonarr-seasonsplit:latest`. The Sonarr
fork's [`FORK.md`](https://github.com/gemivnet/Sonarr/blob/seasonsplit/FORK.md)
documents the multi-season grab behaviour.

---

> **Leftover field.** The database still has a `SeasonSplitRealHash` column
> (migration `20260523140000`) plus a few DTO/UI references, left from the removed
> sibling design to avoid an EF round-trip. Nothing writes it anymore, so it is
> always null and inert (`IsSeasonSplit` is therefore always false). Safe to excise
> in a future cleanup.
