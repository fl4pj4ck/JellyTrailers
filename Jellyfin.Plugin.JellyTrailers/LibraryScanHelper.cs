using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrailers;

/// <summary>
/// Shared helpers for triggering Jellyfin library scans after trailer operations.
/// </summary>
public static class LibraryScanHelper
{
    /// <summary>
    /// Queues a library scan and logs the result.
    /// </summary>
    public static void QueueLibraryScan(ILibraryManager libraryManager, ILogger logger)
    {
        try
        {
            libraryManager.QueueLibraryScan();
            logger.LogInformation("Library scan queued.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Queue library scan failed.");
        }
    }
}
