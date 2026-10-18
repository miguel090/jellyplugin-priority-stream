using Jellyfin.Plugin.LiveTvPriority.Interception;
using Jellyfin.Plugin.LiveTvPriority.Preemption;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LiveTvPriority;

/// <summary>
/// Registers the plugin services and hooks the action filter into the MVC pipeline.
/// </summary>
/// <remarks>
/// <see cref="IPluginServiceRegistrator.RegisterServices"/> runs in ApplicationHost.Init on the same
/// service collection the web host's Startup.ConfigureServices uses, so configuring
/// <see cref="MvcOptions"/> here adds a global filter to every API controller action.
/// </remarks>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<TunerArbiter>();
        serviceCollection.AddSingleton<PreemptionService>();
        serviceCollection.AddSingleton<LiveTvPriorityActionFilter>();
        serviceCollection.Configure<MvcOptions>(options => options.Filters.AddService<LiveTvPriorityActionFilter>());
    }
}
