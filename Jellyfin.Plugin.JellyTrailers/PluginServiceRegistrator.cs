using Jellyfin.Plugin.JellyTrailers.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyTrailers;

/// <summary>
/// Registers plugin services in DI when running on Jellyfin. Uses a parameterless constructor so the host can instantiate it.
/// Plugin.Instance is used as fallback when this registrator is not used (e.g. some Emby hosts).
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        if (Plugin.Instance == null)
            return;

        serviceCollection.AddSingleton(Plugin.Instance);
        serviceCollection.AddSingleton(Plugin.Instance.Configuration);
        serviceCollection.AddSingleton<PluginConfiguration>(Plugin.Instance.Configuration);
        serviceCollection.AddSingleton<IYtDlpRunner, YtDlpRunner>();
        serviceCollection.AddSingleton<ITrailerStatsStore, TrailerStatsStore>();
        serviceCollection.AddSingleton<ILibraryScanner, LibraryScanner>();
    }
}
