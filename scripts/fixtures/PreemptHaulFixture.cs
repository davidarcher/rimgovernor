#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using Verse;
using Verse.AI;
using Fx = RimGovernor.Fixtures.Contracts;

namespace HomeBridge.BridgeTools
{
    // control/coupled-preempts-vanilla-holder (#3671): an unowned colonist's own vanilla haul of a staged stack, started
    // paused so the case need not race the holder's walk; the stage primitives cannot start a job.
    public sealed class PreemptHaulFixture
    {
        [Tool("test/preempt_haul_start", Description = "UNSAFE FOR MODEL EXECUTION. Start the holder's vanilla haul-to-storage job on the staged stack, reserving it. Test builds only.")]
        public async Task<object> Start(IRimBridgeContext ctx, CancellationToken ct,
            [ToolParameter(Description = "The holder's load id.")] string holder = "",
            [ToolParameter(Description = "The stack's load id.")] string stack = "") => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = FixtureKit.PausedMap();
            var pawn = FixtureKit.Spawned<Pawn>(map, holder);
            var thing = FixtureKit.Spawned<Thing>(map, stack);
            var job = HaulAIUtility.HaulToStorageJob(pawn, thing, false) ?? throw new InvalidOperationException("Vanilla offers the holder no haul of the stack.");
            pawn.jobs.StartJob(job, JobCondition.InterruptForced);
            if (pawn.CurJob != job || job.playerForced) throw new InvalidOperationException("The holder's vanilla haul did not start.");
            return FixtureKit.Reply(Read(map, pawn, thing));
        }, ct).ConfigureAwait(false);

        [Tool("test/preempt_haul_read", Description = "UNSAFE FOR MODEL EXECUTION. Read the load id of the holder's job and whether it targets and reserves the stack. Test builds only.")]
        public async Task<object> ReadTool(IRimBridgeContext ctx, CancellationToken ct,
            [ToolParameter(Description = "The holder's load id.")] string holder = "",
            [ToolParameter(Description = "The stack's load id.")] string stack = "") => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = FixtureKit.CurrentMap();
            var pawn = FixtureKit.Spawned<Pawn>(map, holder);
            var thing = map.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == stack);
            return FixtureKit.Reply(Read(map, pawn, thing));
        }, ct).ConfigureAwait(false);

        private static Fx.PreemptHaulRead Read(Map map, Pawn pawn, Thing? thing) => new()
        {
            JobId = pawn.CurJob?.loadID,
            Holds = thing != null && pawn.CurJob?.targetA.Thing == thing
                && map.reservationManager.ReservationsReadOnly.Any(r => r.Claimant == pawn && r.Target.Thing == thing),
        };
    }
}
