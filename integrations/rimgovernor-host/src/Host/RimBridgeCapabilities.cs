using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Host.Core;
using RimGovernor.Host.Contracts;

namespace RimGovernor.Host;

internal static class RimBridgeCapabilities
{
    public static CapabilityRegistry Registry { get; private set; }

    public static OperationJournal Journal { get; private set; }

    public static LogJournal LogJournal { get; private set; }

    public static IReadOnlyList<AnnotatedExtensionCapabilityProvider.DiscoveredTool> ExtensionTools { get; private set; } = [];

    public static void Initialize()
    {
        if (Registry != null)
            return;

        var journal = new OperationJournal();
        var logJournal = new LogJournal();
        var registry = new CapabilityRegistry(journal);
        registry.RegisterProvider(new BuiltInCapabilityModuleProvider(
            providerId: "rimbridge.core/diagnostics",
            category: "diagnostics",
            module: new DiagnosticsCapabilityModule(journal, logJournal),
            aliasMetadataType: typeof(RimBridgeTools),
            source: CapabilitySourceKind.Core));
        registry.RegisterProvider(new BuiltInCapabilityModuleProvider(
            providerId: "rimbridge.core/lifecycle",
            category: "lifecycle",
            module: new LifecycleCapabilityModule(),
            aliasMetadataType: typeof(RimBridgeTools),
            source: CapabilitySourceKind.Core));
        var extensionProviders = RimBridgeExtensionDiscovery.DiscoverProviders();
        var extensionTools = new List<AnnotatedExtensionCapabilityProvider.DiscoveredTool>();

        foreach (var provider in extensionProviders)
        {
            try
            {
                registry.RegisterProvider(provider);
                extensionTools.AddRange(provider.Tools);
                RimBridgeExtensionDiscovery.MarkProviderRegistered(provider.ProviderId, provider.Tools.Count);
            }
            catch (Exception ex)
            {
                RimBridgeExtensionDiscovery.MarkProviderRegistrationFailed(provider.ProviderId, ex);
                Verse.Log.Error($"[RimBridge] Failed to register annotated extension provider '{provider.ProviderId}': {ex}");
            }
        }

        Journal = journal;
        LogJournal = logJournal;
        ExtensionTools = extensionTools;
        Registry = registry;
        LegacyToolExecution.Initialize(registry);
    }
}
