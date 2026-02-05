namespace Jellyfin.Plugin.JellyTrailers;

/// <summary>
/// Single source of truth for plugin identity. Keep in sync with manifest.json guid.
/// Config page gets PluginId from Plugins/JellyTrailers/Version so it never gets out of sync.
/// </summary>
public static class PluginConstants
{
    /// <summary>
    /// Plugin GUID. Must match manifest.json "guid" and the pluginId in Configuration/trailers.html.
    /// </summary>
    public const string PluginId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
}
