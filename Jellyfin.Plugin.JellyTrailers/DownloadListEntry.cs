using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyTrailers;

/// <summary>
/// One entry in the persistent download list (a movie or TV season folder).
/// </summary>
public class DownloadListEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "movie"; // "movie" or "tvshow"

    [JsonPropertyName("added_at")]
    public string AddedAt { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("season")]
    public int? Season { get; set; }
}
