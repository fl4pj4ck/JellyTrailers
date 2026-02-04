using System.Diagnostics;
using System.Text.Json;
using Jellyfin.Plugin.JellyTrailers.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrailers;

/// <summary>
/// Runs yt-dlp to search and download one trailer. Uses executable from config, or the plugin-managed copy (auto-downloaded if missing).
/// </summary>
public class YtDlpRunner
{
    /// <summary>
    /// Option names (without leading --) that are safe to pass from YtDlpOptionsJson.
    /// Excludes execution-related options (exec, postprocessor-args, etc.) to prevent command injection.
    /// </summary>
    private static readonly HashSet<string> AllowedYtDlpOptionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "user-agent", "referer", "add-header", "proxy", "no-check-certificate",
        "retries", "fragment-retries", "file-access-retries", "concurrent-fragments",
        "sleep-interval", "sleep-requests", "throttle", "socket-timeout",
        "source-address", "force-ipv4", "force-ipv6", "geo-bypass", "geo-verification-proxy",
        "format", "merge-output-format", "prefer-free-formats", "no-playlist",
        "cookies", "cookies-from-browser", "no-cookies-from-browser",
        "no-warnings", "no-progress", "ignore-errors", "abort-on-error"
    };

    private static readonly Dictionary<string, string> QualityFormat = new(StringComparer.OrdinalIgnoreCase)
    {
        ["best"] = "best",
        ["1080p"] = "best[height<=1080]",
        ["720p"] = "best[height<=720]",
        ["480p"] = "best[height<=480]"
    };

    private readonly PluginConfiguration _config;
    private readonly IApplicationPaths? _applicationPaths;
    private readonly ILogger _logger;
    private readonly IHttpClientFactory? _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="YtDlpRunner"/> class.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="logger">Logger instance.</param>
    public YtDlpRunner(PluginConfiguration config, ILogger<YtDlpRunner> logger)
        : this(config, null, logger, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="YtDlpRunner"/> class.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="applicationPaths">Application paths (optional, for plugin-managed yt-dlp).</param>
    /// <param name="logger">Logger instance.</param>
    public YtDlpRunner(PluginConfiguration config, IApplicationPaths? applicationPaths, ILogger<YtDlpRunner> logger)
        : this(config, applicationPaths, logger, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="YtDlpRunner"/> class.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="applicationPaths">Application paths (optional, for plugin-managed yt-dlp).</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="httpClientFactory">Optional HTTP client factory for yt-dlp binary download (preferred when available from host DI).</param>
    public YtDlpRunner(PluginConfiguration config, IApplicationPaths? applicationPaths, ILogger<YtDlpRunner> logger, IHttpClientFactory? httpClientFactory)
    {
        _config = config;
        _applicationPaths = applicationPaths;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Gets the yt-dlp format string for the configured quality.
    /// </summary>
    /// <returns>Format string for yt-dlp -f (e.g. best[height&lt;=720]).</returns>
    public string GetFormatForQuality()
    {
        var q = (_config.Quality ?? "720p").Trim();
        return QualityFormat.TryGetValue(q, out var format) ? format : QualityFormat["720p"];
    }

    /// <summary>Normalize configured path: trim whitespace and strip surrounding double-quotes.</summary>
    private static string NormalizeExePath(string? path, string defaultPath)
    {
        if (string.IsNullOrWhiteSpace(path))
            return defaultPath;
        var s = path.Trim().Trim('"').Trim();
        return string.IsNullOrEmpty(s) ? defaultPath : s;
    }

    /// <summary>
    /// Resolves the yt-dlp executable path: config path if set and exists, otherwise plugin-managed copy (downloaded if missing).
    /// </summary>
    private async Task<string> GetEffectiveExePathAsync(CancellationToken cancellationToken)
    {
        var configPath = NormalizeExePath(_config.YtDlpPath, "");
        if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
            return configPath;

        if (_applicationPaths == null)
        {
            // No app paths: fall back to legacy default so existing configs still work
            return NormalizeExePath(_config.YtDlpPath, "/usr/local/bin/yt-dlp");
        }

        var bundledPath = YtDlpDownloadHelper.GetBundledExePath(_applicationPaths);
        if (File.Exists(bundledPath))
            return bundledPath;

        _logger.LogInformation("yt-dlp not found; downloading to {Path}", bundledPath);
        var (_, success) = await YtDlpDownloadHelper.DownloadToPluginDirAsync(_applicationPaths, _logger, cancellationToken, _httpClientFactory).ConfigureAwait(false);
        return bundledPath; // Caller will try to run; if !success we get a clear error
    }

    /// <summary>
    /// Check if the configured yt-dlp executable runs (e.g. for settings page). Returns (true, version) or (false, error message).
    /// Auto-downloads the plugin-managed copy if config path is empty and bundled copy is missing.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if available with version message; false with error message.</returns>
    public async Task<(bool Available, string Message)> CheckAvailableAsync(CancellationToken cancellationToken = default)
    {
        var exe = await GetEffectiveExePathAsync(cancellationToken).ConfigureAwait(false);
        if (_applicationPaths != null && exe == YtDlpDownloadHelper.GetBundledExePath(_applicationPaths) && !File.Exists(exe))
        {
            return (false, "Could not download yt-dlp. Check network access and that the plugin folder is writable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--version",
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return (false, "Failed to start process.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = (await stdoutTask.ConfigureAwait(false))?.Trim() ?? "";
            var stderr = (await stderrTask.ConfigureAwait(false))?.Trim() ?? "";

            if (process.ExitCode == 0)
            {
                var version = string.IsNullOrEmpty(stdout) ? stderr : stdout;
                if (version.Length > 80)
                    version = version.Substring(0, 77) + "...";
                return (true, version);
            }

            var err = string.IsNullOrEmpty(stderr) ? stdout : stderr;
            // Python script version of yt-dlp but python3 not in container — tell user to use standalone binary
            if (!string.IsNullOrEmpty(err) && err.IndexOf("python3", StringComparison.OrdinalIgnoreCase) >= 0
                && (err.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase) || err.Contains("not found", StringComparison.OrdinalIgnoreCase)))
            {
                return (false, "The installed yt-dlp is the Python version but python3 is not in the container. Use the Linux standalone binary (no Python): podman exec -u root jellyfin bash -c \"curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux -o /opt/yt-dlp && chmod +x /opt/yt-dlp\". Then set path to /opt/yt-dlp in plugin settings.");
            }
            return (false, string.IsNullOrEmpty(err) ? "Exit code " + process.ExitCode : err);
        }
        catch (Exception ex)
        {
            var isNotFound = ex is System.ComponentModel.Win32Exception w
                && (w.NativeErrorCode == 2 || ex.Message.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase));
            if (isNotFound)
            {
                // Be explicit: file missing vs file there but won't run (permissions, arch, etc.)
                var isAbsolutePath = exe.Contains('/');
                var exists = isAbsolutePath && File.Exists(exe);
                if (exists)
                {
                    return (false, "File exists at \"" + exe + "\" but Jellyfin could not run it (permissions or wrong architecture?). From the host run: podman exec jellyfin chmod +x " + exe + " — or reinstall so the Jellyfin user can execute it.");
                }
                if (isAbsolutePath)
                {
                    return (false, "Jellyfin cannot run \"" + exe + "\" (file not found from Jellyfin’s process). Install yt-dlp in the same container that runs Jellyfin: podman exec jellyfin bash -c \"curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp -o /usr/local/bin/yt-dlp && chmod +x /usr/local/bin/yt-dlp\". If you already did, the container may have been recreated—run the install again.");
                }
            return (false, "Leave the path empty to use the plugin-managed copy (downloaded automatically), or set the full path to your yt-dlp executable.");
                }

            return (false, ex.Message ?? "Unknown error.");
        }
    }

    /// <summary>
    /// Run yt-dlp for one trailer. Returns true if the output file exists after run.
    /// Auto-downloads the plugin-managed copy if config path is empty and bundled copy is missing.
    /// </summary>
    /// <param name="entry">Library entry (movie or TV season folder).</param>
    /// <param name="outputPath">Full path for the output trailer file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the trailer was downloaded and the file exists.</returns>
    public async Task<bool> DownloadOneAsync(
        DownloadListEntry entry,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var query = TitleParser.BuildSearchQuery(entry.Type, entry.Path, entry.Title, entry.Year, entry.Season);
        var url = $"ytsearch1:{query}";
        var format = GetFormatForQuality();

        var args = new List<string>
        {
            "-o", outputPath,
            "--merge-output-format", "mp4",
            "-f", format,
            "--no-warnings",
            "--no-progress",
            url
        };

        // Optional extra options from JSON (allowlisted names only to prevent e.g. --exec injection)
        if (!string.IsNullOrWhiteSpace(_config.YtDlpOptionsJson))
        {
            try
            {
                var opts = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(_config.YtDlpOptionsJson);
                if (opts != null)
                {
                    foreach (var (k, v) in opts)
                    {
                        var optName = (k ?? string.Empty).TrimStart('-');
                        if (string.IsNullOrEmpty(optName)) continue;
                        if (!AllowedYtDlpOptionNames.Contains(optName))
                        {
                            _logger.LogDebug("YtDlpOptionsJson: skipping disallowed option \"{Option}\". Only allowlisted options are applied.", optName);
                            continue;
                        }
                        var val = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
                        if (val != null)
                        {
                            args.Add("--" + optName);
                            args.Add(val);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                _logger.LogWarning("Invalid YtDlpOptionsJson; ignoring.");
            }
        }

        var exe = await GetEffectiveExePathAsync(cancellationToken).ConfigureAwait(false);
        if (_applicationPaths != null && exe == YtDlpDownloadHelper.GetBundledExePath(_applicationPaths) && !File.Exists(exe))
        {
            _logger.LogWarning("yt-dlp could not be downloaded; skipping.");
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args)
            startInfo.ArgumentList.Add(a);

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                _logger.LogWarning("Failed to start yt-dlp for: {Query}. Is yt-dlp installed and on PATH (or set in plugin settings)?", query);
                return false;
            }

            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stderr = (await stderrTask.ConfigureAwait(false)) ?? "";
            var stdout = (await stdoutTask.ConfigureAwait(false)) ?? "";

            if (process.ExitCode != 0)
            {
                var reason = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                var trimmed = reason?.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    trimmed = "No output from yt-dlp (exit code " + process.ExitCode + ")";
                _logger.LogWarning("yt-dlp failed for \"{Query}\": {Reason}", query, trimmed);
                return false;
            }

            if (!File.Exists(outputPath))
            {
                var reason = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                _logger.LogWarning("yt-dlp succeeded but file missing for \"{Query}\": {Output}", query, (reason ?? "").Trim());
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // Process.Start throws Win32Exception when the executable is missing (ENOENT). The framework
            // message says "No such file or directory" and mentions WorkingDirectory, which is misleading —
            // media paths are correct (from Jellyfin library roots); the only thing missing is the yt-dlp binary.
            var isExecutableNotFound = ex is System.ComponentModel.Win32Exception win32
                && (win32.NativeErrorCode == 2 || ex.Message.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase));
            if (isExecutableNotFound)
            {
                _logger.LogWarning(
                    "yt-dlp executable not found. Path used: {Exe}. Leave the path empty in plugin settings to use the auto-downloaded copy, or set the full path to your yt-dlp executable.",
                    exe);
            }
            else
            {
                _logger.LogWarning(ex, "yt-dlp failed for \"{Query}\": {Message}", query, ex.Message);
            }
            return false;
        }
    }

    /// <summary>
    /// Download a trailer from a direct URL (e.g. from TMDB/OMDb metadata). Returns true if the output file exists after run.
    /// </summary>
    /// <param name="url">Direct trailer URL (YouTube, etc.).</param>
    /// <param name="outputPath">Full path for the output trailer file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the trailer was downloaded and the file exists.</returns>
    public async Task<bool> DownloadFromUrlAsync(
        string url,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogWarning("DownloadFromUrlAsync: empty URL.");
            return false;
        }

        var format = GetFormatForQuality();
        var args = new List<string>
        {
            "-o", outputPath,
            "--merge-output-format", "mp4",
            "-f", format,
            "--no-warnings",
            "--no-progress",
            url.Trim()
        };

        if (!string.IsNullOrWhiteSpace(_config.YtDlpOptionsJson))
        {
            try
            {
                var opts = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(_config.YtDlpOptionsJson);
                if (opts != null)
                {
                    foreach (var (k, v) in opts)
                    {
                        var optName = (k ?? string.Empty).TrimStart('-');
                        if (string.IsNullOrEmpty(optName)) continue;
                        if (!AllowedYtDlpOptionNames.Contains(optName)) continue;
                        var val = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
                        if (val != null)
                        {
                            args.Add("--" + optName);
                            args.Add(val);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                _logger.LogDebug("Invalid YtDlpOptionsJson; ignoring for direct URL download.");
            }
        }

        var exe = await GetEffectiveExePathAsync(cancellationToken).ConfigureAwait(false);
        if (_applicationPaths != null && exe == YtDlpDownloadHelper.GetBundledExePath(_applicationPaths) && !File.Exists(exe))
        {
            _logger.LogWarning("yt-dlp could not be downloaded; skipping.");
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args)
            startInfo.ArgumentList.Add(a);

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                _logger.LogWarning("Failed to start yt-dlp for URL: {Url}", url);
                return false;
            }

            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stderr = (await stderrTask.ConfigureAwait(false)) ?? "";
            var stdout = (await stdoutTask.ConfigureAwait(false)) ?? "";

            if (process.ExitCode != 0)
            {
                var reason = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                _logger.LogWarning("yt-dlp failed for URL \"{Url}\": {Reason}", url, (reason ?? "").Trim());
                return false;
            }

            if (!File.Exists(outputPath))
            {
                _logger.LogWarning("yt-dlp succeeded but file missing for URL \"{Url}\".", url);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "yt-dlp failed for URL \"{Url}\": {Message}", url, ex.Message);
            return false;
        }
    }
}
