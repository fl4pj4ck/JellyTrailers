using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTrailers.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrailers.Tasks;

/// <summary>
/// Scheduled task: delete all trailer files from movie and TV library folders (manual run only).
/// </summary>
public class RemoveAllTrailersTask : IScheduledTask
{
    private readonly PluginConfiguration _config;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<RemoveAllTrailersTask> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public RemoveAllTrailersTask(
        PluginConfiguration config,
        ILibraryManager libraryManager,
        ILogger<RemoveAllTrailersTask> logger,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _libraryManager = libraryManager;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public string Name => "Remove All Trailers (JellyTrailers)";

    /// <inheritdoc />
    public string Key => "JellyTrailersRemoveAll";

    /// <inheritdoc />
    public string Description => "Delete all trailer files from movie and TV library folders (uses current Trailer filename setting).";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var trailerPath = _config.GetEffectiveTrailerPath();
        var scanner = new LibraryScanner(_libraryManager, _loggerFactory.CreateLogger<LibraryScanner>());

        var roots = scanner.GetLibraryRoots();
        if (roots.Count == 0)
        {
            _logger.LogInformation("No movie or TV libraries found; nothing to do.");
            return Task.CompletedTask;
        }

        var addedAt = DateTime.UtcNow.ToString("o");
        var entries = scanner.ScanAndEnrich(roots, addedAt);
        var total = entries.Count;
        var deleted = 0;
        var errors = 0;

        _logger.LogInformation("Remove all trailers: scanning {Count} library entries (trailer path: {TrailerPath}).", total, trailerPath);

        for (var i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[i];
            var fullPath = Path.Combine(entry.Path, trailerPath);

            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    deleted++;
                    _logger.LogDebug("Deleted: {Path}", fullPath);
                }
            }
            catch (Exception ex)
            {
                errors++;
                _logger.LogWarning(ex, "Could not delete trailer: {Path}", fullPath);
            }

            progress?.Report(total > 0 ? (double)(i + 1) / total * 100.0 : 0);
        }

        _logger.LogInformation("Remove all trailers done: {Deleted} deleted, {Errors} errors.", deleted, errors);

        LibraryScanHelper.QueueLibraryScan(_libraryManager, _logger);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Manual only; no default schedule.
        yield break;
    }
}
