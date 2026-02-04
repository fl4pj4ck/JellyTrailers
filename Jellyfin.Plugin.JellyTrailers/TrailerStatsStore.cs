using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrailers;

/// <summary>
/// Persists run stats (downloaded/failed counts) and returns aggregated stats for the settings page.
/// All file I/O uses a single static semaphore so reads/writes are serialized. If async file I/O
/// is added later, use the same semaphore with <see cref="SemaphoreSlim.WaitAsync()"/> to keep locking consistent.
/// </summary>
public class TrailerStatsStore
{
    private readonly string _dataDir;
    private readonly ILogger _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Single lock for all stats file access. Use Wait() for sync calls; use WaitAsync() when adding async I/O.
    /// </summary>
    private static readonly SemaphoreSlim StorageLock = new(1, 1);

    public TrailerStatsStore(IApplicationPaths applicationPaths, ILogger logger)
    {
        _dataDir = Path.Combine(applicationPaths.PluginConfigurationsPath, "JellyTrailers");
        _logger = logger;
    }

    private string StatsPath => Path.Combine(_dataDir, "stats.json");

    /// <summary>
    /// Record folder counts from the last library scan (total folders and how many already have a trailer).
    /// </summary>
    /// <param name="totalFolders">Total library folders scanned.</param>
    /// <param name="foldersWithTrailer">Number of folders that already have a trailer file.</param>
    public void RecordFolderCounts(int totalFolders, int foldersWithTrailer)
    {
        StorageLock.Wait();
        try
        {
            var data = LoadData();
            data.TotalFolders = totalFolders;
            data.FoldersWithTrailer = foldersWithTrailer;
            SaveData(data);
        }
        finally
        {
            StorageLock.Release();
        }
    }

    /// <summary>
    /// Record a run: add to totals and append to run history (trimmed to last 365 days).
    /// </summary>
    /// <param name="downloaded">Number of trailers downloaded this run.</param>
    /// <param name="failed">Number of failed downloads this run.</param>
    public void RecordRun(int downloaded, int failed)
    {
        RecordProgress(downloaded, failed);
    }

    /// <summary>
    /// Record or update progress for today's run so the stats page updates during the task.
    /// If the last run is from today, update it in place; otherwise add a new run.
    /// </summary>
    /// <param name="downloaded">Number of trailers downloaded so far.</param>
    /// <param name="failed">Number of failed downloads so far.</param>
    public void RecordProgress(int downloaded, int failed)
    {
        StorageLock.Wait();
        try
        {
            var data = LoadData();
            var today = DateTime.UtcNow.Date.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            if (data.Runs.Count > 0 && data.Runs[^1].Date.StartsWith(today.Substring(0, 10), StringComparison.Ordinal))
            {
                var last = data.Runs[^1];
                data.TotalDownloaded -= last.Downloaded;
                data.TotalFailed -= last.Failed;
                last.Downloaded = downloaded;
                last.Failed = failed;
                data.TotalDownloaded += downloaded;
                data.TotalFailed += failed;
            }
            else
            {
                data.TotalDownloaded += downloaded;
                data.TotalFailed += failed;
                data.Runs.Add(new RunRecord { Date = today, Downloaded = downloaded, Failed = failed });
            }
            var cutoff = DateTime.UtcNow.AddDays(-365).Date;
            data.Runs = data.Runs
                .Where(r => DateTime.TryParse(r.Date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) && d >= cutoff)
                .ToList();
            SaveData(data);
        }
        finally
        {
            StorageLock.Release();
        }
    }

    /// <summary>
    /// Reset all stats (totals and run history).
    /// </summary>
    public void Reset()
    {
        StorageLock.Wait();
        try
        {
            SaveData(new StatsData());
            _logger.LogInformation("Trailer stats reset");
        }
        finally
        {
            StorageLock.Release();
        }
    }

