using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>control/pawn-activity-haul-designated</c> (#3614): a Hands Deliver step on a slate chunk the brain designated for
/// hauling (the plain Haul designation the room reconcile's HaulOut stands; a Haul takes no guard) to a stockpile cell
/// beside it. Under voluntary control the designation gets no worker by itself; the controlled colonist carries the
/// stack to the stockpile. The native carry and drop are what a unit test cannot drive.
/// </summary>
internal static class ControlHaul
{
    private const string TaskId = "haul/stack";
    private const string Def = "ChunkSlate";
    private const int Count = 1;
    private const int Budget = 6000;

    // A 5x3 clear site beside the colonist with the chunk at its middle cell and a one-cell stockpile at its right edge.
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 5,
            Height = 3,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 3,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 5, 3), Kinds = [Fx.StageClearKind.Things] }],
        Things = [new Fx.StageThing { Name = "stack", Def = Def, Count = Count, Cell = new(2, 1) }],
        Zones = [new Fx.StageZone { Name = "store", Kind = Fx.StageZoneKind.Stockpile, Cells = [new(4, 1)], Priority = Fx.StageStoragePriority.Important, Allow = [Def] }],
        Pawns = [new Fx.StagePawn { Name = "worker", Colonist = 0 }],
    };

    internal static TypedCase Case() => new()
    {
        Name = "control/pawn-activity-haul-designated",
        Scope = "A Hands Deliver step on a slate chunk (what Designator_Haul accepts: a designateHaulable thing) the brain designated for hauling (the plain Haul designation the room reconcile's "
            + "HaulOut stands) to a stockpile cell under voluntary control, which otherwise gives the designation no worker: "
            + "operations_apply admits the order and the controlled colonist moves the chunk to the stockpile within the budget. "
            + "A unit test cannot drive the native carry and drop; no read lists the designation, so its clearing is not asserted.",
        Ticks = Budget,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Stage = Spec,
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var worker = staged.Pawns["worker"].Id;
        var stack = staged.Things["stack"].Ids.Single();
        var origin = staged.Things["stack"].Cells.Single();
        var store = Spec.Zones[0].Cells[0];
        var site = staged.Site ?? throw new AcceptanceException("the haul stage found no site");
        var destination = new Cell(site.X + store.X, site.Z + store.Z);
        var generation = await TypedAuthority.GrantAutoGenerationAsync(s, cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "designate", new Operations.DesignateIntent { Designation = Operations.ThingDesignation.Haul, Target = new() { Id = stack } }, cancellationToken).ConfigureAwait(false);

        var step = new PawnActivityStep(PawnActivityKind.Deliver, stack, "", "", Count, int.MaxValue) { Destination = destination };
        var batch = new PawnActivities(generation, [new PawnActivityOrder(worker, TaskId, TaskId, 0, 1, false, [step]) { Task = TaskId }])
        {
            Tasks = [new SelectedActivityTask(TaskId, worker, [], step, new Cell(origin.X, origin.Z), 0)],
        };
        _ = await TypedApply.AppliedAsync(s, "order", new Operations.PawnActivitiesIntent { Batch = batch }, cancellationToken).ConfigureAwait(false);

        Observations.StockItem? item = null;
        _ = await s.RunUntilAsync(
            "haul",
            Budget,
            async ct =>
            {
                item = await StackAsync(s, stack, ct).ConfigureAwait(false);
                var at = item is null ? "gone" : string.Create(CultureInfo.InvariantCulture, $"{item.Cell.X},{item.Cell.Z}");
                return ($"stack={at}", item is not null && item.Cell.X == destination.X && item.Cell.Z == destination.Z);
            },
            cancellationToken).ConfigureAwait(false);
        s.Record("stack", stack);
        if (item is null || item.Cell.X != destination.X || item.Cell.Z != destination.Z)
        {
            throw new AcceptanceException($"the ordered colonist {worker} left the stack {stack} at {(item is null ? "no cell" : item.Cell.ToString())} instead of the stockpile cell {destination} after {Budget} ticks");
        }
    }

    // The staged stack among the colony's steel, or null when it is gone (merged or destroyed).
    private static async Task<Observations.StockItem?> StackAsync(TypedSession s, string stack, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync("stock", NativeOps.ListSupplies,
            new Observations.ListSuppliesRequest { Scope = s.Scope, Filter = new Observations.StockFilter { DefNames = [Def], Ownership = Observations.StockOwnership.All } },
            cancellationToken).ConfigureAwait(false);
        var supplies = TypedReads.Observed("stock", NativeOps.ListSupplies, reply, reply.Outcome?.Observed);
        return supplies.Stocks.SelectMany(st => st.Items).FirstOrDefault(i => i.Item.Id == stack);
    }
}
