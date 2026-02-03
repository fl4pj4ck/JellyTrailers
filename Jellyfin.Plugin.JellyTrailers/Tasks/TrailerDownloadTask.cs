using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTrailers;
using Jellyfin.Plugin.JellyTrailers.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrailers.Tasks;

/// <summary>
/// Scheduled task: scan libraries, download missing trailers via yt-dlp, trigger library refresh. No persistent list; order by folder mtime (newest first).
/// </summary>
public class TrailerDownloadTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<TrailerDownloadTask> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IApplicationPaths _applicationPaths;

    public TrailerDownloadTask(
        ILibraryManager libraryManager,
        ILogger<TrailerDownloadTask> logger,
        ILoggerFactory loggerFactory,
        IApplicationPaths applicationPaths)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _applicationPaths = applicationPaths;
    }

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
        if (Plugin.Instance == null)
        {
            _logger.LogWarning("Plugin instance not available.");
            return;
        }

        var config = Plugin.Instance.Configuration;
        var scanner = new LibraryScanner(_libraryManager, _loggerFactory.CreateLogger<LibraryScanner>());
        var statsStore = new TrailerStatsStore(_applicationPaths, _loggerFactory.CreateLogger<TrailerStatsStore>());
        var ytDlp = new YtDlpRunner(config, _applicationPaths, _loggerFactory.CreateLogger<YtDlpRunner>());

        // 1. Get library roots and scan filesystem (no persistent list; order by folder mtime so newer items first)
        var roots = scanner.GetLibraryRoots();
        if (roots.Count == 0)
        {
            _logger.LogInformation("No movie or TV libraries found; nothing to do.");
            return;
        }

        var addedAt = DateTime.UtcNow.ToString("o");
        var currentEntries = scanner.ScanAndEnrich(roots, addedAt);
        _logger.LogInformation("Scanned {Count} library folders.", currentEntries.Count);

        // 2. Entries that need a trailer (file doesn't exist), newest folders first (by directory mtime)
        var trailerPath = config.GetEffectiveTrailerPath();
        var needs = currentEntries
            .Where(e => !EntryHasTrailer(e.Path, trailerPath))
            .OrderByDescending(e => GetFolderSortTime(e.Path))
            .ToList();

        var foldersWithTrailer = currentEntries.Count - needs.Count;
        statsStore.RecordFolderCounts(currentEntries.Count, foldersWithTrailer);

        var skipped = foldersWithTrailer;
        var maxPerRun = config.MaxTrailersPerRun;
        if (maxPerRun > 0 && needs.Count > maxPerRun)
            needs = needs.Take(maxPerRun).ToList();

        if (needs.Count == 0)
        {
            _logger.LogInformation("Trailer task: nothing to do (all have trailer or list empty). Skipped: {Skipped}.", skipped);
            statsStore.RecordRun(0, 0);
            LibraryScanHelper.QueueLibraryScan(_libraryManager, _logger);
            return;
        }

        var delayMs = Math.Max(0, config.DelaySeconds) * 1000;
        var retryDelayMs = Math.Max(0, config.RetryDelaySeconds) * 1000;
        var processed = 0;
        var failed = 0;

        _logger.LogInformation(
            "Trailer task: {Count} to process (skipped {Skipped} already have trailer), delay {Delay}s between items.",
            needs.Count, skipped, config.DelaySeconds);

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
                if (!ok)
                {
                    await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Retrying: {Query}", query);
                    ok = await ytDlp.DownloadOneAsync(entry, outputPath, cancellationToken).ConfigureAwait(false);
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

            _logger.LogInformation("Trailer task done: processed {Processed}, skipped {Skipped}, failed {Failed}.", processed, skipped, failed);
        }
        finally
        {
            // Always record stats (and trigger refresh) even when task is cancelled mid-run
            statsStore.RecordRun(processed, failed);
            LibraryScanHelper.QueueLibraryScan(_libraryManager, _logger);
        }
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
