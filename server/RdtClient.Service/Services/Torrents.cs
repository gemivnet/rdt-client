using System.Globalization;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using RdtClient.Data.Data;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Data.Models.DebridClient;
using RdtClient.Data.Models.Internal;
using RdtClient.Service.BackgroundServices;
using RdtClient.Service.Helpers;
using RdtClient.Service.Services.DebridClients;
using RdtClient.Service.Wrappers;
using Torrent = RdtClient.Data.Models.Data.Torrent;

namespace RdtClient.Service.Services;

public class Torrents(
    ILogger<Torrents> logger,
    ITorrentData torrentData,
    IDownloads downloads,
    IProcessFactory processFactory,
    IFileSystem fileSystem,
    IEnricher enricher,
    AllDebridDebridClient allDebridDebridClient,
    PremiumizeDebridClient premiumizeDebridClient,
    RealDebridDebridClient realDebridDebridClient,
    DebridLinkClient debridLinkClient,
    TorBoxDebridClient torBoxDebridClient)
{
    private static readonly SemaphoreSlim RealDebridUpdateLock = new(1, 1);

    private static readonly SemaphoreSlim TorrentResetLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };

    private IDebridClient DebridClient
    {
        get
        {
            return Settings.Get.Provider.Provider switch
            {
                Provider.Premiumize => premiumizeDebridClient,
                Provider.RealDebrid => realDebridDebridClient,
                Provider.AllDebrid => allDebridDebridClient,
                Provider.DebridLink => debridLinkClient,
                Provider.TorBox => torBoxDebridClient,
                _ => throw new("Invalid Provider")
            };
        }
    }

    public virtual (Int64 Speed, Int64 BytesTotal, Int64 BytesDone) GetDownloadStats(Guid downloadId)
    {
        return TorrentRunner.GetStats(downloadId);
    }

    public virtual async Task<IList<Torrent>> Get()
    {
        var torrents = await torrentData.Get();

        return torrents;
    }

    public virtual async Task<Torrent?> GetByHash(String hash)
    {
        var torrent = await torrentData.GetByHash(hash);

        if (torrent != null)
        {
            await UpdateTorrentClientData(torrent);
        }

        return torrent;
    }

    public async Task UpdateCategory(String hash, String? category)
    {
        var torrent = await torrentData.GetByHash(hash);

        if (torrent == null)
        {
            return;
        }

        Log($"Update category to {category}", torrent);

        await torrentData.UpdateCategory(torrent.TorrentId, category);
    }

    public virtual async Task<Torrent> AddNzbLinkToDebridQueue(String nzbLink, Torrent torrent)
    {
        torrent.RdStatus = TorrentStatus.Queued;

        try
        {
            var uri = new Uri(nzbLink);
            var lastSegment = uri.Segments.LastOrDefault()?.TrimEnd('/');
            torrent.RdName = !String.IsNullOrWhiteSpace(lastSegment) ? lastSegment : "Unknown NZB";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{ex.Message}, trying to parse {nzbLink}", ex.Message, nzbLink);

            throw new($"{ex.Message}, trying to parse {nzbLink}");
        }

        var nzbHash = ComputeMd5Hash(nzbLink);
        var nzbNewTorrent = await AddQueued(nzbHash, nzbLink, false, DownloadType.Nzb, torrent);
        Log($"Adding {nzbLink} with hash {nzbHash} (nzb link) to queue");

        await CopyAddedTorrent(nzbNewTorrent);

        return nzbNewTorrent;
    }

    public virtual async Task<Torrent> AddNzbFileToDebridQueue(Byte[] bytes, String? fileName, Torrent torrent)
    {
        torrent.RdName = fileName ?? "Unknown NZB";
        torrent.RdStatus = TorrentStatus.Queued;

        try
        {
            using var stream = new MemoryStream(bytes);

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null
            };

            using var reader = XmlReader.Create(stream, settings);
            var doc = XDocument.Load(reader);
            var nzbNamespace = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

            var title = doc.Root?
                           .Elements(nzbNamespace + "head")
                           .Elements(nzbNamespace + "meta")
                           .FirstOrDefault(x => x.Attribute("type")?.Value == "name")
                           ?
                           .Value;

            if (String.IsNullOrWhiteSpace(title))
            {
                title = doc.Root?
                           .Elements(nzbNamespace + "head")
                           .Elements(nzbNamespace + "meta")
                           .FirstOrDefault(x => x.Attribute("type")?.Value == "title")
                           ?
                           .Value;
            }

            if (!String.IsNullOrWhiteSpace(title))
            {
                torrent.RdName = title.Trim();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{ex.Message}, trying to parse NZB file contents", ex.Message);

            throw new($"{ex.Message}, trying to parse NZB file contents");
        }

        var nzbHash = ComputeMd5HashFromBytes(bytes);
        var nzbFileAsBase64 = Convert.ToBase64String(bytes);
        var nzbNewTorrent = await AddQueued(nzbHash, nzbFileAsBase64, true, DownloadType.Nzb, torrent);
        Log($"Adding {nzbHash} (nzb file) to queue", nzbNewTorrent);

        await CopyAddedTorrent(nzbNewTorrent);

        return nzbNewTorrent;
    }

    public virtual async Task<Torrent> AddMagnetToDebridQueue(String magnetLink, Torrent torrent, String? realMagnet = null)
    {
        // SeasonSplit: a synthetic magnet from a Sonarr fork carries two
        // optional `x.` query parameters that override how we talk to the
        // debrid provider. We strip them out of the magnet before parsing
        // so MonoTorrent.MagnetLink doesn't choke on unknown params.
        var (cleanMagnet, embeddedRealMagnet, embeddedSeasons) = ExtractSeasonSplitParams(magnetLink);

        var effectiveRealMagnet = !String.IsNullOrWhiteSpace(realMagnet) ? realMagnet : embeddedRealMagnet;

        // Defensive: older Sonarr-fork builds shipped the *synthetic* magnet in
        // the realMagnet form param (it carries a synthetic xt plus a nested
        // x.realmagnet=). Peel any wrapper off so the debrid provider always
        // receives a resolvable magnet rather than a synthetic infohash.
        if (!String.IsNullOrWhiteSpace(effectiveRealMagnet))
        {
            var (peeledClean, peeledReal, _) = ExtractSeasonSplitParams(effectiveRealMagnet);
            effectiveRealMagnet = !String.IsNullOrWhiteSpace(peeledReal) ? peeledReal : peeledClean;
        }
        // Embedded x.includeseasons drives the per-season file filter. Apply it
        // when there's no IncludeRegex yet OR when the only IncludeRegex is the
        // global default — the magnet's explicit season is more specific than a
        // catch-all default. An explicit per-torrent override (which differs from
        // the default) still wins.
        var defaultIncludeRegex = Settings.Get.Integrations.Default.IncludeRegex;
        if (!String.IsNullOrWhiteSpace(embeddedSeasons) &&
            (String.IsNullOrWhiteSpace(torrent.IncludeRegex) ||
             String.Equals(torrent.IncludeRegex, defaultIncludeRegex, StringComparison.Ordinal)))
        {
            torrent.IncludeRegex = SeasonsToIncludeRegex(embeddedSeasons);
            logger.LogInformation("[SeasonSplit] Magnet-embedded seasons={seasons} -> IncludeRegex='{regex}'",
                                  embeddedSeasons, torrent.IncludeRegex);
        }

        // SeasonSplit: when realMagnet is supplied (via form param or x.realmagnet),
        // hash is taken from the synthetic magnetLink (so siblings stay distinct
        // in the local DB and qBit API), while the real magnet is what we ship
        // to the debrid provider.
        var debridMagnet = String.IsNullOrWhiteSpace(effectiveRealMagnet) ? cleanMagnet : effectiveRealMagnet;
        var enriched = await enricher.EnrichMagnetLink(debridMagnet);
        MagnetLink magnet;

        try
        {
            magnet = MagnetLink.Parse(cleanMagnet);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{ex.Message}, trying to parse {magnetLink}", ex.Message, cleanMagnet);

            throw new($"{ex.Message}, trying to parse {cleanMagnet}");
        }

        if (!String.IsNullOrWhiteSpace(effectiveRealMagnet))
        {
            logger.LogInformation("[SeasonSplit] Using real magnet for debrid (length={debridLen}), local synth hash from urls (length={synthLen})",
                                  debridMagnet.Length, cleanMagnet.Length);

            // Stamp the real pack infohash so the one-to-many handling can find
            // every per-season sibling that shares this Real-Debrid torrent
            // (RD dedups by infohash, so all siblings collapse to one RdId).
            // Several local torrents (distinct synthetic Hash) -> one real hash.
            try
            {
                torrent.SeasonSplitRealHash = MagnetLink.Parse(debridMagnet).InfoHashes.V1OrV2.ToHex();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[SeasonSplit] Could not parse real magnet for SeasonSplitRealHash; one-to-many sibling handling disabled for this torrent");
            }
        }

        if (!String.IsNullOrWhiteSpace(Settings.Get.General.BannedTrackers))
        {
            // Check the trackers of the magnet actually sent to the debrid
            // provider (the real pack magnet for season-split), not the synthetic
            // one — otherwise the banned-tracker filter is bypassed for the real
            // content. They usually share trackers, but don't assume it.
            var trackerCheckUrls = magnet.AnnounceUrls;
            if (!String.IsNullOrWhiteSpace(effectiveRealMagnet))
            {
                try
                {
                    trackerCheckUrls = MagnetLink.Parse(debridMagnet).AnnounceUrls;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[SeasonSplit] Could not parse real magnet for banned-tracker check; using synthetic magnet trackers");
                }
            }

            var bannedTrackers = Settings.Get.General.BannedTrackers.Split(',');

            foreach (var bannedTracker in bannedTrackers)
            {
                var bannedTrackerCompare = bannedTracker.Trim().ToLower();

                if (String.IsNullOrWhiteSpace(bannedTrackerCompare))
                {
                    continue;
                }

                if (trackerCheckUrls != null)
                {
                    var bannedUrls = trackerCheckUrls.Where(m => m.Trim().ToLower().Contains(bannedTrackerCompare)).ToList();

                    if (bannedUrls.Count > 0)
                    {
                        var bannedUrlsString = String.Join(", ", bannedUrls);

                        throw new($"Cannot add torrent, the torrent contains banned trackers: {bannedUrlsString}.");
                    }
                }
            }
        }

        torrent.RdStatus = TorrentStatus.Queued;
        torrent.RdName = magnet.Name;

        var hash = magnet.InfoHashes.V1OrV2.ToHex();
        var newTorrent = await AddQueued(hash, enriched, false, DownloadType.Torrent, torrent);

        Log($"Adding {hash} (magnet link) to queue", newTorrent);
        await CopyAddedTorrent(newTorrent);

        return newTorrent;
    }

    public virtual async Task<Torrent> AddFileToDebridQueue(Byte[] bytes, Torrent torrent)
    {
        var enriched = await enricher.EnrichTorrentBytes(bytes);

        String fileAsBase64;

        MonoTorrent.Torrent monoTorrent;

        if (enriched.SequenceEqual(bytes))
        {
            fileAsBase64 = Convert.ToBase64String(bytes);
            logger.LogDebug($"bytes {bytes}");
        }
        else
        {
            fileAsBase64 = Convert.ToBase64String(enriched);
            logger.LogDebug($"enriched bytes {enriched}");
        }

        try
        {
            monoTorrent = await MonoTorrent.Torrent.LoadAsync(bytes);
        }
        catch (Exception ex)
        {
            throw new($"{ex.Message}, trying to parse {fileAsBase64}");
        }

        if (!String.IsNullOrWhiteSpace(Settings.Get.General.BannedTrackers))
        {
            var bannedTrackers = Settings.Get.General.BannedTrackers.Split(',');

            foreach (var bannedTracker in bannedTrackers)
            {
                var bannedTrackerCompare = bannedTracker.Trim().ToLower();

                if (String.IsNullOrWhiteSpace(bannedTrackerCompare))
                {
                    continue;
                }

                if (!String.IsNullOrWhiteSpace(monoTorrent.Source) && monoTorrent.Source.Contains(bannedTracker))
                {
                    throw new($"Cannot add torrent, the torrent source '{monoTorrent.Source}' is a banned tracker.");
                }

                if (monoTorrent.AnnounceUrls != null)
                {
                    var bannedUrls = monoTorrent.AnnounceUrls.SelectMany(m => m).Where(m => m.Trim().ToLower().Contains(bannedTrackerCompare)).ToList();

                    if (bannedUrls.Count > 0)
                    {
                        var bannedUrlsString = String.Join(", ", bannedUrls);

                        throw new($"Cannot add torrent, the torrent contains banned trackers: {bannedUrlsString}.");
                    }
                }
            }
        }

        torrent.RdStatus = TorrentStatus.Queued;
        torrent.RdName = monoTorrent.Name;

        var hash = monoTorrent.InfoHashes.V1OrV2.ToHex();

        var newTorrent = await AddQueued(hash, fileAsBase64, true, DownloadType.Torrent, torrent);

        Log($"Adding {hash} (torrent file) to queue", newTorrent);

        await CopyAddedTorrent(newTorrent);

        return newTorrent;
    }

    private async Task CopyAddedTorrent(Torrent torrent)
    {
        if (String.IsNullOrWhiteSpace(Settings.Get.General.CopyAddedTorrents) || String.IsNullOrWhiteSpace(torrent.FileOrMagnet) || String.IsNullOrWhiteSpace(torrent.RdName))
        {
            return;
        }

        try
        {
            if (!fileSystem.Directory.Exists(Settings.Get.General.CopyAddedTorrents))
            {
                fileSystem.Directory.CreateDirectory(Settings.Get.General.CopyAddedTorrents);
            }

            var extension = torrent.Type switch
            {
                DownloadType.Nzb => ".nzb",
                DownloadType.Torrent => torrent.IsFile ? ".torrent" : ".magnet",
                _ => throw new ArgumentException("Unexpected DownloadType")
            };

            var copyFileName = Path.Combine(Settings.Get.General.CopyAddedTorrents, FileHelper.RemoveInvalidFileNameChars(torrent.RdName));

            if (!copyFileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                copyFileName += extension;
            }

            if (fileSystem.File.Exists(copyFileName))
            {
                fileSystem.File.Delete(copyFileName);
            }

            if (torrent.IsFile)
            {
                var bytes = Convert.FromBase64String(torrent.FileOrMagnet);
                await fileSystem.File.WriteAllBytesAsync(copyFileName, bytes);
            }
            else
            {
                await fileSystem.File.WriteAllTextAsync(copyFileName, torrent.FileOrMagnet);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Unable to create torrent blackhole directory: {Settings.Get.General.CopyAddedTorrents}: {ex.Message}");
        }
    }

    /// <summary>
    ///     Adds torrent in database to debrid provider and updates database accordingly.
    /// </summary>
    /// <param name="torrent">The torrent from the database to upload to the debrid provider</param>
    /// <returns>Updated torrent</returns>
    /// <exception cref="Exception">When RdId is not null or FileOrMagnet is null.</exception>
    public async Task DequeueFromDebridQueue(Torrent torrent)
    {
        if (torrent.RdId != null)
        {
            throw new("Torrent already added to debrid provider, cannot dequeue");
        }

        if (torrent.FileOrMagnet == null)
        {
            throw new("Torrent has no torrent file or magnet link");
        }

        logger.LogDebug("Adding {hash} to debrid provider {torrentInfo}", torrent.Hash, torrent.ToLog());

        await RealDebridUpdateLock.WaitAsync();

        try
        {
            String id;

            if (torrent.Type == DownloadType.Nzb)
            {
                id = torrent.IsFile
                    ? await DebridClient.AddNzbFile(Convert.FromBase64String(torrent.FileOrMagnet), torrent.RdName)
                    : await DebridClient.AddNzbLink(torrent.FileOrMagnet);
            }
            else
            {
                id = torrent.IsFile
                    ? await DebridClient.AddTorrentFile(Convert.FromBase64String(torrent.FileOrMagnet))
                    : await DebridClient.AddTorrentMagnet(torrent.FileOrMagnet);
            }

            await torrentData.UpdateRdId(torrent, id);

            await UpdateTorrentClientData(torrent);
        }
        finally
        {
            RealDebridUpdateLock.Release();
        }
    }

    public async Task<IList<DebridClientAvailableFile>> GetAvailableFiles(String hash)
    {
        var result = await DebridClient.GetAvailableFiles(hash);

        return result;
    }

    public async Task SelectFiles(Guid torrentId)
    {
        var torrent = await GetById(torrentId);

        if (torrent == null)
        {
            return;
        }

        var selected = await DebridClient.SelectFiles(torrent);

        if (selected == 0)
        {
            await MarkAllFilesExcluded(torrent);
        }
    }

    public async Task CreateDownloads(Guid torrentId)
    {
        var torrent = await GetById(torrentId);

        if (torrent == null)
        {
            return;
        }

        var downloadInfos = await DebridClient.GetDownloadInfos(torrent);

        if (downloadInfos == null)
        {
            return;
        }

        if (downloadInfos.Count == 0)
        {
            await MarkAllFilesExcluded(torrent);

            return;
        }

        foreach (var downloadInfo in downloadInfos)
        {
            var addResult = await downloads.TryAddForTorrent(torrent.TorrentId, downloadInfo);

            switch (addResult)
            {
                case DownloadAddResult.Added:
                case DownloadAddResult.AlreadyExists:
                    continue;
                case DownloadAddResult.TorrentMissing:
                    logger.LogDebug("Stopping download creation because the torrent was deleted concurrently. TorrentId: {torrentId}", torrent.TorrentId);

                    return;
                case DownloadAddResult.InvalidInput:
                    logger.LogDebug("Skipping download creation because the provider returned an invalid download link. TorrentId: {torrentId}", torrent.TorrentId);

                    continue;
                default:
                    throw new ArgumentOutOfRangeException(nameof(addResult), addResult, null);
            }
        }
    }

    /// <summary>
    ///     Logs a message to the console, sets the error on the torrent and ensures it is not retried.
    /// </summary>
    /// <param name="torrent">The torrent to mark as "All files excluded"</param>
    private async Task MarkAllFilesExcluded(Torrent torrent)
    {
        logger.LogInformation("All files excluded by filters (IncludeRegex: {includeRegex}, ExcludeRegex: {excludeRegex}, DownloadMinSize: {downloadMinSize}) {torrentInfo}",
                              torrent.IncludeRegex,
                              torrent.ExcludeRegex,
                              torrent.DownloadMinSize,
                              torrent.ToLog());

        await torrentData.UpdateRetry(torrent.TorrentId, null, torrent.TorrentRetryAttempts);
        await torrentData.UpdateComplete(torrent.TorrentId, "All files excluded", DateTimeOffset.Now, false);
    }

    public virtual async Task Delete(Guid torrentId, Boolean deleteData, Boolean deleteRdTorrent, Boolean deleteLocalFiles)
    {
        var torrent = await GetById(torrentId);

        if (torrent == null)
        {
            return;
        }

        Log($"Deleting", torrent);

        await UpdateComplete(torrentId, "Torrent deleted", DateTimeOffset.UtcNow, false);

        foreach (var download in torrent.Downloads)
        {
            var retry = 10;

            while (TorrentRunner.ActiveDownloadClients.TryGetValue(download.DownloadId, out var downloadClient))
            {
                Log($"Cancelling download", download, torrent);

                await downloadClient.Cancel();

                await Task.Delay(500);

                retry++;

                if (retry > 5)
                {
                    break;
                }
            }

            retry = 10;

            while (TorrentRunner.ActiveUnpackClients.TryGetValue(download.DownloadId, out var unpackClient))
            {
                Log($"Cancelling unpack", download, torrent);

                unpackClient.Cancel();

                await Task.Delay(500);

                retry++;

                if (retry > 10)
                {
                    break;
                }
            }
        }

        if (deleteData)
        {
            Log($"Deleting RdtClient data", torrent);

            await torrentData.Delete(torrentId);
        }

        if (deleteRdTorrent && torrent.RdId != null)
        {
            Log($"Deleting RealDebrid Torrent", torrent);

            try
            {
                await DebridClient.Delete(torrent);
            }
            catch
            {
                // ignored
            }
        }

        if (deleteLocalFiles && !String.IsNullOrWhiteSpace(torrent.RdName))
        {
            var downloadPath = DownloadPath(torrent, Settings.Get);
            downloadPath = Path.Combine(downloadPath, torrent.RdName);

            Log($"Deleting local files in {downloadPath}", torrent);

            if (Directory.Exists(downloadPath))
            {
                var retry = 0;

                while (true)
                {
                    try
                    {
                        Directory.Delete(downloadPath, true);

                        break;
                    }
                    catch
                    {
                        retry++;

                        if (retry >= 3)
                        {
                            throw;
                        }

                        await Task.Delay(1000);
                    }
                }
            }
        }
    }

    public async Task<String> UnrestrictLink(Guid downloadId)
    {
        var download = await downloads.GetById(downloadId) ?? throw new($"Download with ID {downloadId} not found");

        Log("Unrestricting link", download, download.Torrent);

        var unrestrictedLink = await DebridClient.Unrestrict(download.Torrent!, download.Path);

        await downloads.UpdateUnrestrictedLink(downloadId, unrestrictedLink);

        return unrestrictedLink;
    }

    public async Task<String> RetrieveFileName(Guid downloadId)
    {
        var download = await downloads.GetById(downloadId) ?? throw new($"Download with ID {downloadId} not found");

        Log($"Retrieving filename for", download, download.Torrent!);

        var fileName = await DebridClient.GetFileName(download);

        await downloads.UpdateFileName(downloadId, fileName);

        return fileName;
    }

    public async Task<Profile> GetProfile()
    {
        var user = await DebridClient.GetUser();

        var profile = new Profile
        {
            Provider = Enum.GetName(Settings.Get.Provider.Provider),
            UserName = user.Username,
            Expiration = user.Expiration,
            CurrentVersion = UpdateChecker.CurrentVersion,
            LatestVersion = UpdateChecker.LatestVersion,
            IsInsecure = UpdateChecker.IsInsecure,
            DisableUpdateNotification = Settings.Get.General.DisableUpdateNotifications
        };

        return profile;
    }

    public async Task UpdateRdData()
    {
        await RealDebridUpdateLock.WaitAsync();

        var torrents = await Get();

        try
        {
            var rdTorrents = await DebridClient.GetDownloads();
            var torrentsByRdId = CreateTorrentLookupByRdId(torrents);
            var providerTorrentsById = CreateProviderTorrentLookupById(rdTorrents);

            foreach (var rdTorrent in rdTorrents)
            {
                torrentsByRdId.TryGetValue(rdTorrent.Id, out var siblings);

                // Auto import torrents only torrents that have their files selected
                if ((siblings == null || siblings.Count == 0) && Settings.Get.Provider.AutoImport)
                {
                    var newTorrent = new Torrent
                    {
                        Category = Settings.Get.Provider.Default.Category,
                        DownloadClient = Settings.Get.DownloadClient.Client,
                        DownloadAction =
                            Settings.Get.Provider.Default.OnlyDownloadAvailableFiles ? TorrentDownloadAction.DownloadAvailableFiles : TorrentDownloadAction.DownloadAll,
                        HostDownloadAction = Settings.Get.Provider.Default.HostDownloadAction,
                        FinishedActionDelay = Settings.Get.Provider.Default.FinishedActionDelay,
                        FinishedAction = Settings.Get.Provider.Default.FinishedAction,
                        DownloadMinSize = Settings.Get.Provider.Default.MinFileSize,
                        IncludeRegex = Settings.Get.Provider.Default.IncludeRegex,
                        ExcludeRegex = Settings.Get.Provider.Default.ExcludeRegex,
                        TorrentRetryAttempts = Settings.Get.Provider.Default.TorrentRetryAttempts,
                        DownloadRetryAttempts = Settings.Get.Provider.Default.DownloadRetryAttempts,
                        DeleteOnError = Settings.Get.Provider.Default.DeleteOnError,
                        Lifetime = Settings.Get.Provider.Default.TorrentLifetime,
                        Priority = Settings.Get.Provider.Default.Priority > 0 ? Settings.Get.Provider.Default.Priority : null,
                        RdId = rdTorrent.Id
                    };

                    if (newTorrent.RdStatus == TorrentStatus.WaitingForFileSelection)
                    {
                        continue;
                    }

                    var torrent = await torrentData.Add(rdTorrent.Id, rdTorrent.Hash, null, false, DownloadType.Torrent, Settings.Get.DownloadClient.Client, newTorrent);
                    torrentsByRdId[rdTorrent.Id] = [torrent];
                    torrents.Add(torrent);

                    await UpdateTorrentClientData(torrent, rdTorrent);
                }
                else if (siblings != null)
                {
                    // Fan the single RD torrent's state out to every local sibling.
                    // Each carries its own IncludeRegex, so file selection /
                    // download filtering downstream still scopes to its season.
                    foreach (var torrent in siblings)
                    {
                        await UpdateTorrentClientData(torrent, rdTorrent);
                    }
                }
            }

            foreach (var torrent in torrents)
            {
                var rdTorrent = torrent.RdId != null && providerTorrentsById.TryGetValue(torrent.RdId, out var providerTorrent) ? providerTorrent : null;

                if (rdTorrent == null && Settings.Get.Provider.AutoDelete && torrent.RdStatus != TorrentStatus.Queued)
                {
                    await Delete(torrent.TorrentId, true, false, true);
                }
            }
        }
        finally
        {
            RealDebridUpdateLock.Release();
        }
    }

    public async Task RetryTorrent(Guid torrentId, Int32 retryCount)
    {
        await TorrentResetLock.WaitAsync();

        try
        {
            var torrent = await torrentData.GetById(torrentId);

            if (torrent?.Retry == null)
            {
                return;
            }

            Log($"Retrying Torrent", torrent);

            await UpdateComplete(torrent.TorrentId, "Retrying Torrent", DateTimeOffset.UtcNow, false);
            await UpdateRetry(torrent.TorrentId, null, 0);

            foreach (var download in torrent.Downloads)
            {
                await downloads.UpdateError(download.DownloadId, null);
                await downloads.UpdateCompleted(download.DownloadId, DateTimeOffset.UtcNow);
            }

            foreach (var download in torrent.Downloads)
            {
                while (TorrentRunner.ActiveDownloadClients.TryRemove(download.DownloadId, out var downloadClient))
                {
                    await downloadClient.Cancel();

                    await Task.Delay(100);
                }

                while (TorrentRunner.ActiveUnpackClients.TryRemove(download.DownloadId, out var unpackClient))
                {
                    unpackClient.Cancel();

                    await Task.Delay(100);
                }
            }

            await Delete(torrentId, true, true, true);

            if (String.IsNullOrWhiteSpace(torrent.FileOrMagnet))
            {
                throw new($"Cannot re-add this torrent, original magnet or file not found");
            }

            Torrent newTorrent;

            if (torrent.Type == DownloadType.Nzb)
            {
                if (torrent.IsFile)
                {
                    var bytes = Convert.FromBase64String(torrent.FileOrMagnet!);

                    newTorrent = await AddNzbFileToDebridQueue(bytes, torrent.RdName, torrent);
                }
                else
                {
                    newTorrent = await AddNzbLinkToDebridQueue(torrent.FileOrMagnet!, torrent);
                }
            }
            else
            {
                if (torrent.IsFile)
                {
                    var bytes = Convert.FromBase64String(torrent.FileOrMagnet!);

                    newTorrent = await AddFileToDebridQueue(bytes, torrent);
                }
                else
                {
                    newTorrent = await AddMagnetToDebridQueue(torrent.FileOrMagnet!, torrent);
                }
            }

            await torrentData.UpdateRetry(newTorrent.TorrentId, null, retryCount);
        }
        finally
        {
            TorrentResetLock.Release();
        }
    }

    public async Task RetryDownload(Guid downloadId)
    {
        var download = await downloads.GetById(downloadId);

        if (download == null)
        {
            return;
        }

        Log($"Retrying Download", download, download.Torrent);

        while (TorrentRunner.ActiveDownloadClients.TryRemove(download.DownloadId, out var downloadClient))
        {
            await downloadClient.Cancel();

            await Task.Delay(100);
        }

        while (TorrentRunner.ActiveUnpackClients.TryRemove(download.DownloadId, out var unpackClient))
        {
            unpackClient.Cancel();

            await Task.Delay(100);
        }

        var downloadPath = DownloadPath(download.Torrent!, Settings.Get);

        var filePath = DownloadHelper.GetDownloadPath(downloadPath, download.Torrent!, download);

        if (filePath != null)
        {
            Log($"Deleting {filePath}", download, download.Torrent);

            await FileHelper.Delete(filePath);
        }

        Log($"Resetting", download, download.Torrent);

        await downloads.Reset(downloadId);

        await torrentData.UpdateComplete(download.TorrentId, null, null, false);
    }

    public async Task UpdateComplete(Guid torrentId, String? error, DateTimeOffset datetime, Boolean retry)
    {
        await torrentData.UpdateComplete(torrentId, error, datetime, retry);
    }

    public async Task UpdateFilesSelected(Guid torrentId, DateTimeOffset datetime)
    {
        await torrentData.UpdateFilesSelected(torrentId, datetime);
    }

    public async Task<Boolean> UpdateFileSelection(String hash, IReadOnlyCollection<Int32> fileIds, Boolean selected)
    {
        if (fileIds.Count == 0)
        {
            return false;
        }

        var torrent = await torrentData.GetByHash(hash);

        if (torrent == null)
        {
            return false;
        }

        var files = torrent.Files.ToList();

        if (files.Count == 0)
        {
            return false;
        }

        foreach (var fileId in fileIds)
        {
            if (fileId < 0 || fileId >= files.Count)
            {
                continue;
            }

            files[fileId].Selected = selected;
        }

        torrent.RdFiles = JsonSerializer.Serialize(files, JsonSerializerOptions);
        await torrentData.UpdateRdData(torrent);

        return true;
    }

    public async Task UpdatePriority(String hash, Int32 priority)
    {
        var torrent = await torrentData.GetByHash(hash);

        if (torrent == null)
        {
            return;
        }

        await torrentData.UpdatePriority(torrent.TorrentId, priority);
    }

    public async Task UpdateRetry(Guid torrentId, DateTimeOffset? datetime, Int32 retry)
    {
        await torrentData.UpdateRetry(torrentId, datetime, retry);
    }

    public async Task UpdateError(Guid torrentId, String error)
    {
        await torrentData.UpdateError(torrentId, error);
    }

    public async Task<Torrent?> GetById(Guid torrentId)
    {
        var torrent = await torrentData.GetById(torrentId);

        if (torrent == null)
        {
            return null;
        }

        await UpdateTorrentClientData(torrent);

        return torrent;
    }

    private static String DownloadPath(Torrent torrent, DbSettings settings)
    {
        var settingDownloadPath = settings.DownloadClient.DownloadPath;

        if (!String.IsNullOrWhiteSpace(torrent.Category))
        {
            settingDownloadPath = Path.Combine(settingDownloadPath, torrent.Category);
        }

        return settingDownloadPath;
    }

    private async Task<Torrent> AddQueued(String infoHash,
                                          String fileOrMagnetContents,
                                          Boolean isFile,
                                          DownloadType downloadType,
                                          Torrent torrent)
    {
        var existingTorrent = await torrentData.GetByHash(infoHash);

        if (existingTorrent != null)
        {
            return existingTorrent;
        }

        var newTorrent = await torrentData.Add(null,
                                               infoHash,
                                               fileOrMagnetContents,
                                               isFile,
                                               downloadType,
                                               torrent.DownloadClient,
                                               torrent);

        return newTorrent;
    }

    public async Task Update(Torrent torrent)
    {
        await torrentData.Update(torrent);
    }

    public async Task RunTorrentComplete(Guid torrentId, DbSettings? settings = null)
    {
        settings ??= Settings.Get;

        if (String.IsNullOrWhiteSpace(settings.General.RunOnTorrentCompleteFileName))
        {
            return;
        }

        var torrent = await torrentData.GetById(torrentId) ?? throw new($"Cannot find Torrent with ID {torrentId}");

        var downloadsForTorrent = await downloads.GetForTorrent(torrentId);

        var fileName = settings.General.RunOnTorrentCompleteFileName;
        var arguments = settings.General.RunOnTorrentCompleteArguments ?? "";

        Log($"Parsing external program {fileName} with arguments {arguments}", torrent);

        var downloadPath = DownloadPath(torrent, settings);
        var torrentPath = Path.Combine(downloadPath, torrent.RdName ?? "Unknown");

        var filePath = torrentPath;

        var files = fileSystem.Directory.GetFiles(filePath);

        if (files.Length == 1)
        {
            filePath = Path.Combine(torrentPath, files[0]);
        }

        arguments = arguments.Replace("%N", $"\"{torrent.RdName}\"");
        arguments = arguments.Replace("%L", $"\"{torrent.Category}\"");
        arguments = arguments.Replace("%F", $"\"{filePath}\"");
        arguments = arguments.Replace("%R", $"\"{downloadPath}\"");
        arguments = arguments.Replace("%D", $"\"{torrentPath}\"");
        arguments = arguments.Replace("%C", downloadsForTorrent.Count.ToString(CultureInfo.InvariantCulture).Replace(",", "").Replace(".", ""));
        arguments = arguments.Replace("%Z", torrent.RdSize?.ToString(CultureInfo.InvariantCulture).Replace(",", "").Replace(".", ""));
        arguments = arguments.Replace("%I", torrent.Hash);

        Log($"Executing external program {fileName} with arguments {arguments}", torrent);

        var errorSb = new StringBuilder();
        var outputSb = new StringBuilder();

        using var process = processFactory.NewProcess();

        process.StartInfo.FileName = fileName;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        process.OutputDataReceived += (_, data) =>
        {
            if (data == null)
            {
                return;
            }

            outputSb.AppendLine(data.Trim());
        };

        process.ErrorDataReceived += (_, data) =>
        {
            if (data == null)
            {
                return;
            }

            errorSb.AppendLine(data.Trim());
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var exited = process.WaitForExit(60000 * 10);

        var errors = errorSb.ToString();
        var output = outputSb.ToString();

        if (errors.Length > 0)
        {
            Log($"External application exited with errors: {errors}", torrent);
        }

        if (output.Length > 0)
        {
            Log($"External application exited with output: {output}", torrent);
        }

        if (!exited)
        {
            Log("External application after a 60 second timeout", torrent);
        }
    }

    private async Task UpdateTorrentClientData(Torrent torrent, DebridClientTorrent? torrentClientTorrent = null)
    {
        try
        {
            var originalTorrent = CaptureRdState(torrent);

            await DebridClient.UpdateData(torrent, torrentClientTorrent);

            var newTorrent = CaptureRdState(torrent);

            if (originalTorrent != newTorrent)
            {
                await torrentData.UpdateRdData(torrent);
            }
        }
        catch (Exception)
        {
            // ignored
        }
    }

    // One RdId can map to MANY local torrents: Real-Debrid dedups by infohash,
    // so every per-season season-split "sibling" (distinct synthetic Hash, same
    // real pack magnet) collapses onto a single RD torrent id. A plain
    // last-writer-wins dictionary would silently drop all but one sibling and
    // only that one season would ever download — hence the list-valued lookup.
    private static Dictionary<String, List<Torrent>> CreateTorrentLookupByRdId(IEnumerable<Torrent> torrents)
    {
        var lookup = new Dictionary<String, List<Torrent>>(StringComparer.Ordinal);

        foreach (var torrent in torrents)
        {
            if (String.IsNullOrWhiteSpace(torrent.RdId))
            {
                continue;
            }

            if (!lookup.TryGetValue(torrent.RdId, out var siblings))
            {
                siblings = [];
                lookup[torrent.RdId] = siblings;
            }

            siblings.Add(torrent);
        }

        return lookup;
    }

    private static Dictionary<String, DebridClientTorrent> CreateProviderTorrentLookupById(IEnumerable<DebridClientTorrent> torrents)
    {
        var lookup = new Dictionary<String, DebridClientTorrent>(StringComparer.Ordinal);

        foreach (var torrent in torrents)
        {
            if (!String.IsNullOrWhiteSpace(torrent.Id))
            {
                lookup[torrent.Id] = torrent;
            }
        }

        return lookup;
    }

    private static TorrentRdState CaptureRdState(Torrent torrent)
    {
        return new(torrent.RdName,
                   torrent.RdSize,
                   torrent.RdHost,
                   torrent.RdSplit,
                   torrent.RdProgress,
                   torrent.RdStatus,
                   torrent.RdStatusRaw,
                   torrent.RdAdded,
                   torrent.RdEnded,
                   torrent.RdSpeed,
                   torrent.RdSeeders,
                   torrent.RdFiles);
    }

    private readonly record struct TorrentRdState(String? RdName,
                                                  Int64? RdSize,
                                                  String? RdHost,
                                                  Int64? RdSplit,
                                                  Int64? RdProgress,
                                                  TorrentStatus? RdStatus,
                                                  String? RdStatusRaw,
                                                  DateTimeOffset? RdAdded,
                                                  DateTimeOffset? RdEnded,
                                                  Int64? RdSpeed,
                                                  Int64? RdSeeders,
                                                  String? RdFiles);

    private void Log(String message, Download? download, Torrent? torrent)
    {
        if (download != null)
        {
            message = $"{message} {download.ToLog()}";
        }

        if (torrent != null)
        {
            message = $"{message} {torrent.ToLog()}";
        }

        logger.LogDebug(message);
    }

    private void Log(String message, Torrent? torrent = null)
    {
        if (torrent != null)
        {
            message = $"{message} {torrent.ToLog()}";
        }

        logger.LogDebug(message);
    }

    private static String ComputeSha1Hash(String input)
    {
        using var sha1 = SHA1.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = sha1.ComputeHash(bytes);

        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    private static String ComputeMd5Hash(String input)
    {
        using var md5 = MD5.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = md5.ComputeHash(bytes);

        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    private static String ComputeMd5HashFromBytes(Byte[] bytes)
    {
        using var md5 = MD5.Create();
        var hashBytes = md5.ComputeHash(bytes);

        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    // SeasonSplit helpers: pulls the x.realmagnet / x.includeseasons hints out
    // of the magnet URL and returns a cleaned magnet plus the extracted values.
    private static (String CleanMagnet, String? RealMagnet, String? Seasons) ExtractSeasonSplitParams(String magnetLink)
    {
        if (String.IsNullOrEmpty(magnetLink) ||
            (magnetLink.IndexOf("x.realmagnet=", StringComparison.OrdinalIgnoreCase) < 0 &&
             magnetLink.IndexOf("x.includeseasons=", StringComparison.OrdinalIgnoreCase) < 0))
        {
            return (magnetLink, null, null);
        }

        var queryIdx = magnetLink.IndexOf('?');
        if (queryIdx < 0)
        {
            return (magnetLink, null, null);
        }

        var prefix = magnetLink.Substring(0, queryIdx + 1);
        var query = magnetLink.Substring(queryIdx + 1);
        var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);

        String? realMagnet = null;
        String? seasons = null;
        var kept = new System.Collections.Generic.List<String>(parts.Length);

        foreach (var part in parts)
        {
            var eq = part.IndexOf('=');
            var key = eq < 0 ? part : part.Substring(0, eq);
            var val = eq < 0 ? "" : Uri.UnescapeDataString(part.Substring(eq + 1));

            if (String.Equals(key, "x.realmagnet", StringComparison.OrdinalIgnoreCase))
            {
                realMagnet = val;
            }
            else if (String.Equals(key, "x.includeseasons", StringComparison.OrdinalIgnoreCase))
            {
                seasons = val;
            }
            else
            {
                kept.Add(part);
            }
        }

        return (prefix + String.Join("&", kept), realMagnet, seasons);
    }

    private static String SeasonsToIncludeRegex(String seasons)
    {
        // Builds a per-file IncludeRegex matching only the given season(s).
        // Matches the "S03E05" episode form (also S3E5 / S03.E05) and the
        // "Season 03" folder form, while rejecting range folders like
        // "S01-S05" and adjacent seasons (S30, S13). A bare "\bS03\b" fails on
        // the common contiguous "S03E05" naming (no word boundary before "E"),
        // so we anchor on the episode marker instead. Kept in sync with the
        // Sonarr fork's BuildSeasonIncludeRegex.
        var nums = seasons.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var values = new System.Collections.Generic.List<String>(nums.Length);
        foreach (var n in nums)
        {
            // Season 0 is valid (specials, "S00E01"). Cap at < 100.
            if (Int32.TryParse(n, out var v) && v >= 0 && v < 100)
            {
                values.Add(v.ToString());
            }
        }

        if (values.Count == 0)
        {
            // No tokens at all -> no filter (""). Tokens that were all invalid /
            // out-of-range -> a never-match pattern, so we DON'T silently fall
            // back to "no filter" and download the entire pack.
            return nums.Length == 0 ? "" : "(?!)";
        }

        var group = values.Count == 1 ? values[0] : $"(?:{String.Join("|", values)})";

        return $"(?i)(?<![A-Za-z0-9])(?:S0*{group}(?=[ ._-]?E\\d)|season[ ._-]*0*{group}(?![0-9]))";
    }
}
