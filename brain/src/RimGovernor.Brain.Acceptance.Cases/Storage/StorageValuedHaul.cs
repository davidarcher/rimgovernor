using System.Globalization;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>storage/valued-haul</c> (#3624): under the served brain idle colonists with nothing else due carry a cloth stack
/// lying outside into a roofed storeroom, since cloth deteriorates in the open, and a steel stack the injected plan's
/// planned room will use, since storage lies on the way to the site. Hauling is valued, never for its own sake: the native
/// carry, the storage cell native chose and the live deterioration read are what a unit test cannot drive.
/// </summary>
internal static class StorageValuedHaul
{
    private const int Budget = 4000;

    // A 30x7 site: the planned room at its west end, the 5x5 storeroom (walls, a door each side, roof) a lane east of it
    // with a stockpile on its 3x3 interior, and the cloth, the steel and three workers 18 cells further east, so storage
    // lies on the walk from the stacks to the site. Steel saves at most its distance to storage, so it is worth carrying
    // only for a colonist standing nearer it than that (#3642): the workers stand by the stacks, since a worker back from
    // storing the cloth stands as far from the steel as storage does. The third covers the colonist the first review's
    // ideology plan makes a specialist: that role disables hauling after the haul was chosen, and the next review gives
    // the stack to a colonist still beside it.
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        World = new Fx.StageWorld { Hour = 8 },
        Site = new Fx.StageSite
        {
            Width = 30,
            Height = 7,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 3,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Heavy],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 30, 7), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones, Fx.StageClearKind.Roofs] }],
        Rooms = [new Fx.StageRoom { Name = "store", Rect = new Fx.StageRect(6, 1, 5, 5), Stuff = "WoodLog", Doors = [new(6, 3), new(10, 3)], Roof = true }],
        Zones =
        [
            new Fx.StageZone
            {
                Name = "store", Kind = Fx.StageZoneKind.Stockpile, Priority = Fx.StageStoragePriority.Important, Allow = ["Cloth", "Steel"],
                Cells = [.. from z in Enumerable.Range(2, 3) from x in Enumerable.Range(7, 3) select new Fx.StageCell(x, z)],
            },
        ],
        Things =
        [
            new Fx.StageThing { Name = "cloth", Def = "Cloth", Count = 75, Cell = new(28, 1) },
            new Fx.StageThing { Name = "steel", Def = "Steel", Count = 75, Cell = new(28, 5) },
        ],
        Pawns =
        [
            new Fx.StagePawn { Name = "worker", Colonist = 0, Cell = new(26, 2) },
            new Fx.StagePawn { Name = "helper", Colonist = 1, Cell = new(26, 4) },
            new Fx.StagePawn { Name = "spare", Colonist = 2, Cell = new(26, 3) },
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "storage/valued-haul",
        Scope = "Issues #3624, #3642: under the served brain idle colonists carry a cloth stack lying outside into a roofed storeroom "
            + "stockpile (cloth deteriorates in the open) and a steel stack the injected plan's planned room will use (storage "
            + "lies on the way to the site) within the budget. A unit test cannot drive the native carry, native's storage cell or "
            + "the live deterioration read.",
        Ticks = Budget,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 3 },
        Stage = Spec,
        Ops = [FixtureStage.Tool],
        Keep = ["Food", "Rest", "Joy"],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "valued-haul" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("the haul stage found no site");
        var store = Spec.Zones[0].Cells.Select(c => new Cell(site.X + c.X, site.Z + c.Z)).ToHashSet();
        var stacks = new Dictionary<string, string> { ["cloth"] = staged.Things["cloth"].Ids.Single(), ["steel"] = staged.Things["steel"].Ids.Single() };

        // The planned room west of the storeroom: a 3x3 interior (its ring one lane west of the storeroom's), its door east.
        await TypedLayoutPlan.InjectAsync(s, new LayoutPlan
        {
            NextRoomId = 2,
            Rooms =
            [
                new PlannedRoom
                {
                    Id = 1,
                    Role = PlannedRole.Bedroom,
                    Region = RegionRules.RectRegion(new(site.X + 1, site.Z + 2, 3, 3)),
                    Door = new(site.X + 4, site.Z + 3),
                    DoorRot = Rotation.East,
                },
            ],
        }, cancellationToken).ConfigureAwait(false);

        var at = new Dictionary<string, Cell?>();
        _ = await s.ServeUntilAsync("hauled", s.TicksLeft, async ct =>
        {
            foreach (var (name, id) in stacks) at[name] = await CellOfAsync(s, id, ct).ConfigureAwait(false);
            var signature = string.Join(' ', at.Select(a => $"{a.Key}={(a.Value is { } c ? string.Create(CultureInfo.InvariantCulture, $"{c.X},{c.Z}") : "gone")}"));
            return (signature, at.Values.All(c => c is { } cell && store.Contains(cell)));
        }, cancellationToken).ConfigureAwait(false);
        foreach (var (name, cell) in at) s.Record(name, cell?.ToString() ?? "gone");
        var left = at.Where(a => a.Value is not { } cell || !store.Contains(cell)).Select(a => $"{a.Key} at {a.Value?.ToString() ?? "no cell"}").ToList();
        if (left.Count > 0)
        {
            throw new AcceptanceException($"the idle colonists left {string.Join(", ", left)} outside the storeroom stockpile after {Budget} ticks");
        }
    }

    // The staged stack's cell, or null when it is gone (merged, carried or used).
    private static async Task<Cell?> CellOfAsync(TypedSession s, string stack, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync("stock", NativeOps.ListSupplies,
            new Observations.ListSuppliesRequest { Scope = s.Scope, Filter = new Observations.StockFilter { DefNames = ["Cloth", "Steel"], Ownership = Observations.StockOwnership.All } },
            cancellationToken).ConfigureAwait(false);
        var supplies = TypedReads.Observed("stock", NativeOps.ListSupplies, reply, reply.Outcome?.Observed);
        return supplies.Stocks.SelectMany(st => st.Items).FirstOrDefault(i => i.Item.Id == stack) is { } item ? new Cell(item.Cell.X, item.Cell.Z) : null;
    }
}
