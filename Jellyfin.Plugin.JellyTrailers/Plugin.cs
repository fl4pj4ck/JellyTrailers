using Jellyfin.Plugin.JellyTrailers.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyTrailers;

/// <summary>
/// JellyTrailers plugin: download movie and TV trailers with yt-dlp.
/// </summary>
/// <remarks>
/// Does not implement IPluginServiceRegistrator: some hosts (e.g. Emby.Server) use
/// Activator.CreateInstance(pluginType) with no args to call RegisterServices, which fails
/// because this plugin has no parameterless constructor. Config is resolved from Instance at runtime.
/// </remarks>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the plugin instance. Used by tasks and API to resolve configuration at runtime (no DI registration).
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "JellyTrailers";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginConstants.PluginId);

    /// <inheritdoc />
    public override string Description => "Download movie and TV show trailers with yt-dlp and place them next to your media.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = "JellyTrailers",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.trailers.html",
        },
    };
}
