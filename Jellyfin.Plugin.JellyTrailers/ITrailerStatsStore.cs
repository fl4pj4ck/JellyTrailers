namespace Jellyfin.Plugin.JellyTrailers;

/// <summary>
/// Abstraction for trailer run stats persistence (enables testing with mocks).
/// </summary>
public interface ITrailerStatsStore
{
    /// <summary>
    /// Record folder counts from the last library scan.
    /// </summary>
    void RecordFolderCounts(int totalFolders, int foldersWithTrailer);

    /// <summary>
    /// Record a run: add to totals and append to run history.
    /// </summary>
    void RecordRun(int downloaded, int failed);

    /// <summary>
    /// Record or update progress for today's run.
    /// </summary>
    void RecordProgress(int downloaded, int failed);

    /// <summary>
    /// Reset all stats (totals and run history).
    /// </summary>
    void Reset();

    /// <summary>
    /// Get aggregated stats for display.
    /// </summary>
    TrailerStats GetStats();
}
