using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrailers;

/// <summary>
/// Downloads the correct yt-dlp executable for the current OS into the plugin data folder.
/// </summary>
public static class YtDlpDownloadHelper
{
    private const string BaseUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";

    /// <summary>
    /// Gets the path where the plugin stores (or will store) the yt-dlp executable for the current platform.
    /// </summary>
    public static string GetBundledExePath(IApplicationPaths applicationPaths)
    {
        var dataDir = Path.Combine(applicationPaths.PluginConfigurationsPath, "JellyTrailers");
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp";
        return Path.Combine(dataDir, fileName);
    }

    /// <summary>
    /// Gets the download URL for the current platform.
    /// </summary>
    private static (string FileName, string UrlFileName) GetPlatformNames()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ("yt-dlp.exe", "yt-dlp.exe");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return ("yt-dlp", "yt-dlp_macos");
        return ("yt-dlp", "yt-dlp_linux");
    }

    /// <summary>
    /// Downloads the yt-dlp binary for the current OS into the plugin data folder.
    /// Returns (targetPath, true) on success, (targetPath, false) on failure.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="httpClientFactory">Optional HTTP client factory (preferred when available from host DI).</param>
    public static async Task<(string Path, bool Success)> DownloadToPluginDirAsync(
        IApplicationPaths applicationPaths,
        ILogger logger,
        CancellationToken cancellationToken = default,
        IHttpClientFactory? httpClientFactory = null)
    {
        var dataDir = Path.Combine(applicationPaths.PluginConfigurationsPath, "JellyTrailers");
        var (fileName, urlFileName) = GetPlatformNames();
        var targetPath = Path.Combine(dataDir, fileName);
        var url = BaseUrl + urlFileName;

        try
        {
            Directory.CreateDirectory(dataDir);
            using var client = httpClientFactory != null
                ? httpClientFactory.CreateClient()
                : new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Jellyfin-JellyTrailers-Plugin/1.0");
            byte[] bytes = await client.GetByteArrayAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0)
            {
                logger.LogWarning("yt-dlp download returned empty response from {Url}", url);
                return (targetPath, false);
            }
            await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken).ConfigureAwait(false);
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                const UnixFileMode execMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                File.SetUnixFileMode(targetPath, execMode);
            }
            logger.LogInformation("Downloaded yt-dlp to {Path} ({Size} bytes)", targetPath, bytes.Length);
            return (targetPath, true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to download yt-dlp from {Url}", url);
            return (targetPath, false);
        }
    }
}
