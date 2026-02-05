using Jellyfin.Plugin.JellyTrailers.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyTrailers;

/// <summary>
/// JellyTrailers plugin: download movie and TV trailers with yt-dlp.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IPluginServiceRegistrator
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
    /// Gets the plugin instance. Kept for legacy or places where DI is not available.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Register via factory so resolution happens when the service is first requested (by then
        // the host has constructed Plugin and Instance is set). This avoids "Unable to resolve
        // service for type PluginConfiguration" when the host calls RegisterServices before
        // instantiating the plugin.
        serviceCollection.AddSingleton<Plugin>(_ => Instance ?? throw new InvalidOperationException("JellyTrailers.Plugin not initialized."));
        serviceCollection.AddSingleton<PluginConfiguration>(_ => Instance?.Configuration ?? throw new InvalidOperationException("JellyTrailers.Plugin not initialized."));
    }

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
