# rdt-client — seasonsplit fork

> This is a **fork of [rogerfar/rdt-client](https://github.com/rogerfar/rdt-client)**
> with a small extension that makes it the download-client half of the
> **[gemivnet/Sonarr](https://github.com/gemivnet/Sonarr)** season-split fork.
> Everything not described here is unchanged upstream rdt-client — see the
> [README](./README.md).

## Why this fork exists

The Sonarr fork splits a multi-season torrent pack into per-season "synthetic"
grabs. Each sibling grab points at the **same** underlying Real-Debrid torrent,
but Sonarr needs them tracked as distinct queue items and only wants one
season's files imported per grab. Stock rdt-client can't do that: it keys
torrents by the magnet's real infohash (so siblings collapse into one) and has
only a global include-regex.

This fork adds two **optional, backwards-compatible** inputs on the qBittorrent
`torrents/add` endpoint. When absent, behaviour is identical to upstream.

## What changed

Two new fields on `QBTorrentsAddRequest`, also accepted as `x.` parameters
embedded in the magnet URL (so they survive any qBit-API client):

| Field / magnet param | Purpose |
|---|---|
| `realMagnet` / `x.realmagnet=` | The real pack magnet sent to the debrid provider. The local torrent hash is taken from the (synthetic) magnet in `urls`, so sibling seasons stay distinct in the DB and qBit API while sharing one RD download. |
| `includeRegex` / `x.includeseasons=` | Per-torrent include-regex override (e.g. `(?i)\bS19\b`). Only files matching it are materialised, so one season's episodes land instead of the whole pack. |

Touched files:
- `server/RdtClient.Web/Controllers/QBittorrentController.cs` — accept the two new request fields.
- `server/RdtClient.Service/Services/QBittorrent.cs` — thread them into the torrent.
- `server/RdtClient.Service/Services/Torrents.cs` — `ExtractSeasonSplitParams` pulls `x.realmagnet`/`x.includeseasons` out of the magnet; `AddMagnetToDebridQueue` uses the real magnet for the provider but the synthetic hash locally.

All changes log at Information level with a `[SeasonSplit]` prefix, e.g.
`[SeasonSplit] TorrentsAdd received: ... includeRegex='(?i)\bS19\b'`.

## Keeping up to date

```bash
git remote add upstream https://github.com/rogerfar/rdt-client.git   # one time
git fetch upstream
git rebase upstream/main seasonsplit
# Conflicts, if any, are confined to the three files above.
```

The build workflow pins the embedded version to upstream's latest release tag so
the in-app "update available" banner stays quiet.

## Docker image

Pushed on every commit to the `seasonsplit` branch by
`.github/workflows/seasonsplit-image.yml`:

- `ghcr.io/gemivnet/rdt-client-seasonsplit:latest`

Use it together with `ghcr.io/gemivnet/sonarr-seasonsplit:latest`. The Sonarr
fork's [`FORK.md`](https://github.com/gemivnet/Sonarr/blob/seasonsplit/FORK.md)
documents the full end-to-end flow.
