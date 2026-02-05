using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTrailers;
using Jellyfin.Plugin.JellyTrailers.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrailers.Tasks;

/// <summary>
/// Scheduled task: scan libraries, download missing trailers via yt-dlp, trigger library refresh. No persistent list; order by folder mtime (newest first).
/// When running on Jellyfin, config and services are injected via DI; otherwise resolved from Plugin.Instance and new (Emby compatibility).
/// </summary>
public class TrailerDownloadTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<TrailerDownloadTask> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IApplicationPaths _applicationPaths;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PluginConfiguration? _config;
    private readonly IYtDlpRunner? _ytDlpRunner;
    private readonly ITrailerStatsStore? _statsStore;
    private readonly ILibraryScanner? _scanner;

    public TrailerDownloadTask(
        ILibraryManager libraryManager,
        ILogger<TrailerDownloadTask> logger,
        ILoggerFactory loggerFactory,
        IApplicationPaths applicationPaths,
        IHttpClientFactory httpClientFactory,
        PluginConfiguration? config = null,
        IYtDlpRunner? ytDlpRunner = null,
        ITrailerStatsStore? statsStore = null,
        ILibraryScanner? scanner = null)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _applicationPaths = applicationPaths;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _ytDlpRunner = ytDlpRunner;
        _statsStore = statsStore;
        _scanner = scanner;
    }

    private PluginConfiguration Config => _config ?? Plugin.Instance!.Configuration;

    /// <inheritdoc />
    public string Name => "Download Trailers (JellyTrailers)";

    /// <inheritdoc />
    public string Key => "JellyTrailersDownload";

    /// <inheritdoc />
    public string Description => "Download missing movie and TV trailers with yt-dlp and place them next to your media.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var scanner = _scanner ?? new LibraryScanner(_libraryManager, _loggerFactory.CreateLogger<LibraryScanner>());
        var statsStore = _statsStore ?? new TrailerStatsStore(_applicationPaths, _loggerFactory.CreateLogger<TrailerStatsStore>());
        var ytDlp = _ytDlpRunner ?? new YtDlpRunner(Config, _applicationPaths, _loggerFactory.CreateLogger<YtDlpRunner>(), _httpClientFactory);

        // 1. Get library roots and scan filesystem (no persistent list; order by folder mtime so newer items first)
        var includeNames = Config.GetIncludeLibraryNamesSet();
        var excludeNames = Config.GetExcludeLibraryNamesSet();
        var roots = scanner.GetLibraryRoots(
            includeNames.Count > 0 ? includeNames : null,
            excludeNames.Count > 0 ? excludeNames : null);
        if (roots.Count == 0)
        {
            _logger.LogInformation("No movie or TV libraries found; nothing to do.");
            return;
        }

        var addedAt = DateTime.UtcNow.ToString("o");
        var currentEntries = scanner.ScanAndEnrich(roots, addedAt);
        _logger.LogInformation("Scanned {Count} library folders.", currentEntries.Count);

        // 2. Entries that need a trailer (file doesn't exist), newest folders first (by directory mtime)
        var trailerPath = Config.GetEffectiveTrailerPath();
        var needs = currentEntries
            .Where(e => !EntryHasTrailer(e.Path, trailerPath))
            .OrderByDescending(e => GetFolderSortTime(e.Path))
            .ToList();

        var foldersWithTrailer = currentEntries.Count - needs.Count;
        statsStore.RecordFolderCounts(currentEntries.Count, foldersWithTrailer);

        var skipped = foldersWithTrailer;
        var maxPerRun = Config.MaxTrailersPerRun;
        if (maxPerRun > 0 && needs.Count > maxPerRun)
            needs = needs.Take(maxPerRun).ToList();

        if (needs.Count == 0)
        {
            _logger.LogInformation("Trailer task: nothing to do (all have trailer or list empty). Skipped: {Skipped}.", skipped);
            _logger.LogInformation("JellyTrailers run finished: 0 downloaded, 0 failed, {Skipped} skipped.", skipped);
            statsStore.RecordRun(0, 0);
            LibraryScanHelper.QueueLibraryScan(_libraryManager, _logger);
            return;
        }

        var delayMs = Math.Max(0, Config.DelaySeconds) * 1000;
        var retryDelayMs = Math.Max(0, Config.RetryDelaySeconds) * 1000;
        var processed = 0;
        var failed = 0;

        _logger.LogInformation(
            "Trailer task: {Count} to process (skipped {Skipped} already have trailer), delay {Delay}s between items.",
            needs.Count, skipped, Config.DelaySeconds);

        try
        {
            for (var i = 0; i < needs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = needs[i];
                var outputPath = Path.Combine(entry.Path, trailerPath);
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var query = TitleParser.BuildSearchQuery(entry.Type, entry.Path, entry.Title, entry.Year, entry.Season);
                _logger.LogInformation("Trailer [{Index}/{Total}] {Query} -> {Output}", i + 1, needs.Count, query, outputPath);

                var ok = await ytDlp.DownloadOneAsync(entry, outputPath, cancellationToken).ConfigureAwait(false);

                // When YouTube download fails, try TMDB/OMDb fallback first (before retrying YouTube)
                if (!ok && Config.UseTmdbOmdbFallback)
                {
                    var fallbackUrl = GetFirstRemoteTrailerUrl(entry.Path);
                    if (!string.IsNullOrWhiteSpace(fallbackUrl))
                    {
                        _logger.LogInformation("Trying TMDB/OMDb fallback for: {Query}", query);
                        ok = await ytDlp.DownloadFromUrlAsync(fallbackUrl, outputPath, cancellationToken).ConfigureAwait(false);
                        if (ok)
                            _logger.LogInformation("Trailer downloaded via TMDB/OMDb fallback: {Path}", entry.Path);
                    }
                }

                if (!ok)
                {
                    await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Retrying: {Query}", query);
                    ok = await ytDlp.DownloadOneAsync(entry, outputPath, cancellationToken).ConfigureAwait(false);
                }

                // If YouTube retry still failed, try fallback once more
                if (!ok && Config.UseTmdbOmdbFallback)
                {
                    var fallbackUrl = GetFirstRemoteTrailerUrl(entry.Path);
                    if (!string.IsNullOrWhiteSpace(fallbackUrl))
                    {
                        _logger.LogInformation("Trying TMDB/OMDb fallback for: {Query}", query);
                        ok = await ytDlp.DownloadFromUrlAsync(fallbackUrl, outputPath, cancellationToken).ConfigureAwait(false);
                        if (ok)
                            _logger.LogInformation("Trailer downloaded via TMDB/OMDb fallback: {Path}", entry.Path);
                    }
                }

                if (ok)
                    processed++;
                else
                {
                    failed++;
                    _logger.LogWarning("Trailer failed: {Query} (path: {Path})", query, entry.Path);
                }

                statsStore.RecordProgress(processed, failed);

                progress?.Report((double)(i + 1) / needs.Count * 100.0);
                if (delayMs > 0 && i < needs.Count - 1)
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }

        }
        finally
        {
            // Always record stats and log run summary (including when task is cancelled mid-run)
            _logger.LogInformation("JellyTrailers run finished: {Processed} downloaded, {Failed} failed, {Skipped} skipped.", processed, failed, skipped);
            statsStore.RecordRun(processed, failed);
            LibraryScanHelper.QueueLibraryScan(_libraryManager, _logger);
        }
    }

    /// <summary>
    /// Resolves the library item by path and returns the first remote trailer URL from metadata (TMDB/OMDb), or null.
    /// For movies, uses the item's RemoteTrailers. For TV season folders, uses the parent Series' RemoteTrailers.
    /// </summary>
    private string? GetFirstRemoteTrailerUrl(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return null;
        try
        {
            var path = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var item = _libraryManager.FindByPath(path, true);
            if (item == null)
            {
                var parentPath = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parentPath))
                    item = _libraryManager.FindByPath(parentPath, true);
            }

            var trailers = GetTrailersFromItem(item);
            if (trailers == null || trailers.Count == 0)
                return null;
            var first = trailers[0];
            var url = first?.Url?.Trim();
            return string.IsNullOrEmpty(url) ? null : url;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not get remote trailer URL for path: {Path}", folderPath);
            return null;
        }
    }

    /// <summary>
    /// Gets RemoteTrailers from the item. For Season, returns the parent Series' trailers (metadata is on Series).
    /// </summary>
    private static IReadOnlyList<MediaUrl>? GetTrailersFromItem(BaseItem? item)
    {
        if (item == null)
            return null;
        if (item is Season season)
        {
            var series = season.Series ?? season.FindParent<Series>();
            return series?.RemoteTrailers;
        }
        return item.RemoteTrailers;
    }

    private static bool EntryHasTrailer(string mediaPath, string trailerPath)
    {
        var full = Path.Combine(mediaPath, trailerPath);
        return File.Exists(full);
    }

    /// <summary>Folder sort time (newest first). Uses directory LastWriteTimeUtc; returns MinValue if unavailable.</summary>
    private static DateTime GetFolderSortTime(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
#if NET9_0_OR_GREATER
        // Jellyfin 10.11+ API: Type is TaskTriggerInfoType enum
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(2).Ticks // 02:00
        };
#else
        // Jellyfin 10.10 API: TaskTriggerInfo.TriggerDaily constant
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerDaily,
            TimeOfDayTicks = TimeSpan.FromHours(2).Ticks // 02:00
        };
#endif
    }
}
