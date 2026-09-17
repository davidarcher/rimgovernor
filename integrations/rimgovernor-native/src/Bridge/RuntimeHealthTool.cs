#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Read-only native runtime health: every required authority hook by
    /// name with whether it is in place and why not, the typed clock hooks,
    /// and the current game's authority snapshot. Reads never install a hook;
    /// installation is retried by the next admission or update poll. A caller
    /// uses this to tell "retry" (a hook missing, generation still advancing)
    /// from "restart required" (a hook whose target cannot be resolved at all).
    /// </summary>
    public sealed class RuntimeHealthTool
    {
        [Tool("home/runtime_health", Description = "Read-only native runtime health: required authority hooks by name, typed clock hooks and the current authority snapshot. Never installs anything.")]
        public async Task<object> Read(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() =>
            {
                var hooks = NativeAuthorityHooks.Statuses;
                var health = NativeAuthorityHooks.Health;
                object? authority = null;
                var game = Current.Game;
                NativeControlAuthority? state;
                if (game != null && NativeControlAuthority.TryGetForGame(game, out state) && state != null)
                {
                    var snapshot = state.Status();
                    authority = new { initialized = true, available = snapshot.Available, active = snapshot.Active,
                        generation = snapshot.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        reason = snapshot.Reason.ToString() };
                }
                else authority = new { initialized = false };
                return new
                {
                    success = true,
                    ready = health.Ready,
                    verified = health.VerifiedTargets,
                    required = health.RequiredTargets,
                    detail = health.Detail,
                    hooks = hooks.Select(h => new { name = h.Name, installed = h.Installed, resolved = h.Method != null, error = h.Error }).ToArray(),
                    // Installed lazily by the first clock epoch; false before any is fine.
                    clockHooksInstalled = Supervisor.TypedHooksReady(),
                    authority
                };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
