using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTrailers;
using Jellyfin.Plugin.JellyTrailers.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrailers.Api;

/// <summary>
/// API controller for JellyTrailers plugin (stats and yt-dlp check).
/// Endpoints require authentication; intended for use from the Dashboard config page (admin).
/// When running on Jellyfin, config and services are injected via DI; otherwise resolved from Plugin.Instance and new (Emby compatibility).
/// </summary>
[Route("Plugins/JellyTrailers")]
[ApiController]
[Authorize]
public class JellyTrailersController : ControllerBase
{
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<JellyTrailersController> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PluginConfiguration? _config;
    private readonly IYtDlpRunner? _ytDlpRunner;
    private readonly ITrailerStatsStore? _statsStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyTrailersController"/> class.
    /// </summary>
    public JellyTrailersController(
        IApplicationPaths applicationPaths,
        ILogger<JellyTrailersController> logger,
        ILoggerFactory loggerFactory,
        IHttpClientFactory httpClientFactory,
        PluginConfiguration? config = null,
        IYtDlpRunner? ytDlpRunner = null,
        ITrailerStatsStore? statsStore = null)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _ytDlpRunner = ytDlpRunner;
        _statsStore = statsStore;
    }

    private PluginConfiguration Config => _config ?? Plugin.Instance!.Configuration;

    /// <summary>
    /// Gets a short message from an exception for API responses.
    /// </summary>
    /// <param name="ex">The exception.</param>
    /// <returns>Exception message or type name.</returns>
    private static string GetExceptionMessage(Exception ex)
    {
        if (ex == null) return string.Empty;
        var msg = ex.Message?.Trim();
        if (!string.IsNullOrEmpty(msg)) return msg;
        if (ex.InnerException != null)
        {
            msg = ex.InnerException.Message?.Trim();
            if (!string.IsNullOrEmpty(msg)) return msg;
        }
        return ex.GetType().Name;
    }

    /// <summary>
    /// Get the current plugin version (for display on settings page).
    /// </summary>
    /// <returns>Plugin version and ID (config page uses PluginId for getPluginConfiguration).</returns>
    [HttpGet("Version")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PluginVersionResult> GetVersion()
    {
        var version = typeof(Plugin).Assembly.GetName().Version;
        var versionString = version != null ? version.ToString() : "0.0.0";
        var ytDlpOptionsJsonInvalid = Config.YtDlpOptionsJsonWasInvalid;

        return Ok(new PluginVersionResult
        {
            Version = versionString,
            PluginId = PluginConstants.PluginId,
            YtDlpOptionsJsonInvalid = ytDlpOptionsJsonInvalid
        });
    }

    /// <summary>
    /// Check if yt-dlp is available with the current plugin configuration (for settings page warning).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Availability and version or error message.</returns>
    [HttpGet("YtDlpCheck")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<YtDlpCheckResult>> GetYtDlpCheck(CancellationToken cancellationToken)
    {
        var runner = _ytDlpRunner ?? new YtDlpRunner(Config, _applicationPaths, _loggerFactory.CreateLogger<YtDlpRunner>(), _httpClientFactory);
        var (available, message) = await runner.CheckAvailableAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new YtDlpCheckResult { Available = available, Message = message });
    }

    /// <summary>
    /// Download yt-dlp binary into plugin data folder (manual trigger from settings page).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Download result with path and success status.</returns>
    [HttpPost("YtDlpDownload")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResult), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<YtDlpDownloadResult>> DownloadYtDlp(CancellationToken cancellationToken)
    {
        var targetPath = YtDlpDownloadHelper.GetBundledExePath(_applicationPaths);
        var (path, success) = await YtDlpDownloadHelper.DownloadToPluginDirAsync(_applicationPaths, _logger, cancellationToken, _httpClientFactory).ConfigureAwait(false);
        if (success)
            return Ok(new YtDlpDownloadResult { Success = true, Path = path, Message = "Downloaded successfully. Leave the path empty in settings to use this copy." });
        return Ok(new YtDlpDownloadResult { Success = false, Path = targetPath, Message = "Download failed. Check Jellyfin logs for details." });
    }

    /// <summary>
    /// Get trailer download stats for the settings page.
    /// </summary>
    /// <returns>Aggregated trailer stats.</returns>
    [HttpGet("Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResult), StatusCodes.Status500InternalServerError)]
    public ActionResult<TrailerStats> GetStats()
    {
        try
        {
            var store = _statsStore ?? new TrailerStatsStore(_applicationPaths, _loggerFactory.CreateLogger<TrailerStatsStore>());
            var stats = store.GetStats();
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get trailer stats");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new ApiErrorResult
                {
                    Error = "Failed to load trailer stats",
                    Detail = GetExceptionMessage(ex)
                });
        }
    }

    /// <summary>
    /// Reset trailer download stats (totals and run history).
    /// </summary>
    /// <returns>204 No Content on success.</returns>
    [HttpPost("Stats/Reset")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResult), StatusCodes.Status500InternalServerError)]
    public ActionResult ResetStats()
    {
        try
        {
            var store = _statsStore ?? new TrailerStatsStore(_applicationPaths, _loggerFactory.CreateLogger<TrailerStatsStore>());
            store.Reset();
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset trailer stats");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new ApiErrorResult
                {
                    Error = "Failed to reset trailer stats",
                    Detail = GetExceptionMessage(ex)
                });
        }
    }
}

/// <summary>Result of yt-dlp availability check for the settings page.</summary>
public class YtDlpCheckResult
{
    public bool Available { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>Result of yt-dlp download for the settings page.</summary>
public class YtDlpDownloadResult
{
    public bool Success { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>Plugin version and ID for the settings page.</summary>
public class PluginVersionResult
{
    public string Version { get; set; } = string.Empty;

    /// <summary>Plugin GUID so the config page never gets out of sync with PluginConstants.PluginId.</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>True when YtDlpOptionsJson was invalid on last check (config page shows warning).</summary>
    public bool YtDlpOptionsJsonInvalid { get; set; }
}

/// <summary>JSON error payload returned when an API endpoint fails (e.g. 500).</summary>
public class ApiErrorResult
{
    /// <summary>Short error description for the client.</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>Optional detail (e.g. exception message) for debugging.</summary>
    public string? Detail { get; set; }
}
