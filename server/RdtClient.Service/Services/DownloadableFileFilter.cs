using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;

namespace RdtClient.Service.Services;

public interface IDownloadableFileFilter
{
    public Boolean IsDownloadable(Torrent torrent, String filePath, Int64 fileSize);
}

public class DownloadableFileFilter(ILogger<DownloadableFileFilter> logger) : IDownloadableFileFilter
{
    // IncludeRegex/ExcludeRegex can arrive from per-torrent overrides (SeasonSplit)
    // or settings, i.e. untrusted/arbitrary patterns. Cap match time so a
    // catastrophic-backtracking pattern can't hang the download thread.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public Boolean IsDownloadable(Torrent torrent, String filePath, Int64 fileSize)
    {
        var isDownloadable = PassesSizeFilter(torrent, filePath, fileSize) &&
                             PassesFilePathFilter(torrent, filePath);

        if (isDownloadable)
        {
            logger.LogDebug("File {filePath} was included after filtering", filePath);
        }

        return isDownloadable;
    }

    private Boolean PassesSizeFilter(Torrent torrent, String filePath, Int64 fileSize)
    {
        if (torrent is { ClientKind: Provider.RealDebrid, DownloadAction: TorrentDownloadAction.DownloadManual })
        {
            return true;
        }

        if (torrent.DownloadMinSize <= 0 || fileSize > torrent.DownloadMinSize * 1024 * 1024)
        {
            return true;
        }

        logger.LogDebug("Not downloading file {filePath} file size {fileSize} smaller than minimum {downloadMinSize}", filePath, fileSize, torrent.DownloadMinSize);

        return false;
    }

    private Boolean PassesFilePathFilter(Torrent torrent, String filePath)
    {
        return PassesIncludeRegexFilter(torrent, filePath) && PassesExcludeRegexFilter(torrent, filePath);
    }

    private Boolean PassesIncludeRegexFilter(Torrent torrent, String filePath)
    {
        // On a broken/timed-out include regex, default to including the file:
        // dropping every file silently is worse than ignoring the filter.
        if (String.IsNullOrWhiteSpace(torrent.IncludeRegex) || SafeIsMatch(filePath, torrent.IncludeRegex, onError: true))
        {
            return true;
        }

        logger.LogDebug("Not downloading file {filePath} does not match regex {includeRegex}", filePath, torrent.IncludeRegex);

        return false;
    }

    private Boolean PassesExcludeRegexFilter(Torrent torrent, String filePath)
    {
        // If the IncludeRegex is set, ignore the ExcludeRegex
        if (!String.IsNullOrWhiteSpace(torrent.IncludeRegex))
        {
            return true;
        }

        // On a broken/timed-out exclude regex, default to NOT excluding.
        if (String.IsNullOrWhiteSpace(torrent.ExcludeRegex) || !SafeIsMatch(filePath, torrent.ExcludeRegex, onError: false))
        {
            return true;
        }

        logger.LogDebug("Not downloading file {filePath} matches regex {excludeRegex}", filePath, torrent.ExcludeRegex);

        return false;
    }

    private Boolean SafeIsMatch(String input, String pattern, Boolean onError)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.None, RegexTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            logger.LogWarning("Regex '{pattern}' timed out matching '{input}' — ignoring this filter", pattern, input);
            return onError;
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid regex '{pattern}' — ignoring this filter", pattern);
            return onError;
        }
    }
}
