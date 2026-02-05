namespace Jellyfin.Plugin.JellyTrailers;

/// <summary>
/// Abstraction for scanning library roots and building download entries (enables testing with mocks).
/// </summary>
public interface ILibraryScanner
{
    /// <summary>
    /// Get library root paths grouped by content type, optionally filtered by library name.
    /// </summary>
    IReadOnlyList<(string Path, string Type)> GetLibraryRoots(
        IReadOnlySet<string>? includeLibraryNames = null,
        IReadOnlySet<string>? excludeLibraryNames = null);

    /// <summary>
    /// Scan filesystem from library roots and return entries (path, type, added_at).
    /// </summary>
    List<DownloadListEntry> ScanAndEnrich(IReadOnlyList<(string Path, string Type)> roots, string addedAt);
}
