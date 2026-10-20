#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Fx = RimGovernor.Fixtures.Contracts;

namespace HomeBridge.BridgeTools
{
    // control/unassigned-runs-vanilla (#3669): a player's CutPlant designation on a staged tree, which the stage
    // primitives cannot express, and the colonist's job as the native job tracker runs it.
    public sealed class VanillaChopFixture
    {
        [Tool("test/vanilla_chop_designate", Description = "UNSAFE FOR MODEL EXECUTION. Lay a player CutPlant designation on one plant by id. Test builds only.")]
        public async Task<object> Designate(IRimBridgeContext ctx, CancellationToken ct,
            [ToolParameter(Description = "The plant's load id.")] string plant = "") => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded game is required.");
            if (RefIndex.Thing(map, plant) is not Plant tree || !tree.Spawned) throw new ArgumentException("Unknown plant.");
            if (map.designationManager.DesignationOn(tree, DesignationDefOf.CutPlant) == null)
                map.designationManager.AddDesignation(new Designation(tree, DesignationDefOf.CutPlant));
            return FixtureKit.Reply(new Fx.FixtureDone { Success = map.designationManager.DesignationOn(tree, DesignationDefOf.CutPlant) != null });
        }, ct).ConfigureAwait(false);

        [Tool("test/vanilla_chop_read", Description = "UNSAFE FOR MODEL EXECUTION. Read one colonist's current JobDef and whether a Hands execution owns it. Test builds only.")]
        public async Task<object> Read(IRimBridgeContext ctx, CancellationToken ct,
            [ToolParameter(Description = "The colonist's load id.")] string pawn = "") => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded game is required.");
            if (RefIndex.Thing(map, pawn) is not Pawn colonist) throw new ArgumentException("Unknown colonist.");
            return FixtureKit.Reply(new Fx.VanillaJobRead { Job = colonist.CurJob?.def.defName ?? "", Owned = NativePawnActivities.Owned(colonist) });
        }, ct).ConfigureAwait(false);
    }
}
