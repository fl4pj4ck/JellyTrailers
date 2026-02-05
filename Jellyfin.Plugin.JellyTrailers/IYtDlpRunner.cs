namespace Jellyfin.Plugin.JellyTrailers;

/// <summary>
/// Abstraction for running yt-dlp (enables testing with mocks).
/// </summary>
public interface IYtDlpRunner
{
    /// <summary>
    /// Check if the configured yt-dlp executable runs.
    /// </summary>
    Task<(bool Available, string Message)> CheckAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Run yt-dlp for one trailer (search by query). Returns true if the output file exists after run.
    /// </summary>
    Task<bool> DownloadOneAsync(DownloadListEntry entry, string outputPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Download a trailer from a direct URL. Returns true if the output file exists after run.
    /// </summary>
    Task<bool> DownloadFromUrlAsync(string url, string outputPath, CancellationToken cancellationToken = default);
}
