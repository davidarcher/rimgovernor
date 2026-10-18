#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Fx = RimGovernor.Fixtures.Contracts;

namespace HomeBridge.BridgeTools
{
    // needs/recreation-shared-seat (#3570): one colonist starts the giver's own play job on the staged multi-seat building,
    // so it holds the building's reservation at the giver job's joyMaxParticipants, as a game in progress does. The stage
    // primitives cannot start a job.
    public sealed class RecreationSeatFixture
    {
        [Tool("test/recreation_hold_seat", Description = "UNSAFE FOR MODEL EXECUTION. Start the staged holder's own play job on the staged recreation building, holding one of its seats.")]
        public async Task<object> HoldSeat(IRimBridgeContext ctx, CancellationToken ct,
            [ToolParameter(Description = "The holder's load id.")] string holder = "",
            [ToolParameter(Description = "The recreation building's load id.")] string building = "") => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = FixtureKit.PausedMap();
            var pawn = FixtureKit.Spawned<Pawn>(map, holder);
            var thing = FixtureKit.Spawned<Thing>(map, building);
            var giver = NativeRecreation.GiverFor(thing) ?? throw new InvalidOperationException($"{thing.def.defName} has no interact-building giver.");
            if (giver.jobDef.joyMaxParticipants < 2) throw new InvalidOperationException($"{giver.jobDef.defName} seats one pawn; the case needs a shared building.");
            var job = NativeRecreation.PlayJob(pawn, thing, giver) ?? throw new InvalidOperationException("The giver offers the holder no play job.");
            pawn.jobs.StartJob(job, JobCondition.InterruptForced);
            if (pawn.CurJob != job || !map.reservationManager.ReservationsReadOnly.Any(r => r.Claimant == pawn && r.Target.Thing == thing))
                throw new InvalidOperationException("The holder's play job did not reserve the building.");
            return FixtureKit.Reply(new Fx.FixtureDone { Success = true });
        }, ct).ConfigureAwait(false);
    }
}
