#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using Verse;
using RimGovernor.Host.Sdk;
using RimGovernor.Brain.Domain;
using Fx = RimGovernor.Fixtures.Contracts;

namespace HomeBridge.BridgeTools
{
    // production/sow-block (#3528): the staged sower and empty growing zone bound by load id; one exact Sow step over a
    // cell count smaller than the zone installed straight into the native execution table.
    public sealed partial class PawnControlFixture
    {
        private static string sowZone = "";

        [Tool("test/pawn_sow_prepare", Description="UNSAFE FOR MODEL EXECUTION. Bind the staged sower and growing zone; stand every colonist still.")]
        public async Task<object> PrepareSow(IRimBridgeContext ctx, CancellationToken ct,
            [ToolParameter(Description="The sower's load id.")] string sower = "",
            [ToolParameter(Description="The growing zone's load id.")] string zone = "") => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = FixtureKit.PausedMap();
            worker = FixtureKit.Spawned<Pawn>(map, sower);
            if (NativeSowingActivities.Grower(map, zone) == null) throw new ArgumentException($"{zone} is not a growing zone.");
            sowZone = zone;
            Still(map);
            deadline = Find.TickManager.TicksGame + 12000;
            return FixtureKit.Reply(new Fx.FixtureDone { Success = true });
        }, ct).ConfigureAwait(false);

        [Tool("test/pawn_sow_install", Description="UNSAFE FOR MODEL EXECUTION. Install one exact Sow step over the first observed sowable cells of the prepared zone.")]
        public async Task<object> InstallSow(IRimBridgeContext ctx, CancellationToken ct,
            [ToolParameter(Description="The cells to sow.")] int count = 1) => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var row = NativeSowingActivities.Read(worker!.Map).Workers.SingleOrDefault(w => w.Pawn == worker.GetUniqueLoadID() && w.Zone == sowZone)
                ?? throw new InvalidOperationException("No native sowable cells observed for the prepared sower and zone.");
            if (row.Cells.Count <= count) throw new InvalidOperationException($"The zone offers {row.Cells.Count} cells; the case needs more than {count}.");
            var step = new PawnActivityStep(PawnActivityKind.Work, sowZone, row.WorkGiver, "Sow", count, deadline) { Destination = row.Cells[0] };
            var batch = new PawnActivities(Context().NativeGeneration!.Value,
                new[] { new PawnActivityOrder(worker.GetUniqueLoadID(), "sow-block", "sow-method", 0, 1, false, new[] { step }) });
            var invalid = NativePawnActivities.Validate(batch, Context());
            if (invalid != null) throw new InvalidOperationException(invalid.Detail);
            NativePawnActivities.Apply(batch, Context());
            StandbyColonists.Apply();
            return FixtureKit.Reply(new Fx.FixtureDone { Success = true });
        }, ct).ConfigureAwait(false);

        [Tool("test/pawn_sow_read", Description="UNSAFE FOR MODEL EXECUTION. Read the sower's execution and the plants of the prepared zone's crop actually standing in it.")]
        public async Task<object> ReadSow(IRimBridgeContext ctx, CancellationToken ct) => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var grower = NativeSowingActivities.Grower(worker!.Map, sowZone)!;
            var crop = grower.GetPlantDefToGrow();
            var sown = ((Zone_Growing)grower).Cells.Count(c => c.GetThingList(worker.Map).Any(t => t.def == crop));
            return FixtureKit.Reply(new Fx.SowRead { Execution = Execution(NativePawnActivities.Read(worker)), Sown = sown, Forced = worker.CurJob?.playerForced == true });
        }, ct).ConfigureAwait(false);
    }
}
