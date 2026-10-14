using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Host.Core;
using RimGovernor.Host.Core.Tools;
using RimGovernor.Host.Contracts;

namespace RimGovernor.Host;

internal static class RimBridgeCapabilities
{
    public static CapabilityRegistry Registry { get; private set; }

    public static OperationJournal Journal { get; private set; }

    public static LogJournal LogJournal { get; private set; }

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

        foreach (var provider in extensionProviders)
        {
            try
            {
                registry.RegisterProvider(provider);
            }
            catch (Exception ex)
            {
                Verse.Log.Error($"[RimBridge] Failed to register annotated extension provider '{provider.ProviderId}': {ex}");
            }
        }

        Journal = journal;
        LogJournal = logJournal;
        Registry = registry;
        LegacyToolExecution.Initialize(registry);
        RimGovernor.Host.Sdk.RimBridgeToolAccess.Current = new RegistryToolInvoker(registry);
    }

    /// <summary>
    /// The registry as the mod's HTTP control channel calls it: any tool by name with named arguments, run in Wait
    /// mode as a wire call is. This is what makes <c>test/*</c> fixture ops and the host's own tools
    /// (<c>rimgovernor/set_time_speed</c>) routable, not just the ProtoJSON tools.
    /// </summary>
    private sealed class RegistryToolInvoker(CapabilityRegistry registry) : RimGovernor.Host.Sdk.IRimBridgeToolInvoker
    {
        // The extension-tool timeout argument (AnnotatedExtensionCapabilityProvider); other providers ignore it.
        private const string TimeoutArgument = "_rimBridgeTimeoutMs";

        public IReadOnlyCollection<string> ToolNames => registry.GetCapabilities()
            .SelectMany(descriptor => descriptor.Aliases)
            .Where(ToolNameValidator.IsCanonical)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        public async System.Threading.Tasks.Task<object> InvokeAsync(
            string tool,
            IDictionary<string, object> arguments,
            TimeSpan timeout,
            System.Threading.CancellationToken cancellationToken)
        {
            var named = new Dictionary<string, object>(arguments, StringComparer.Ordinal)
            {
                [TimeoutArgument] = (int)Math.Min(timeout.TotalMilliseconds, int.MaxValue),
            };
            // A tool call is activity: the idle-exit harness feature must not count a busy bridge as idle.
            IdleExit.Begin();
            OperationEnvelope envelope;
            try
            {
                envelope = await registry.InvokeAsync(tool, named, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                IdleExit.End();
            }
            if (!envelope.Success)
            {
                var error = envelope.Error;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error?.Message)
                    ? $"{tool} {envelope.Status}"
                    : error.Message);
            }

            return envelope.Result;
        }
    }
}
