using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>control/pawn-activity-haul-out-ordinary</c> (#3625): the room reconcile's HaulOut over an ordinary stack (chemfuel,
/// which Designator_Haul refuses: not designateHaulable) larger than one carry. The plain Haul Designate stands no
/// designation but a haul-out record, the construction activity read lists the stack as a Haul removal, and two Deliver
/// orders of one carry each move it to the stockpile cell, the second landing on the first; the removal is gone after.
/// The native designator, carry capacity, split, merge and record are what a unit test cannot establish.
/// </summary>
internal static class ControlHaulOut
{
    private const string TaskId = "haul/stack";
    private const string Def = "Chemfuel";
    private const int Count = 100;
    private const int Budget = 6000;

    // A 5x3 clear site beside the colonist with the stack at its middle cell and a one-cell stockpile at its right edge.
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
        Name = "control/pawn-activity-haul-out-ordinary",
        Scope = "A plain Haul Designate (the room reconcile's HaulOut) on 100 chemfuel, an ordinary stack Designator_Haul refuses and "
            + "larger than one carry: operations_apply applies it with no designation, the construction activity read reports one Haul "
            + "removal of the stack (Count 100, Capacity under 100, a known delivery to the stockpile cell); with the other colonists held, "
            + "a Deliver of one carry leaves the remainder at its cell under the op, and a second Deliver of the remainder to the same "
            + "cell joins the first carry and clears the op. "
            + "Does not drive the coupled forecast's binding or a planned room's build (unit tests and the nightly cover those).",
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
        var site = staged.Site ?? throw new AcceptanceException("the haul-out stage found no site");
        var destination = new Cell(site.X + store.X, site.Z + store.Z);
        var generation = await TypedAuthority.GrantAutoGenerationAsync(s, cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "designate", new Operations.DesignateIntent { Location = new Operations.DesignationLocation.ByTarget(), Designation = Operations.ThingDesignation.Haul, Target = new() { Id = stack }, ReplaceWithWall = false, Withdraw = false, Guard = Operations.DesignationGuard.NoGuard }, cancellationToken).ConfigureAwait(false);

        var op = await HaulOpAsync(s, stack, cancellationToken).ConfigureAwait(false)
            ?? throw new AcceptanceException($"designated: the read reports no Haul removal of the ordinary stack {stack}");
        var row = op.Options.FirstOrDefault(o => o.Pawn == worker)
            ?? throw new AcceptanceException($"designated: the Haul removal of {stack} has no row for the worker {worker}: {op}");
        s.Record("capacity", row.Capacity.ToString(CultureInfo.InvariantCulture));
        if (row.Count != Count || row.Capacity <= 0 || row.Capacity >= Count || row.Location != destination || row.Delivery is not Observations.ConstructionDelivery.Carried)
        {
            throw new AcceptanceException($"designated: want the Haul row of {stack} at Count {Count}, Capacity under {Count} and a known delivery to {destination}, got {row}");
        }

        // The other colonists hold, so no vanilla haul takes the remainder between the two carries.
        var hold = new PawnActivityStep(PawnActivityKind.Hold, "", "", "", 0, int.MaxValue);
        var others = op.Options.Select(o => o.Pawn).Where(p => p != worker).Distinct(StringComparer.Ordinal)
            .Select(p => new PawnActivityOrder(p, "hold", "hold", 0, 1, false, [hold])).ToArray();
        if (others.Length > 0)
        {
            _ = await TypedApply.AppliedAsync(s, "hold", new Operations.PawnActivitiesIntent { Batch = new PawnActivities(generation, others) }, cancellationToken).ConfigureAwait(false);
        }

        var first = Math.Min(row.Capacity, Count);
        await DeliverAsync(s, generation, worker, stack, origin, destination, first, 0, 1, cancellationToken).ConfigureAwait(false);
        var left = false;
        Observations.StockItem? at = null;
        _ = await s.RunUntilAsync(
            "first-carry",
            Budget,
            async ct =>
            {
                left = await StackAsync(s, stack, ct).ConfigureAwait(false) is { } item && item.Cell.X == origin.X && item.Cell.Z == origin.Z;
                at = await AtAsync(s, destination, ct).ConfigureAwait(false);
                return ($"left={left} stored={at?.Id ?? "none"}", left && at is not null);
            },
            cancellationToken).ConfigureAwait(false);
        var rest = await HaulOpAsync(s, stack, cancellationToken).ConfigureAwait(false);
        if (!left || at is null || rest is null)
        {
            throw new AcceptanceException($"first carry: want the remainder of {stack} at its cell under its Haul removal and the carry on {destination}, "
                + $"got left={left} stored={at?.Id ?? "none"} op={rest?.ToString() ?? "none"}");
        }

        await DeliverAsync(s, generation, worker, stack, origin, destination, Count - first, 1, 2, cancellationToken).ConfigureAwait(false);
        var cleared = false;
        _ = await s.RunUntilAsync(
            "second-carry",
            s.TicksLeft,
            async ct =>
            {
                cleared = await HaulOpAsync(s, stack, ct).ConfigureAwait(false) is null && await StackAsync(s, stack, ct).ConfigureAwait(false) is null;
                at = await AtAsync(s, destination, ct).ConfigureAwait(false);
                return ($"cleared={cleared} stored={at?.Id ?? "none"}", cleared && at is not null);
            },
            cancellationToken).ConfigureAwait(false);
        if (!cleared || at is null)
        {
            throw new AcceptanceException($"second carry: want the Haul removal of {stack} gone and the chemfuel on {destination}, got cleared={cleared} stored={at?.Id ?? "none"}");
        }
    }

    private static async Task DeliverAsync(TypedSession s, ulong generation, string worker, string stack, Fx.StageCell origin, Cell destination,
        int count, long expected, long revision, CancellationToken cancellationToken)
    {
        var step = new PawnActivityStep(PawnActivityKind.Deliver, stack, "", "", count, int.MaxValue).At(destination);
        var batch = new PawnActivities(generation, [new PawnActivityOrder(worker, TaskId, TaskId, expected, revision, false, [step]) { Task = TaskId }])
        {
            Tasks = [new SelectedActivityTask(TaskId, worker, [], step, new Cell(origin.X, origin.Z), 0)],
        };
        _ = await TypedApply.AppliedAsync(s, "order-" + revision.ToString(CultureInfo.InvariantCulture), new Operations.PawnActivitiesIntent { Batch = batch }, cancellationToken).ConfigureAwait(false);
    }

    // The stack's Haul removal, or null when the read lists no op for it.
    private static async Task<Observations.PlannedSiteClearing?> HaulOpAsync(TypedSession s, string stack, CancellationToken cancellationToken)
    {
        var read = await s.CallAsync("removals", NativeOps.ReadConstructionActivity, new Observations.ConstructionActivityRequest(s.Identity, default), cancellationToken).ConfigureAwait(false);
        return read.Removals.FirstOrDefault(r => r.Id == stack && r.Kind == Observations.PlannedClearingKind.Haul);
    }

    // The staged stack among the colony's chemfuel, or null when it is gone (merged into the stored stack).
    private static async Task<Observations.StockItem?> StackAsync(TypedSession s, string stack, CancellationToken cancellationToken)
        => (await StockAsync(s, cancellationToken).ConfigureAwait(false)).FirstOrDefault(i => i.Id == stack);

    // The colony's chemfuel on the stockpile cell, or null when none is there.
    private static async Task<Observations.StockItem?> AtAsync(TypedSession s, Cell cell, CancellationToken cancellationToken)
        => (await StockAsync(s, cancellationToken).ConfigureAwait(false)).FirstOrDefault(i => i.Cell.X == cell.X && i.Cell.Z == cell.Z);

    private static async Task<IEnumerable<Observations.StockItem>> StockAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var supplies = await s.CallAsync("stock", NativeOps.ListSupplies,
            new Observations.ListSuppliesRequest { Scope = s.Scope, Filter = new Observations.StockFilter { DefNames = [Def], Category = Observations.StockCategory.Haulable, Ownership = Observations.StockOwnership.All, IncludeHeld = true } },
            cancellationToken).ConfigureAwait(false);
        return supplies.Stocks.SelectMany(st => st.Items);
    }
}
