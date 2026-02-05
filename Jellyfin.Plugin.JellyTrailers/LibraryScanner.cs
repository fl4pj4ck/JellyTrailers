using System.Collections.Generic;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrailers;

/// <summary>
/// Scans Jellyfin library roots and filesystem to build a list of movie/TV folder entries
/// Movies = direct subdirs of each movie root; TV = S01/S02 or show folders.
/// </summary>
public class LibraryScanner : ILibraryScanner
{
    private static readonly System.Text.RegularExpressions.Regex TvSeasonSubdir =
        new(@"^S\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

    public LibraryScanner(ILibraryManager libraryManager, ILogger<LibraryScanner> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Get library root paths grouped by content type (Movie, TvShow), optionally filtered by library name.
    /// </summary>
    /// <param name="includeLibraryNames">If non-empty, only include roots whose library name is in this set (case-insensitive).</param>
    /// <param name="excludeLibraryNames">Exclude roots whose library name is in this set (case-insensitive).</param>
    /// <returns>List of (path, type) for each root.</returns>
    public IReadOnlyList<(string Path, string Type)> GetLibraryRoots(
        IReadOnlySet<string>? includeLibraryNames = null,
        IReadOnlySet<string>? excludeLibraryNames = null)
    {
        var result = new List<(string Path, string Type)>();
        try
        {
            var folders = _libraryManager.GetVirtualFolders();
            foreach (var folder in folders)
            {
                var collectionType = folder.CollectionType?.ToString().Trim().ToLowerInvariant() ?? string.Empty;
                var type = collectionType switch
                {
                    "movies" => "movie",
                    "movie" => "movie",
                    "tvshows" => "tvshow",
                    "tv" => "tvshow",
                    _ => string.Empty
                };

                if (string.IsNullOrEmpty(type))
                    continue;

                var libraryName = folder.Name?.Trim() ?? string.Empty;
                if (includeLibraryNames != null && includeLibraryNames.Count > 0 && !includeLibraryNames.Contains(libraryName))
                    continue;
                if (excludeLibraryNames != null && excludeLibraryNames.Count > 0 && excludeLibraryNames.Contains(libraryName))
                    continue;

                if (folder.Locations == null)
                    continue;

                foreach (var loc in folder.Locations)
                {
                    if (string.IsNullOrWhiteSpace(loc))
                        continue;
                    var path = System.IO.Path.GetFullPath(loc.Trim());
                    if (System.IO.Directory.Exists(path))
                    {
                        result.Add((path, type));
                        _logger.LogDebug("Library root: {Path} ({Type}, library: {Name})", path, type, libraryName);
                    }
                    else
                    {
                        _logger.LogWarning("Library path does not exist: {Path}", path);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get library roots");
        }

        return result;
    }

    /// <summary>
    /// Scan filesystem from library roots and return entries (path, type, added_at).
    /// Movies: one entry per direct subdir of a movie root.
    /// TV: structure 1 = ShowName/S01, S02 → one entry per season dir; structure 2 = one entry per show dir.
    /// </summary>
    public List<DownloadListEntry> ScanAndEnrich(IReadOnlyList<(string Path, string Type)> roots, string addedAt)
    {
        var entries = new List<DownloadListEntry>();
        foreach (var (rootPath, type) in roots)
        {
            if (type == "movie")
                ScanMovies(rootPath, addedAt, entries);
            else
                ScanTv(rootPath, addedAt, entries);
        }

        EnrichEntries(entries);
        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return entries;
    }

    private void ScanMovies(string basePath, string addedAt, List<DownloadListEntry> entries)
    {
        if (!Directory.Exists(basePath))
        {
            _logger.LogInformation("Movies path does not exist: {Path}", basePath);
            return;
        }

        try
        {
            var dirs = Directory.GetDirectories(basePath).OrderBy(Path.GetFileName).ToArray();
            foreach (var dir in dirs)
            {
                entries.Add(new DownloadListEntry
                {
                    Path = Path.GetFullPath(dir),
                    Type = "movie",
                    AddedAt = addedAt
                });
            }
            _logger.LogInformation("Scanned movies: {Path} ({Count} entries)", basePath, dirs.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not scan movies path {Path}", basePath);
        }
    }

    private void ScanTv(string basePath, string addedAt, List<DownloadListEntry> entries)
    {
        if (!Directory.Exists(basePath))
        {
            _logger.LogInformation("TV path does not exist: {Path}", basePath);
            return;
        }

        try
        {
            var struct1 = 0;
            var struct2 = 0;
            var showDirs = Directory.GetDirectories(basePath).OrderBy(Path.GetFileName).ToArray();
            foreach (var showDir in showDirs)
            {
                var seasonSubdirs = Directory.GetDirectories(showDir)
                    .Where(d => TvSeasonSubdir.IsMatch(Path.GetFileName(d) ?? string.Empty))
                    .OrderBy(Path.GetFileName)
                    .ToArray();

                if (seasonSubdirs.Length > 0)
                {
                    struct1 += seasonSubdirs.Length;
                    foreach (var seasonDir in seasonSubdirs)
                    {
                        entries.Add(new DownloadListEntry
                        {
                            Path = Path.GetFullPath(seasonDir),
                            Type = "tvshow",
                            AddedAt = addedAt
                        });
                    }
                }
                else
                {
                    struct2++;
                    entries.Add(new DownloadListEntry
                    {
                        Path = Path.GetFullPath(showDir),
                        Type = "tvshow",
                        AddedAt = addedAt
                    });
                }
            }
            var tvAdded = struct1 + struct2;
            _logger.LogInformation(
                "Scanned TV: {Path} (structure 1 seasons: {S1}, structure 2 folders: {S2}, total TV entries: {Total})",
                basePath, struct1, struct2, tvAdded);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not scan TV path {Path}", basePath);
        }
    }

    private void EnrichEntries(List<DownloadListEntry> entries)
    {
        foreach (var e in entries)
        {
            if (e.Type == "movie")
            {
                var (title, year) = TitleParser.ParseMovie(e.Path);
                e.Title = title;
                e.Year = year;
            }
            else
            {
                var (title, season, year) = TitleParser.ParseTvShow(e.Path);
                e.Title = title;
                e.Season = season;
                e.Year = year;
            }
        }
    }
}
