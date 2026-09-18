using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable fixture only: injects the two runtime faults the lifecycle/
    // runtime-fault case recovers from. Unpatch removes one required authority
    // invalidation hook from the live game, exactly as a partial startup
    // install or a conflicting mod's unpatch would leave it. CorruptRow
    // damages one retained clock journal file, as a disk fault or a tool
    // editing the profile would. Never compiled into production builds (see
    // RimGovernor.Bridge.csproj's RuntimeFaultFixture-gated Compile entry).
    // Nothing here repairs: recovery is the production path under test
    // (NativeAuthorityHooks.Install from the next admission or poll; the
    // journal reading the damaged row as loss and appending past it).
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

        [Tool("test/runtime_fault_corrupt_row", Description = "Disposable fixture: truncates the retained clock journal file of one published row (1..newestCursor) so it no longer decodes. Recovery is production behavior, not this fixture's.")]
        public async Task<object> CorruptRow(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Cursor of a published row, from clock_read_events newestCursor or home/runtime_health journal.newestCursor.")] long cursor)
        {
            return await ctx.MainThread.InvokeAsync<object>(() =>
            {
                if (Find.CurrentMap == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required");
                var path = Supervisor.RetainedJournalRowPath(cursor);
                if (!File.Exists(path)) throw new InvalidOperationException("Retained row " + cursor + " has no file to damage");
                var intact = new FileInfo(path).Length;
                // Half a row: an XML document cut mid-element, the shape a
                // torn write or a truncating tool leaves behind.
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                    stream.SetLength(Math.Max(1, intact / 2));
                return new { success = true, cursor, intactBytes = intact, damagedBytes = new FileInfo(path).Length };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
