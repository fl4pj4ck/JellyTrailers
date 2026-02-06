using System.Collections.Generic;
using System.Text.Json;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyTrailers.Configuration;

/// <summary>
/// Plugin configuration for trailer downloads.
/// Invalid stored values are never exposed: path and JSON getters return safe defaults so first run never needs "fixing".
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    private string _trailerPath = "trailer.mp4";
    private string _ytDlpOptionsJson = "{}";
    private bool _trailerPathCorrected;

    /// <summary>
    /// Comma-separated library names to include (empty = all movie/TV libraries).
    /// </summary>
    public string IncludeLibraryNames { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated library names to exclude.
    /// </summary>
    public string ExcludeLibraryNames { get; set; } = string.Empty;

    /// <summary>
    /// Path to the yt-dlp executable. Leave empty to use the plugin-managed copy (downloaded automatically to the plugin data folder).
    /// Set a path (e.g. "/usr/local/bin/yt-dlp" or "yt-dlp") to use an existing installation.
    /// </summary>
    public string YtDlpPath { get; set; } = string.Empty;

    /// <summary>
    /// Trailer file path relative to each media folder (e.g. "trailer.mp4" or "Trailer/trailer.mp4").
    /// Getter normalizes invalid/empty stored value to "trailer.mp4" and clears TrailerPathCorrected so first run and bad disk never show a notice.
    /// </summary>
    public string TrailerPath
    {
        get
        {
            if (IsPathInvalid(_trailerPath) || string.IsNullOrWhiteSpace(_trailerPath))
            {
                _trailerPath = "trailer.mp4";
                _trailerPathCorrected = false;
            }
            return GetEffectiveTrailerPathInternal(_trailerPath);
        }
        set
        {
            var v = value ?? string.Empty;
            _trailerPath = v;
            if (IsPathInvalid(v))
                _trailerPathCorrected = false;
        }
    }

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
    /// Optional extra yt-dlp options as JSON. Only allowlisted option names are applied (e.g. user-agent, proxy, format);
    /// execution-related options (exec, postprocessor-args, etc.) are ignored for security.
    /// Getter normalizes invalid/empty stored value to "{}" so first run and bad disk never show an invalid-JSON notice.
    /// </summary>
    public string YtDlpOptionsJson
    {
        get
        {
            if (!TryParseJson(_ytDlpOptionsJson))
            {
                _ytDlpOptionsJson = "{}";
                return "{}";
            }
            return _ytDlpOptionsJson;
        }
        set => _ytDlpOptionsJson = TryParseJson(value) ? (value ?? "{}").Trim() : "{}";
    }

    /// <summary>
    /// True when the stored YtDlpOptionsJson was non-empty and invalid. Always false after first read because getter normalizes.
    /// </summary>
    public bool YtDlpOptionsJsonWasInvalid => false;

    /// <summary>
    /// When true, if download from YouTube (yt-dlp search) fails, try to download using the first trailer URL
    /// from Jellyfin metadata (TMDB/OMDb). Enabled by default.
    /// </summary>
    public bool UseTmdbOmdbFallback { get; set; } = true;

    /// <summary>
    /// Set to true when TrailerPath was corrected (e.g. contained ".." or was absolute). Getter returns false when path is valid or empty (TrailerPath getter normalizes first).
    /// </summary>
    public bool TrailerPathCorrected
    {
        get
        {
            if (!IsPathInvalid(_trailerPath) || string.IsNullOrWhiteSpace(_trailerPath))
            {
                _trailerPathCorrected = false;
                return false;
            }
            return _trailerPathCorrected;
        }
        set => _trailerPathCorrected = value;
    }

    private static bool IsPathInvalid(string? path)
    {
        var p = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        if (string.IsNullOrEmpty(p)) return true;
        return p.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(p);
    }

    private static string GetEffectiveTrailerPathInternal(string path)
    {
        var p = string.IsNullOrWhiteSpace(path) ? "trailer.mp4" : path.Trim();
        if (string.IsNullOrEmpty(p)) return "trailer.mp4";
        if (p.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(p))
            return "trailer.mp4";
        return p;
    }

    private static bool TryParseJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            JsonSerializer.Deserialize<JsonElement>(value.Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the trailer filename to use (non-null, trimmed); defaults to "trailer.mp4" if not set.
    /// Rejects paths containing ".." or absolute paths to prevent writing outside library folders.
    /// </summary>
    /// <returns>Safe relative trailer path, e.g. "trailer.mp4".</returns>
    public string GetEffectiveTrailerPath() => GetEffectiveTrailerPathInternal(_trailerPath);

    /// <summary>
    /// Parses <see cref="IncludeLibraryNames"/> into a set of trimmed, non-empty names (case-insensitive).
    /// Empty set means "no filter" (include all).
    /// </summary>
    public HashSet<string> GetIncludeLibraryNamesSet()
    {
        return ParseLibraryNamesCsv(IncludeLibraryNames);
    }

    /// <summary>
    /// Parses <see cref="ExcludeLibraryNames"/> into a set of trimmed, non-empty names (case-insensitive).
    /// </summary>
    public HashSet<string> GetExcludeLibraryNamesSet()
    {
        return ParseLibraryNamesCsv(ExcludeLibraryNames);
    }

    private static HashSet<string> ParseLibraryNamesCsv(string csv)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(csv)) return set;
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length > 0) set.Add(part);
        }
        return set;
    }
}
