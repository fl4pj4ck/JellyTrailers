using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTrailers;
using Jellyfin.Plugin.JellyTrailers.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrailers.Api;

/// <summary>
/// API controller for JellyTrailers plugin (stats and yt-dlp check).
/// Endpoints require authentication; intended for use from the Dashboard config page (admin).
/// </summary>
[Route("Plugins/JellyTrailers")]
[ApiController]
[Authorize]
public class JellyTrailersController : ControllerBase
{
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<JellyTrailersController> _logger;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyTrailersController"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public JellyTrailersController(IApplicationPaths applicationPaths, ILogger<JellyTrailersController> logger, ILoggerFactory loggerFactory)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

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
    /// Check if yt-dlp is available with the current plugin configuration (for settings page warning).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Availability and version or error message.</returns>
    [HttpGet("YtDlpCheck")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<YtDlpCheckResult>> GetYtDlpCheck(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return Ok(new YtDlpCheckResult { Available = false, Message = "Plugin not initialized." });
        }

        var runner = new YtDlpRunner(config, _applicationPaths, _loggerFactory.CreateLogger<YtDlpRunner>());
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
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<YtDlpDownloadResult>> DownloadYtDlp(CancellationToken cancellationToken)
    {
        var targetPath = YtDlpDownloadHelper.GetBundledExePath(_applicationPaths);
        var (path, success) = await YtDlpDownloadHelper.DownloadToPluginDirAsync(_applicationPaths, _logger, cancellationToken).ConfigureAwait(false);
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
    public ActionResult<TrailerStats> GetStats()
    {
        try
        {
            var store = new TrailerStatsStore(_applicationPaths, _loggerFactory.CreateLogger<TrailerStatsStore>());
            var stats = store.GetStats();
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get trailer stats");
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Reset trailer download stats (totals and run history).
    /// </summary>
    /// <returns>204 No Content on success.</returns>
    [HttpPost("Stats/Reset")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public ActionResult ResetStats()
    {
        try
        {
            var store = new TrailerStatsStore(_applicationPaths, _loggerFactory.CreateLogger<TrailerStatsStore>());
            store.Reset();
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset trailer stats");
            return StatusCode(StatusCodes.Status500InternalServerError);
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
