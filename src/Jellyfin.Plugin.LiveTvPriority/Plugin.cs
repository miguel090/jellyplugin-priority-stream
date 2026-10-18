using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.LiveTvPriority.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.LiveTvPriority;

/// <summary>
/// Live TV Priority plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin id. Must match <c>guid</c> in manifest.json / build.yaml.</summary>
    public static readonly Guid PluginId = Guid.Parse("443cd6fe-a768-4ae2-a39e-e87e0e8e359d");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Live TV Priority";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description => "Lets priority users take over the Live TV tuner from other sessions.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}