    /// <summary>
    /// Get aggregated stats for display.
    /// TotalDownloaded is at least FoldersWithTrailer so the UI reflects trailers on disk when run history was reset or is missing.
    /// </summary>
    /// <returns>Aggregated trailer stats for the settings page.</returns>
    public TrailerStats GetStats()
    {
        StorageLock.Wait();
        try
        {
            var data = LoadData();
            var now = DateTime.UtcNow;
            var runs = data.Runs
                .Select(r => DateTime.TryParse(r.Date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? (d, r.Downloaded, r.Failed) : (default(DateTime), 0, 0))
                .Where(t => t.Item1 != default)
                .ToList();

            var last7 = runs.Where(t => t.Item1 >= now.AddDays(-7)).Sum(t => t.Item2);
            var last30 = runs.Where(t => t.Item1 >= now.AddDays(-30)).Sum(t => t.Item2);
            var last365 = runs.Where(t => t.Item1 >= now.AddDays(-365)).Sum(t => t.Item2);
            // So display matches reality: if we have more folders with trailers than recorded downloads (e.g. stats reset, or pre-stats downloads), use folder count as floor
            var totalDownloaded = data.TotalDownloaded >= data.FoldersWithTrailer
                ? data.TotalDownloaded
                : Math.Max(data.TotalDownloaded, data.FoldersWithTrailer);

            return new TrailerStats
            {
                TotalDownloaded = totalDownloaded,
                TotalFailed = data.TotalFailed,
                TotalFolders = data.TotalFolders,
                FoldersWithTrailer = data.FoldersWithTrailer,
                Last7Days = last7,
                Last30Days = last30,
                Last365Days = last365,
                LastRunDate = data.Runs.Count > 0 ? data.Runs[^1].Date : null,
                LastRunDownloaded = data.Runs.Count > 0 ? data.Runs[^1].Downloaded : 0,
                LastRunFailed = data.Runs.Count > 0 ? data.Runs[^1].Failed : 0,
                TotalRuns = data.Runs.Count
            };
        }
        finally
        {
            StorageLock.Release();
        }
    }

    private StatsData LoadData()
    {
        var path = StatsPath;
        if (!File.Exists(path))
            return new StatsData();

        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<StatsData>(json, JsonOptions);
            return data ?? new StatsData();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load stats from {Path}", path);
            return new StatsData();
        }
    }

    private void SaveData(StatsData data)
    {
        Directory.CreateDirectory(_dataDir);
        try
        {
            var json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(StatsPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save stats to {Path}", StatsPath);
        }
    }

    private class StatsData
    {
        [JsonPropertyName("totalDownloaded")]
        public int TotalDownloaded { get; set; }

        [JsonPropertyName("totalFailed")]
        public int TotalFailed { get; set; }

        [JsonPropertyName("totalFolders")]
        public int TotalFolders { get; set; }

        [JsonPropertyName("foldersWithTrailer")]
        public int FoldersWithTrailer { get; set; }

        [JsonPropertyName("runs")]
        public List<RunRecord> Runs { get; set; } = new();
    }

    private class RunRecord
    {
        [JsonPropertyName("date")]
        public string Date { get; set; } = string.Empty;

        [JsonPropertyName("downloaded")]
        public int Downloaded { get; set; }

        [JsonPropertyName("failed")]
        public int Failed { get; set; }
    }
}

/// <summary>
/// Aggregated stats returned for the settings page.
/// </summary>
public class TrailerStats
{
    [JsonPropertyName("totalDownloaded")]
    public int TotalDownloaded { get; set; }

    [JsonPropertyName("totalFailed")]
    public int TotalFailed { get; set; }

    [JsonPropertyName("last7Days")]
    public int Last7Days { get; set; }

    [JsonPropertyName("last30Days")]
    public int Last30Days { get; set; }

    [JsonPropertyName("last365Days")]
    public int Last365Days { get; set; }

    [JsonPropertyName("lastRunDate")]
    public string? LastRunDate { get; set; }

    [JsonPropertyName("lastRunDownloaded")]
    public int LastRunDownloaded { get; set; }

    [JsonPropertyName("lastRunFailed")]
    public int LastRunFailed { get; set; }

    [JsonPropertyName("totalRuns")]
    public int TotalRuns { get; set; }

    [JsonPropertyName("totalFolders")]
    public int TotalFolders { get; set; }

    [JsonPropertyName("foldersWithTrailer")]
    public int FoldersWithTrailer { get; set; }
}
