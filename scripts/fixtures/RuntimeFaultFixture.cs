using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable fixture only: removes one required authority invalidation
    // hook from the live game, exactly as a partial startup install or a
    // conflicting mod's unpatch would leave it. Never compiled into production
    // builds (see RimGovernor.Bridge.csproj's RuntimeFaultFixture-gated Compile
    // entry). Nothing here reinstalls: recovery is the production path under
    // test (NativeAuthorityHooks.Install from the next admission or poll).
    public sealed class RuntimeFaultFixture
    {
        [Tool("test/runtime_fault_unpatch", Description = "Disposable fixture: removes the named required authority hook (see home/runtime_health hooks[].name) from the live game so authority reports HooksUnavailable. Recovery is production behavior, not this fixture's.")]
        public async Task<object> Unpatch(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact hook name from home/runtime_health.")] string hook)
        {
            return await ctx.MainThread.InvokeAsync<object>(() =>
            {
                if (Find.CurrentMap == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required");
                var status = NativeAuthorityHooks.Statuses.FirstOrDefault(h => h.Name == hook);
                if (status == null) throw new ArgumentException("Unknown hook '" + hook + "'");
                if (status.Method == null) throw new InvalidOperationException("Hook target is unresolved; nothing to remove");
                if (!status.Installed) throw new InvalidOperationException("Hook is not installed");
                new Harmony(NativeAuthorityHooks.Owner).Unpatch(status.Method, HarmonyPatchType.All, NativeAuthorityHooks.Owner);
                var removed = !NativeAuthorityHooks.Statuses.First(h => h.Name == hook).Installed;
                if (!removed) throw new InvalidOperationException("Hook is still installed after Unpatch");
                return new { success = true, hook, removed = true, health = NativeAuthorityHooks.Health.VerifiedTargets };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
