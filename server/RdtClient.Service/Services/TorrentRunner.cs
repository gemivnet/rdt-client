using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Aria2NET;
using Microsoft.Extensions.Logging;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Data.Models.Internal;
using RdtClient.Service.Helpers;
using RdtClient.Service.Services.Downloaders;

namespace RdtClient.Service.Services;

public class TorrentRunner(
    ILogger<TorrentRunner> logger,
    Torrents torrents,
    Downloads downloads,
    RemoteService remoteService,
    IHttpClientFactory httpClientFactory,
    IRateLimitCoordinator coordinator)
{
    public static readonly ConcurrentDictionary<Guid, DownloadClient> ActiveDownloadClients = new();
    public static readonly ConcurrentDictionary<Guid, UnpackClient> ActiveUnpackClients = new();
    private DateTimeOffset? _lastNextAllowedAt;

    // Stall detection: a download that advances less than DownloadStallMinProgressBytes
    // within DownloadStallTimeout is treated as stuck - a provider rate-limit (429) or
    // a wedged/low-seed connection can leave it trickling at a few bytes/sec, never
    // finishing while holding a download slot the rest of the queue waits on. Failing
    // it routes through the existing retry path so it retries and then fails over
    // (the *arr app grabs a cached/better-seeded release instead).
    private static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromMinutes(10);
    private const Int64 DownloadStallMinProgressBytes = 1024 * 1024; // 1 MiB / window
    private static readonly ConcurrentDictionary<Guid, (Int64 BytesDone, DateTimeOffset Since)> DownloadProgressTracker = new();

    // Truncation guard: a "completed" video file smaller than this is a stub left
    // by a provider 429 / dropped connection (the real episodes are far larger).
    // Used when the total size was never reported, so the byte-ratio check can't run.
    private const Int64 TruncatedVideoFloorBytes = 1024 * 1024; // 1 MiB
    // Any completed download below this is a provider error-body stub, not a real
    // file: the debrid provider serves a tiny JSON/HTML error body (e.g. a ~150 B
    // to ~30 KB "DATABASE_ERROR" page under 429/load) in place of the file, and the
    // downloader reports it as a SUCCESSFUL completion. Real selected files are far
    // larger (the provider min-file-size filter alone is megabytes), so this floor
    // can't catch a legitimate file. Catches the non-video case where BytesDone ==
    // BytesTotal (the stub's own size), which slips past both the byte-ratio guard
    // and the video-only floor.
    private const Int64 TruncatedStubFloorBytes = 64 * 1024; // 64 KiB
    private static readonly String[] VideoExtensions = [".mkv", ".mp4", ".avi", ".ts", ".m4v", ".wmv", ".mpg", ".mpeg", ".m2ts", ".flv", ".webm"];

    // Provider-side ghost guard: a torrent the provider parks in a non-terminal
    // "downloading" state (TorBox "checking" / "stalled (no seeds)") that never
    // generates any download links sits forever with 0 Downloads, pinning a
    // download slot and starving the dequeue (downloadingTorrentsCount keeps it
    // counted). If it hasn't produced a single link this long after being added,
    // treat it as dead and fail it so the slot frees and the *arr app fails over.
    private static readonly TimeSpan ProviderNoLinksStallTimeout = TimeSpan.FromMinutes(30);

    public static Boolean IsPausedForLowDiskSpace { get; set; }

    public static (Int64 Speed, Int64 BytesTotal, Int64 BytesDone) GetStats(Guid downloadId)
    {
        if (ActiveDownloadClients.TryGetValue(downloadId, out var downloadClient))
        {
            return (downloadClient.Speed, downloadClient.BytesTotal, downloadClient.BytesDone);
        }

        if (ActiveUnpackClients.TryGetValue(downloadId, out var unpackClient))
        {
            return (0, 100, unpackClient.Progess);
        }

        return (0, 0, 0);
    }

    public async Task Initialize()
    {
        Log("Initializing TorrentRunner");

        var settingsCopy = JsonSerializer.Deserialize<DbSettings>(JsonSerializer.Serialize(Settings.Get));

        if (settingsCopy != null)
        {
            settingsCopy.Provider.ApiKey = "*****";
            settingsCopy.DownloadClient.Aria2cSecret = "*****";
            settingsCopy.DownloadClient.DownloadStationPassword = "*****";

            Log(JsonSerializer.Serialize(settingsCopy));
        }

        // When starting up reset any pending downloads or unpackings so that they are restarted.
        var allTorrents = await torrents.Get();

        allTorrents = allTorrents.Where(m => m.Completed == null).ToList();

        Log($"Found {allTorrents.Count} not completed torrents");

        foreach (var torrent in allTorrents)
        {
            foreach (var download in torrent.Downloads)
            {
                if (download.DownloadQueued != null && download.DownloadStarted != null && download.DownloadFinished == null && download.Error == null)
                {
                    Log("Resetting download status", download, torrent);

                    await downloads.UpdateDownloadStarted(download.DownloadId, null);
                }

                if (download.UnpackingQueued != null && download.UnpackingStarted != null && download.UnpackingFinished == null && download.Error == null)
                {
                    Log("Resetting unpack status", download, torrent);

                    await downloads.UpdateUnpackingStarted(download.DownloadId, null);
                }
            }
        }

        Log("TorrentRunner Initialized");
    }

    public async Task Tick()
    {
        if (String.IsNullOrWhiteSpace(Settings.Get.Provider.ApiKey))
        {
            Log($"No RealDebridApiKey set in settings");

            return;
        }

        var settingDownloadLimit = Settings.Get.General.DownloadLimit;

        if (settingDownloadLimit < 1)
        {
            settingDownloadLimit = 1;
        }

        var settingUnpackLimit = Settings.Get.General.UnpackLimit;

        if (settingUnpackLimit < 0)
        {
            settingUnpackLimit = 0;
        }

        var settingDownloadPath = Settings.Get.DownloadClient.DownloadPath;

        if (String.IsNullOrWhiteSpace(settingDownloadPath))
        {
            logger.LogError("No DownloadPath set in settings");

            return;
        }

        var sw = new Stopwatch();
        sw.Start();

        var currentNextAllowedAt = coordinator.GetMaxNextAllowedAt();

        if (currentNextAllowedAt != _lastNextAllowedAt)
        {
            if (currentNextAllowedAt == null || currentNextAllowedAt <= DateTimeOffset.UtcNow)
            {
                if (_lastNextAllowedAt > DateTimeOffset.UtcNow)
                {
                    Log("Rate-limit cooldown expired, resuming dequeuing");

                    await remoteService.UpdateRateLimitStatus(new()
                    {
                        NextDequeueTime = null,
                        SecondsRemaining = 0
                    });
                }
            }

            _lastNextAllowedAt = currentNextAllowedAt;
        }

        if (!ActiveDownloadClients.IsEmpty || !ActiveUnpackClients.IsEmpty)
        {
            Log($"TorrentRunner Tick Start, {ActiveDownloadClients.Count} active downloads, {ActiveUnpackClients.Count} active unpacks");
        }

        if (ActiveDownloadClients.Any(m => m.Value.Type == Data.Enums.DownloadClient.Aria2c))
        {
            Log("Updating Aria2 status");

            var httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            var aria2NetClient = new Aria2NetClient(Settings.Get.DownloadClient.Aria2cUrl, Settings.Get.DownloadClient.Aria2cSecret, httpClient, 1);

            var allDownloads = await aria2NetClient.TellAllAsync();

            Log($"Found {allDownloads.Count} Aria2 downloads");

            foreach (var activeDownload in ActiveDownloadClients)
            {
                if (activeDownload.Value.Downloader is Aria2cDownloader aria2Downloader)
                {
                    await aria2Downloader.Update(allDownloads);
                }
            }

            Log("Finished updating Aria2 status");
        }

        if (ActiveDownloadClients.Any(m => m.Value.Type == Data.Enums.DownloadClient.DownloadStation))
        {
            Log("Updating DownloadStation status");

            foreach (var activeDownload in ActiveDownloadClients)
            {
                if (activeDownload.Value.Downloader is DownloadStationDownloader downloadStationDownloader)
                {
                    await downloadStationDownloader.Update();
                }
            }
        }

        // Stall detection: fail downloads that make effectively no progress for too
        // long. Speed/BytesDone are fresh here (Aria2/DownloadStation were just
        // refreshed above; Bezzad updates them live). A download advancing less than
        // DownloadStallMinProgressBytes over DownloadStallTimeout is wedged - failing
        // it via MarkFailed sets an Error so the retry path below picks it up.
        foreach (var (downloadId, downloadClient) in ActiveDownloadClients)
        {
            if (downloadClient.Finished)
            {
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var bytesDone = downloadClient.BytesDone;

            if (!DownloadProgressTracker.TryGetValue(downloadId, out var last) ||
                bytesDone - last.BytesDone >= DownloadStallMinProgressBytes)
            {
                // First time seen, or meaningful progress since last window - reset.
                DownloadProgressTracker[downloadId] = (bytesDone, now);
            }
            else if (now - last.Since > DownloadStallTimeout)
            {
                var stalledDownload = await downloads.GetById(downloadId);

                LogError($"Download stalled: advanced only {bytesDone - last.BytesDone} bytes in {DownloadStallTimeout.TotalMinutes:n0}m ({bytesDone}/{downloadClient.BytesTotal} bytes). Failing it so it can retry / fail over.",
                         stalledDownload,
                         stalledDownload?.Torrent);

                await downloadClient.MarkFailed($"Download stalled - no meaningful progress for {DownloadStallTimeout.TotalMinutes:n0} minutes (provider rate-limiting, or the torrent is uncached / low-seed)");

                DownloadProgressTracker.TryRemove(downloadId, out _);
            }
        }

        // Drop tracker entries for downloads that are no longer active.
        foreach (var trackedId in DownloadProgressTracker.Keys.ToList())
        {
            if (!ActiveDownloadClients.ContainsKey(trackedId))
            {
                DownloadProgressTracker.TryRemove(trackedId, out _);
            }
        }

        // Check if any torrents are finished downloading to the host, remove them from the active download list.
        var completedActiveDownloads = ActiveDownloadClients.Where(m => m.Value.Finished).ToList();

        if (completedActiveDownloads.Count > 0)
        {
            Log($"Processing {completedActiveDownloads.Count} completed downloads");

            foreach (var (downloadId, downloadClient) in completedActiveDownloads)
            {
                var download = await downloads.GetById(downloadId);

                if (download == null)
                {
                    ActiveDownloadClients.TryRemove(downloadId, out _);

                    Log($"Download with ID {downloadId} not found! Removed from download queue");

                    continue;
                }

                Log("Processing download", download, download.Torrent);

                var error = downloadClient.Error;

                // Always record the byte counts so a truncation is diagnosable after
                // the fact (the torrent may be cleaned up before we can inspect it).
                Log($"Download completed: {downloadClient.BytesDone}/{downloadClient.BytesTotal} bytes, type {downloadClient.Type}, file '{download.FileName ?? download.Path}', error '{downloadClient.Error}'",
                    download,
                    download.Torrent);

                // Truncation guard: a provider rate-limit (429) or a dropped
                // connection can end the HTTP stream early while the downloader
                // still reports "complete". Importing the partial file leaves a stub
                // the *arr apps reject ("unable to determine if sample"). Catch it two
                // ways so we don't depend on the total size being reported:
                //   1. byte-ratio - got materially fewer bytes than the known total;
                //   2. video-floor - the total was never captured (stream died before
                //      Content-Length was seen) but a "complete" video file this tiny
                //      is a stub.
                // Either way set an error so it retries / fails over instead of being
                // marked finished. (Skip Symlink - it transfers no local bytes.)
                if (String.IsNullOrWhiteSpace(error) && downloadClient.Type != Data.Enums.DownloadClient.Symlink)
                {
                    var fileName = download.FileName ?? download.Path ?? String.Empty;
                    var isVideo = VideoExtensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

                    if (downloadClient.BytesTotal > 0 && downloadClient.BytesDone < (Int64)(downloadClient.BytesTotal * 0.999))
                    {
                        error = $"Truncated download: received {downloadClient.BytesDone} of {downloadClient.BytesTotal} bytes (provider rate-limit or dropped connection)";
                    }
                    else if (isVideo && downloadClient.BytesDone < TruncatedVideoFloorBytes)
                    {
                        error = $"Truncated download: video file completed at only {downloadClient.BytesDone} bytes (provider rate-limit or dropped connection; total size was not reported)";
                    }
                    else if (downloadClient.BytesDone > 0 && downloadClient.BytesDone < TruncatedStubFloorBytes)
                    {
                        // Non-video stub where BytesDone == BytesTotal (or total was
                        // never reported): the provider returned a tiny error-body in
                        // place of the file. Fail it at completion time so it routes
                        // to the bounded retry / fail-over path immediately instead of
                        // squatting a download slot for the full stall-detection window
                        // (which starves every torrent's queue).
                        error = $"Truncated download: completed at only {downloadClient.BytesDone} bytes (provider rate-limit / error-body stub)";
                    }
                }

                if (!String.IsNullOrWhiteSpace(error))
                {
                    // Retry the download if an error is encountered.
                    LogError($"Download reported an error: {error}", download, download.Torrent);

                    Log($"Download retry count {download.RetryCount}/{download.Torrent!.DownloadRetryAttempts}, torrent retry count {download.Torrent.RetryCount}/{download.Torrent.TorrentRetryAttempts}",
                        download,
                        download.Torrent);

                    if (download.RetryCount < download.Torrent.DownloadRetryAttempts)
                    {
                        Log($"Retrying download", download, download.Torrent);

                        await downloads.Reset(downloadId);
                        await downloads.UpdateRetryCount(downloadId, download.RetryCount + 1);
                    }
                    else
                    {
                        Log($"Not retrying download", download, download.Torrent);

                        await downloads.UpdateError(downloadId, error);
                        await downloads.UpdateCompleted(downloadId, DateTimeOffset.UtcNow);
                    }
                }
                else
                {
                    Log($"Download finished successfully", download, download.Torrent);

                    await downloads.UpdateDownloadFinished(downloadId, DateTimeOffset.UtcNow);
                    await downloads.UpdateUnpackingQueued(downloadId, DateTimeOffset.UtcNow);
                }

                ActiveDownloadClients.TryRemove(downloadId, out _);

                Log($"Removed from ActiveDownloadClients", download, download.Torrent);
            }
        }

        // Check if any torrents are finished unpacking, remove them from the active unpack list.
        var completedUnpacks = ActiveUnpackClients.Where(m => m.Value.Finished).ToList();

        if (completedUnpacks.Count > 0)
        {
            Log($"Processing {completedUnpacks.Count} completed unpacks");

            foreach (var (downloadId, unpackClient) in completedUnpacks)
            {
                var download = await downloads.GetById(downloadId);

                if (download == null)
                {
                    ActiveUnpackClients.TryRemove(downloadId, out _);

                    Log($"Download with ID {downloadId} not found! Removed from unpack queue");

                    continue;
                }

                if (unpackClient.Error != null)
                {
                    Log($"Unpack reported an error: {unpackClient.Error}", download, download.Torrent);

                    await downloads.UpdateError(downloadId, unpackClient.Error);
                }
                else
                {
                    Log($"Unpack finished successfully", download, download.Torrent);

                    await downloads.UpdateUnpackingFinished(downloadId, DateTimeOffset.UtcNow);
                }

                await downloads.UpdateCompleted(downloadId, DateTimeOffset.UtcNow);

                ActiveUnpackClients.TryRemove(downloadId, out _);

                Log($"Removed from ActiveUnpackClients", download, download.Torrent);
            }
        }

        var allTorrents = await torrents.Get();
        var downloadsById = allTorrents.SelectMany(m => m.Downloads).ToDictionary(m => m.DownloadId, m => m);

        // Check for deleted torrents that are stuck in the ActiveDownloads or ActiveUnpacks
        foreach (var activeDownload in ActiveDownloadClients)
        {
            if (!downloadsById.ContainsKey(activeDownload.Key))
            {
                await activeDownload.Value.Cancel();
                ActiveDownloadClients.TryRemove(activeDownload.Key, out _);

                break;
            }
        }

        foreach (var activeUnpacks in ActiveUnpackClients)
        {
            if (!downloadsById.ContainsKey(activeUnpacks.Key))
            {
                activeUnpacks.Value.Cancel();
                ActiveUnpackClients.TryRemove(activeUnpacks.Key, out _);

                break;
            }
        }

        // Process torrent retries
        foreach (var torrent in allTorrents.Where(m => m.Retry != null))
        {
            try
            {
                Log($"Retrying torrent {torrent.RetryCount}/{torrent.TorrentRetryAttempts}", torrent);

                if (torrent.RetryCount > torrent.TorrentRetryAttempts)
                {
                    await torrents.UpdateRetry(torrent.TorrentId, null, torrent.RetryCount);
                    Log($"Torrent reach max retry count");

                    continue;
                }

                await torrents.RetryTorrent(torrent.TorrentId, torrent.RetryCount);
            }
            catch (Exception ex)
            {
                await torrents.UpdateRetry(torrent.TorrentId, null, torrent.RetryCount);
                await torrents.UpdateError(torrent.TorrentId, ex.Message);
            }
        }

        // Process torrent errors
        foreach (var torrent in allTorrents.Where(m => m.Error != null && m.DeleteOnError > 0))
        {
            if (torrent.Completed == null)
            {
                continue;
            }

            if (torrent.Completed.Value.AddMinutes(torrent.DeleteOnError) > DateTime.UtcNow)
            {
                continue;
            }

            Log($"Removing torrent because it has been {torrent.DeleteOnError} minutes in the error state", torrent);

            await torrents.Delete(torrent.TorrentId, true, true, true);
        }

        // Process torrent lifetime
        foreach (var torrent in allTorrents.Where(m => m.Downloads.Count == 0 && m.Completed == null && m.Lifetime > 0))
        {
            if (torrent.Added.AddMinutes(torrent.Lifetime) > DateTime.UtcNow)
            {
                continue;
            }

            Log($"Torrent has reached its {torrent.Lifetime} minutes lifetime, marking as error", torrent);

            await torrents.UpdateRetry(torrent.TorrentId, null, torrent.TorrentRetryAttempts);
            await torrents.UpdateComplete(torrent.TorrentId, $"Torrent lifetime of {torrent.Lifetime} minutes reached", DateTimeOffset.UtcNow, false);
        }

        // Process torrents in DebridQueue
        var torrentsToAddToProvider = allTorrents.Where(m => m.Completed == null && m.Error == null && m.RdId == null && m.RdAdded == null && m.FileOrMagnet != null && m.RdStatus == TorrentStatus.Queued)
                                                 .ToList();

        if (torrentsToAddToProvider.Count != 0)
        {
            var nextAllowedAt = coordinator.GetMaxNextAllowedAt();

            if (nextAllowedAt > DateTimeOffset.UtcNow)
            {
                logger.LogDebug($"Dequeuing torrents is paused until {nextAllowedAt}, {nextAllowedAt - DateTimeOffset.Now} remaining");
            }
            else
            {
                // Count only torrents that are genuinely occupying a download slot:
                // non-terminal RdStatus AND not yet Completed. A torrent marked
                // Completed (e.g. failed/stalled-out, pending cleanup) keeps its
                // RdStatus until it's deleted, so without the Completed guard it
                // would keep being counted and could pin the slot budget.
                var downloadingTorrentsCount = allTorrents.Count(m => m.Completed == null &&
                                                                      m.RdStatus is not (TorrentStatus.Queued or TorrentStatus.Finished or TorrentStatus.Error));

                var maxParallelDownloads = Settings.Get.Provider.MaxParallelDownloads;

                logger.LogDebug("Currently downloading {downloadingTorrentCount}/{maxParallelDownloads} torrents, {queuedCount} queued.",
                                downloadingTorrentsCount,
                                maxParallelDownloads,
                                torrentsToAddToProvider.Count);

                // Clamp to non-negative: if more torrents are "downloading" than the
                // budget (e.g. ghosts pinning slots), an unclamped subtraction goes
                // negative and Take(-1) silently dequeues nothing, freezing the queue.
                var dequeueCount = maxParallelDownloads == 0 ? torrentsToAddToProvider.Count : Math.Max(0, maxParallelDownloads - downloadingTorrentsCount);

                foreach (var torrent in torrentsToAddToProvider.Take(dequeueCount))
                {
                    try
                    {
                        await torrents.DequeueFromDebridQueue(torrent);
                    }
                    catch (RateLimitException ex)
                    {
                        await SetRateLimit(ex.RetryAfter, ex.Message);

                        break;
                    }
                    catch (Exception ex)
                    {
                        await torrents.UpdateComplete(torrent.TorrentId, $"Could not add to provider: {ex.Message}", DateTimeOffset.Now, true);
                        logger.LogWarning(ex, "Could not dequeue torrent {torrentId}", torrent.TorrentId);
                    }
                }
            }
        }

        allTorrents = await torrents.Get();

        var completeTorrents = allTorrents.Where(m => m.Completed != null);
        var torrentsToDelete = completeTorrents.Where(m => DateTimeOffset.UtcNow >= m.Completed?.AddMinutes(m.FinishedActionDelay) && m.Error == null);

        foreach (var torrent in torrentsToDelete)
        {
            if (torrent.DownloadClient == Data.Enums.DownloadClient.Symlink)
            {
                switch (torrent.FinishedAction)
                {
                    case TorrentFinishedAction.RemoveAllTorrents:
                        Log($"Force setting FinishedAction to RemoveClient as download client is Symlink and FinishedAction is RemoveAllTorrents", torrent);
                        torrent.FinishedAction = TorrentFinishedAction.RemoveClient;

                        break;
                    case TorrentFinishedAction.RemoveRealDebrid:
                        Log($"Force setting FinishedAction to TorrentFinishedAction.None as download client is Symlink and FinishedAction is RemoveRealDebrid", torrent);
                        torrent.FinishedAction = TorrentFinishedAction.None;

                        break;
                }
            }

            switch (torrent.FinishedAction)
            {
                case TorrentFinishedAction.RemoveAllTorrents:
                    Log($"Removing torrents from debrid provider and RDT-Client, no files", torrent);
                    await torrents.Delete(torrent.TorrentId, true, true, false);

                    break;
                case TorrentFinishedAction.RemoveRealDebrid:
                    Log($"Removing torrents from debrid provider, no files", torrent);
                    await torrents.Delete(torrent.TorrentId, false, true, false);

                    break;
                case TorrentFinishedAction.RemoveClient:
                    Log($"Removing torrents from client, no files", torrent);
                    await torrents.Delete(torrent.TorrentId, true, false, false);

                    break;
                case TorrentFinishedAction.None:
                    Log($"Not removing torrents or files", torrent);

                    break;
                default:
                    Log($"Invalid torrent FinishedAction {torrent.FinishedAction}", torrent);

                    break;
            }
        }

        var incompleteTorrents = allTorrents.Where(m => m.Completed == null).ToList();

        if (incompleteTorrents.Count > 0)
        {
            Log($"Processing {allTorrents.Count} torrents");
        }

        foreach (var torrent in incompleteTorrents)
        {
            try
            {
                // Check if there are any downloads that are queued and can be started.
                var queuedDownloads = torrent.Downloads
                                             .Where(m => m.Completed == null && m.DownloadQueued != null && m.DownloadStarted == null && m.Error == null)
                                             .OrderBy(m => m.DownloadQueued)
                                             .ToList();

                Log($"Currently {queuedDownloads.Count} queued downloads and {ActiveDownloadClients.Count} total active downloads", torrent);

                foreach (var download in queuedDownloads)
                {
                    Log($"Processing to download", download, torrent);

                    if (ActiveDownloadClients.Count >= settingDownloadLimit && torrent.DownloadClient != Data.Enums.DownloadClient.Symlink)
                    {
                        Log($"Not starting download because there are already the max number of downloads active", download, torrent);

                        return;
                    }

                    if (IsPausedForLowDiskSpace && torrent.DownloadClient == Data.Enums.DownloadClient.Bezzad)
                    {
                        logger.LogInformation($"Not starting Bezzad download because of low disk space {download.ToLog()} {torrent.ToLog()}");

                        return;
                    }

                    if (ActiveDownloadClients.ContainsKey(download.DownloadId))
                    {
                        Log($"Not starting download because this download is already active", download, torrent);

                        return;
                    }

                    try
                    {
                        if (download.Link == null)
                        {
                            Log($"Unrestricting links", download, torrent);

                            var downloadLink = await torrents.UnrestrictLink(download.DownloadId);
                            download.Link = downloadLink;

                            if (download.FileName == null)
                            {
                                var fileName = await torrents.RetrieveFileName(download.DownloadId);
                                download.FileName = fileName;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Cannot unrestrict link: {ex.Message}", ex.Message);

                        await downloads.UpdateError(download.DownloadId, ex.Message);
                        await downloads.UpdateCompleted(download.DownloadId, DateTimeOffset.UtcNow);
                        download.Error = ex.Message;
                        download.Completed = DateTimeOffset.UtcNow;

                        return;
                    }

                    Log($"Marking download as started", download, torrent);

                    download.DownloadStarted = DateTime.UtcNow;
                    await downloads.UpdateDownloadStarted(download.DownloadId, download.DownloadStarted);

                    var downloadPath = settingDownloadPath;

                    if (!String.IsNullOrWhiteSpace(torrent.Category))
                    {
                        downloadPath = Path.Combine(downloadPath, torrent.Category);
                    }

                    Log($"Setting download path to {downloadPath}", download, torrent);

                    // Start the download process
                    var downloadClient = new DownloadClient(download, torrent, downloadPath, torrent.Category);

                    if (ActiveDownloadClients.TryAdd(download.DownloadId, downloadClient))
                    {
                        Log($"Starting download", download, torrent);

                        try
                        {
                            var remoteId = await downloadClient.Start();

                            if (String.IsNullOrWhiteSpace(remoteId))
                            {
                                throw new($"No remote ID received from download client");
                            }

                            Log($"Received ID {remoteId}", download, torrent);

                            if (download.RemoteId != remoteId)
                            {
                                await downloads.UpdateRemoteId(download.DownloadId, remoteId);
                            }

                            if (IsPausedForLowDiskSpace && downloadClient.Type == Data.Enums.DownloadClient.Bezzad)
                            {
                                logger.LogInformation($"Pausing new Bezzad download due to low disk space {download.ToLog()} {torrent.ToLog()}");
                                await downloadClient.Pause();
                            }
                        }
                        catch (Exception ex)
                        {
                            LogError($"Unable to start download: {ex.Message}", download, torrent);

                            continue;
                        }

                        Log($"Started download", download, torrent);
                    }
                }

                // Check if there are any unpacks that are queued and can be started.
                var queuedUnpacks = torrent.Downloads
                                           .Where(m => m.Completed == null && m.UnpackingQueued != null && m.UnpackingStarted == null && m.Error == null)
                                           .OrderBy(m => m.DownloadQueued)
                                           .ToList();

                foreach (var download in queuedUnpacks)
                {
                    Log($"Starting unpack", download, torrent);

                    if (download.Link == null)
                    {
                        Log($"No download link found", download, torrent);

                        await downloads.UpdateError(download.DownloadId, "Download Link cannot be null");
                        await downloads.UpdateCompleted(download.DownloadId, DateTimeOffset.UtcNow);

                        continue;
                    }

                    // Check if the unpacking process is even needed
                    var uri = new Uri(download.Link);

                    var extension = Path.GetExtension(download.FileName);

                    if ((extension != ".rar" && extension != ".zip") ||
                        torrent.DownloadClient == Data.Enums.DownloadClient.Symlink ||
                        settingUnpackLimit == 0)
                    {
                        Log($"No need to unpack, setting it as unpacked", download, torrent);

                        download.UnpackingStarted = DateTimeOffset.UtcNow;
                        download.UnpackingFinished = DateTimeOffset.UtcNow;
                        download.Completed = DateTimeOffset.UtcNow;

                        await downloads.UpdateUnpackingStarted(download.DownloadId, download.UnpackingStarted);
                        await downloads.UpdateUnpackingFinished(download.DownloadId, download.UnpackingFinished);
                        await downloads.UpdateCompleted(download.DownloadId, download.Completed);

                        continue;
                    }

                    // Check if we have reached the download limit, if so queue the download, but don't start it.
                    if (ActiveUnpackClients.Count >= settingUnpackLimit)
                    {
                        Log($"Not starting unpack because there are already the max number of unpacks active", download, torrent);

                        continue;
                    }

                    if (ActiveUnpackClients.ContainsKey(download.DownloadId))
                    {
                        Log($"Not starting unpack because this download is already active", download, torrent);

                        continue;
                    }

                    download.UnpackingStarted = DateTimeOffset.UtcNow;
                    await downloads.UpdateUnpackingStarted(download.DownloadId, download.UnpackingStarted);

                    var downloadPath = settingDownloadPath;

                    if (!String.IsNullOrWhiteSpace(torrent.Category))
                    {
                        downloadPath = Path.Combine(downloadPath, torrent.Category);
                    }

                    Log($"Setting unpack path to {downloadPath}", download, torrent);

                    // Start the unpacking process
                    var unpackClient = new UnpackClient(download, downloadPath);

                    if (ActiveUnpackClients.TryAdd(download.DownloadId, unpackClient))
                    {
                        Log($"Starting unpack", download, torrent);

                        unpackClient.Start();
                    }
                }

                Log("Processing", torrent);

                // If torrent is erroring out on the debrid side.
                if (torrent.RdStatus == TorrentStatus.Error)
                {
                    Log($"Torrent reported an error: {torrent.RdStatusRaw}", torrent);
                    Log($"Torrent retry count {torrent.RetryCount}/{torrent.TorrentRetryAttempts}", torrent);

                    Log($"Received provider error: {torrent.RdStatusRaw}, not processing further", torrent);

                    await torrents.UpdateComplete(torrent.TorrentId, $"Debrid error: {torrent.RdStatusRaw}.", DateTimeOffset.UtcNow, true);

                    continue;
                }

                // Debrid provider is waiting for file selection, select which files to download.
                if ((torrent.RdStatus == TorrentStatus.WaitingForFileSelection || torrent.RdStatus == TorrentStatus.Finished) &&
                    torrent.FilesSelected == null &&
                    torrent.Downloads.Count == 0)
                {
                    Log($"Selecting files", torrent);

                    await torrents.SelectFiles(torrent.TorrentId);

                    await torrents.UpdateFilesSelected(torrent.TorrentId, DateTime.UtcNow);
                }

                // Debrid provider finished downloading the torrent, process the file to host.
                if (torrent.RdStatus == TorrentStatus.Finished)
                {
                    // The files are selected but there are no downloads yet, check if debrid provider has generated links yet.
                    if (torrent.Downloads.Count == 0 && torrent.FilesSelected != null)
                    {
                        Log($"Creating downloads", torrent);

                        if (torrent.HostDownloadAction == TorrentHostDownloadAction.DownloadAll)
                        {
                            try
                            {
                                await torrents.CreateDownloads(torrent.TorrentId);
                            }
                            catch (Exception ex)
                            {
                                // e.g. a permanent link shortfall (infringing-filtered
                                // files). Fail the torrent instead of retrying the
                                // link fetch forever.
                                logger.LogError(ex, "Could not create downloads for {torrentId}", torrent.TorrentId);

                                await torrents.UpdateComplete(torrent.TorrentId, ex.Message, DateTimeOffset.UtcNow, false);
                            }
                        }
                    }
                }

                // Provider-side ghost guard: a torrent stuck in the non-terminal
                // Downloading state (TorBox "checking" / "stalled (no seeds)") that
                // has never generated a download link (Downloads.Count == 0) will
                // never finish on its own and has no handler above, so it pins a
                // download slot indefinitely and starves the dequeue.
                //
                // Only treat it as a dead magnet when it shows NO sign of life:
                // 0% progress AND no seeders AND no speed. A torrent TorBox is
                // genuinely downloading (RdProgress > 0, or seeders/speed > 0) shares
                // the same Downloading status and 0 links until it caches, so without
                // these guards we'd nuke a working pack at the 30m mark and discard
                // all its TorBox-side progress (retry re-adds from scratch). The
                // (Int64) progress cast preserves percent, so RdProgress == 0 reliably
                // means 0% downloaded. Failing routes through the bounded retry path
                // so the slot frees and Sonarr fails over to a seeded release.
                if (torrent.RdStatus == TorrentStatus.Downloading &&
                    torrent.Downloads.Count == 0 &&
                    (torrent.RdProgress ?? 0) == 0 &&
                    (torrent.RdSeeders ?? 0) == 0 &&
                    (torrent.RdSpeed ?? 0) == 0 &&
                    DateTimeOffset.UtcNow - (torrent.RdAdded ?? torrent.Added) > ProviderNoLinksStallTimeout)
                {
                    LogError($"Torrent stalled on provider (status '{torrent.RdStatusRaw}', 0% / no seeders / no links after {ProviderNoLinksStallTimeout.TotalMinutes:n0}m); failing it so the slot frees and it can fail over", null, torrent);

                    await torrents.UpdateComplete(torrent.TorrentId, $"Stalled on provider ('{torrent.RdStatusRaw}') — 0% with no seeders or download links after {ProviderNoLinksStallTimeout.TotalMinutes:n0} minutes", DateTimeOffset.UtcNow, true);

                    continue;
                }

                // Check if torrent is complete, or if we don't want to download any files to the host.
                if (torrent.Downloads.Count > 0 ||
                    (torrent.RdStatus == TorrentStatus.Finished && torrent.HostDownloadAction == TorrentHostDownloadAction.DownloadNone))
                {
                    var completeCount = torrent.Downloads.Count(m => m.Completed != null);

                    var completePerc = 0;

                    var totalDownloadBytes = torrent.Downloads.Sum(m => GetStats(m.DownloadId).BytesTotal);
                    var totalDoneBytes = torrent.Downloads.Sum(m => GetStats(m.DownloadId).BytesDone);

                    if (totalDownloadBytes > 0)
                    {
                        completePerc = (Int32)(((Double)totalDoneBytes / totalDownloadBytes) * 100);
                    }

                    if (completeCount == torrent.Downloads.Count)
                    {
                        Log($"All downloads complete, marking torrent as complete", torrent);

                        await torrents.UpdateComplete(torrent.TorrentId, null, DateTimeOffset.UtcNow, true);

                        try
                        {
                            await torrents.RunTorrentComplete(torrent.TorrentId);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex.Message, "Unable to run post process: {Message}", ex.Message);
                        }
                    }
                    else
                    {
                        Log($"Waiting for downloads to complete. {completeCount}/{torrent.Downloads.Count} complete ({completePerc}%)", torrent);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex.Message, "Torrent processing result in an unexpected exception: {Message}", ex.Message);
                await torrents.UpdateComplete(torrent.TorrentId, $"Runner error: {ex.Message}", DateTimeOffset.UtcNow, true);
            }
        }

        sw.Stop();

        if (sw.ElapsedMilliseconds > 1000)
        {
            Log($"TorrentRunner Tick End (took {sw.ElapsedMilliseconds}ms)");
        }
    }

    public async Task SetRateLimit(TimeSpan retryAfter, String message)
    {
        coordinator.UpdateCooldown("General", retryAfter);
        var nextDequeueTime = coordinator.GetMaxNextAllowedAt();
        var now = DateTimeOffset.UtcNow;
        var secondsRemaining = nextDequeueTime.HasValue ? (nextDequeueTime.Value - now).TotalSeconds : 0;

        Log($"Rate-limit reached, pausing dequeuing for {retryAfter.TotalMinutes} minutes (until {nextDequeueTime}): {message}");

        _lastNextAllowedAt = nextDequeueTime;

        await remoteService.UpdateRateLimitStatus(new()
        {
            NextDequeueTime = nextDequeueTime,
            SecondsRemaining = secondsRemaining
        });
    }

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

    private void LogError(String message, Download? download, Torrent? torrent)
    {
        if (download != null)
        {
            message = $"{message} {download.ToLog()}";
        }

        if (torrent != null)
        {
            message = $"{message} {torrent.ToLog()}";
        }

        logger.LogError(message);
    }
}
