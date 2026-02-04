using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyTrailers.Configuration;

/// <summary>
/// Plugin configuration for trailer downloads.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Path to the yt-dlp executable. Leave empty to use the plugin-managed copy (downloaded automatically to the plugin data folder).
    /// Set a path (e.g. "/usr/local/bin/yt-dlp" or "yt-dlp") to use an existing installation.
    /// </summary>
    public string YtDlpPath { get; set; } = string.Empty;

    /// <summary>
    /// Trailer file path relative to each media folder (e.g. "trailer.mp4" or "Trailer/trailer.mp4").
    /// </summary>
    public string TrailerPath { get; set; } = "trailer.mp4";

    /// <summary>
    /// Maximum quality: "best", "1080p", "720p", "480p".
    /// </summary>
    public string Quality { get; set; } = "720p";

    /// <summary>
    /// Seconds to wait between each download (reduces YouTube throttling).
    /// </summary>
    public int DelaySeconds { get; set; } = 3;

    /// <summary>
    /// Seconds to wait before retrying a failed download.
    /// </summary>
    public int RetryDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Maximum number of trailers to download per run (0 = no limit).
    /// </summary>
    public int MaxTrailersPerRun { get; set; } = 50;

    /// <summary>
    /// Optional extra yt-dlp options as JSON (e.g. empty object or format override).
    /// </summary>
    public string YtDlpOptionsJson { get; set; } = "{}";

    /// <summary>
    /// Returns the trailer filename to use (non-null, trimmed); defaults to "trailer.mp4" if not set.
    /// Rejects paths containing ".." or absolute paths to prevent writing outside library folders.
    /// </summary>
    /// <returns>Safe relative trailer path, e.g. "trailer.mp4".</returns>
    public string GetEffectiveTrailerPath()
    {
        var path = string.IsNullOrWhiteSpace(TrailerPath) ? "trailer.mp4" : TrailerPath.Trim();
        if (string.IsNullOrEmpty(path)) return "trailer.mp4";
        if (path.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(path))
            return "trailer.mp4";
        return path;
    }
}
