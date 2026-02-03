using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyTrailers;

/// <summary>
/// Parses movie and TV show title/year/season from folder paths (release-style names).
/// Port of Python title_parser logic without guessit; regex fallbacks only.
/// </summary>
public static class TitleParser
{
    // TV: basename is just S01, S02 (structure 1: ShowName/S01)
    private static readonly Regex TvSeasonOnly = new(@"^S(\d+)$", RegexOptions.IgnoreCase);
    // TV: season in folder name (structure 2: Show.Name.S01.1080p...)
    private static readonly Regex TvSeasonInName = new(@"\.S(\d+)(?:\.|$)", RegexOptions.IgnoreCase);
    // Year: 1880-2030
    private static readonly Regex YearToken = new(@"^(19|20)\d{2}$");

    // Tokens to strip from release-style names so YouTube gets a searchable show/movie name
    private static readonly HashSet<string> ReleaseJunkTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "1080p", "720p", "480p", "2160p", "4k", "uhd",
        "web-dl", "webdl", "webrip", "bluray", "blu-ray", "hdtv", "dvdrip", "brrip",
        "nf", "hmax", "amzn", "atvp", "dsnp", "disney", "hulu",
        "ddp51", "dd51", "dd5", "atmos", "dts", "aac", "ac3", "eac3",
        "x264", "x265", "h264", "h265", "hevc", "avc",
        "multi", "subbed", "dubbed",
        "extended", "uncut", "remastered", "repack", "proper"
    };

    /// <summary>
    /// Strip release/codec/source junk from a title so YouTube search gets a short show/movie name.
    /// e.g. "Baby.Bandito.MULTi.1080p.NF.WEB-DL" -> "Baby Bandito"
    /// </summary>
    public static string CleanTitleForSearch(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title ?? string.Empty;
        var normalized = title.Replace(".", " ").Replace("-", " ").Replace("  ", " ").Trim();
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>();
        foreach (var t in tokens)
        {
            var s = t.Trim();
            if (string.IsNullOrEmpty(s)) continue;
            // Drop release junk
            if (ReleaseJunkTokens.Contains(s)) continue;
            // Drop numbers that are clearly not part of a title (3+ digits: year, 264, 1080)
            if (Regex.IsMatch(s, @"^\d{3,}$")) continue;
            // Drop season markers (S01, S01-S05)
            if (Regex.IsMatch(s, @"^S\d+(-S\d+)?$", RegexOptions.IgnoreCase)) continue;
            // Drop single letters (e.g. H from H264)
            if (s.Length <= 1) continue;
            // Drop typical release-group style (short all-caps/alphanumeric, e.g. TV4TG, ViSiON)
            if (s.Length <= 8 && Regex.IsMatch(s, @"^[A-Z0-9\-]+$")) continue;
            // Drop tokens that look like codec/source (e.g. DDP5, DD5)
            if (Regex.IsMatch(s, @"^(DDP?|DD)\d", RegexOptions.IgnoreCase)) continue;
            kept.Add(s);
            // Keep at most 5 words so query stays "Show Name trailer" not the whole release
            if (kept.Count >= 5) break;
        }
        var result = string.Join(" ", kept).Trim();
        return string.IsNullOrEmpty(result) ? normalized : result;
    }

    /// <summary>
    /// Parse movie folder name into title and optional year.
    /// </summary>
    public static (string Title, int? Year) ParseMovie(string pathOrBasename)
    {
        var basename = Path.GetFileName(pathOrBasename.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            ?? pathOrBasename;

        var tokens = basename.Replace("-", ".").Split('.');
        for (var i = 0; i < tokens.Length; i++)
        {
            if (YearToken.IsMatch(tokens[i]) && int.TryParse(tokens[i], out var y) && y >= 1880 && y <= 2030)
            {
                var titleTokens = tokens.Take(i).ToList();
                var title = string.Join(" ", titleTokens).Replace("  ", " ").Trim();
                return (string.IsNullOrWhiteSpace(title) ? basename : title, y);
            }
        }

        return (basename, null);
    }

    /// <summary>
    /// Parse TV show folder into title, optional season, optional year.
    /// Structure 1: .../ShowName/S01 → title from ShowName, season from S01.
    /// Structure 2: Show.Name.S01.1080p... → title and season from basename.
    /// </summary>
    public static (string Title, int? Season, int? Year) ParseTvShow(string pathOrBasename)
    {
        var path = pathOrBasename.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var basename = Path.GetFileName(path) ?? path;
        var parentName = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty) ?? string.Empty;

        // Structure 1: basename is S01, S02, ...
        var m = TvSeasonOnly.Match(basename);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var season1))
        {
            var (title, year) = ParseMovie(parentName);
            return (title, season1, year);
        }

        // Structure 2: .S01. or .S02. in basename
        var m2 = TvSeasonInName.Match(basename);
        if (m2.Success && int.TryParse(m2.Groups[1].Value, out var season2))
        {
            var titlePart = basename[..m2.Index].Replace(".", " ").Replace("  ", " ").Trim();
            return (string.IsNullOrWhiteSpace(titlePart) ? basename : titlePart, season2, null);
        }

        return (basename.Replace(".", " ").Trim(), null, null);
    }

    /// <summary>
    /// Build YouTube search query for a trailer (movie or TV).
    /// Uses CleanTitleForSearch so we search e.g. "Baby Bandito trailer" not the full release name.
    /// </summary>
    public static string BuildSearchQuery(string type, string path, string? title, int? year, int? season)
    {
        var rawTitle = title ?? Path.GetFileName(path) ?? "Unknown";
        var effectiveTitle = CleanTitleForSearch(rawTitle);
        if (string.IsNullOrWhiteSpace(effectiveTitle)) effectiveTitle = rawTitle;

        if (string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase))
        {
            if (year.HasValue)
                return $"{effectiveTitle} {year} trailer";
            return $"{effectiveTitle} trailer";
        }

        if (season.HasValue)
            return $"{effectiveTitle} season {season} trailer";
        return $"{effectiveTitle} trailer";
    }
}
