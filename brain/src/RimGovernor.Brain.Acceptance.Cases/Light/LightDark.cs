using System.Globalization;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>light/dark</c>: the rooms/lighting Need vertical on the lab. An enclosed roofed room
/// staged on the blank lab holds a fuelled stove whose interaction cell native measures dark, with no lamp in reach and
/// wood outside. Under the served brain the colonists build one lamp, and the measured glow at the cell reads lit; an
/// independent native read after the service lets go confirms exactly one lit TorchLamp (the colony has no power source)
/// on a free interior cell within the placement radius.
/// </summary>
internal static class LightDark
{
    // The 10x9 clearing: the room at (0..7, 2..6) with its door at (0, 3) and interior (1..6, 3..5); the FueledStove
    // (3x1, north-facing) centred on (4, 5) against the north wall with its interaction cell at (4, 4); wood on row 0.
    private static readonly Fx.StageCell WorkCell = new(4, 4);
    private static readonly Fx.StageRect Interior = new(1, 3, 6, 3);

    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 10,
            Height = 9,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unzoned, Fx.StageGround.Unroofed, Fx.StageGround.Unfogged],
        },
        Cells = [new Fx.StageCells { Rect = new(0, 0, 10, 9), Terrain = "Concrete", Home = true }],
        Rooms = [new Fx.StageRoom { Name = "work", Rect = new(0, 2, 8, 5), Stuff = "WoodLog", Doors = [new(0, 3)], Roof = true }],
        Buildings = [new Fx.StageBuilding { Name = "stove", Def = "FueledStove", Cells = [new(4, 5)], Fuel = 50 }],
        Things = [new Fx.StageThing { Name = "wood", Def = "WoodLog", Count = 75, Cell = new(2, 0) }],
        Pawns =
        [
            new Fx.StagePawn { Name = "a", Colonist = 0, Work = new Dictionary<string, int> { ["Construction"] = 1 } },
            new Fx.StagePawn { Name = "b", Colonist = 1, Work = new Dictionary<string, int> { ["Construction"] = 1 } },
            new Fx.StagePawn { Name = "c", Colonist = 2, Work = new Dictionary<string, int> { ["Construction"] = 1 } },
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "light/dark",
        Scope = "Native rooms/lighting Need vertical on the lab: under the served brain a measured-dark stove interaction cell "
            + "in an enclosed roofed room gets one affordable lamp built beside it until the measured glow reads lit, and an "
            + "independent native read finds exactly one lit TorchLamp on a free interior cell within the placement radius. "
            + "A unit test cannot measure the native glow grid or see the build.",

        // The lamp is admitted and built within a few thousand ticks on a healthy run.
        Ticks = 20000,
        Tier = CaseTier.Smoke,
        Stage = Spec,

        // "work" rides along because every building method's builder check requires the colony's work priorities to match
        // the controller's own assignment, which only the work family applies.
        Serve = new ServeSpec { Prefix = "light-accept", Families = ["lighting", "work"] },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("light/dark: the stage found no site");
        var stoveId = staged.Buildings["stove"].Ids.Single();
        var cell = (X: site.X + WorkCell.X, Z: site.Z + WorkCell.Z);
        var lighting = LightingPolicy.Default();

        // Before: the stove's work cell roofed and dark (the facts the review latches on), no cave plant, and no lamp.
        var before = await ReadAsync(s, "lighting-before", cancellationToken).ConfigureAwait(false);
        s.RecordWire("lighting_before", before);
        var bench = Bench(before, stoveId);
        if (bench.Roofed != true || !(bench.Glow < lighting.LitGlow) || bench.Cell is not { } at || at.X != cell.X || at.Z != cell.Z || bench.PlantDefs.Count != 0)
        {
            throw new AcceptanceException($"lighting-before: the staged stove is not a roofed dark work cell at {cell}: {bench}");
        }

        if (before.Lamps.Count != 0)
        {
            throw new AcceptanceException($"lighting-before: {before.Lamps.Count} lamps present before the brain acts");
        }

        _ = await s.ServeUntilAsync("light-dark", s.TicksLeft, async ct =>
        {
            var now = await ReadAsync(s, "lighting-release", ct).ConfigureAwait(false);
            var glow = Bench(now, stoveId).Glow ?? 0;
            return (Wait.Signature(glow, now.Lamps.Count), glow >= lighting.LitGlow);
        }, cancellationToken).ConfigureAwait(false);

        // The independent read once the service has let go: exactly the one lamp, lit, standing where a lamp may go.
        var after = await ReadAsync(s, "lighting-after", cancellationToken).ConfigureAwait(false);
        s.RecordWire("lighting_after", after);
        var glow = Bench(after, stoveId).Glow ?? 0;
        if (glow < lighting.LitGlow)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"lighting-after: stove cell glow {glow:F2} is under {lighting.LitGlow:F2}"));
        }

        if (after.Lamps.ToList() is not [{ Lit: true, Building.Id: { } lampId }])
        {
            throw new AcceptanceException($"lighting-after: want exactly one lit lamp, got {string.Join("; ", after.Lamps)}");
        }

        var rows = await s.BuildingsAsync("lamp", new Observations.ListBuildingsRequest { Ids = [lampId] }, cancellationToken).ConfigureAwait(false);
        if (rows is not [{ Building: { DefName: var def, Position: { X: { } x, Z: { } z } } }])
        {
            throw new AcceptanceException($"lamp {lampId}: want one placed building, got {string.Join("; ", rows)}");
        }

        s.Record("lamp", ServedOps.Args(("def", def), ("x", x), ("z", z)));
        if (def != "TorchLamp")
        {
            throw new AcceptanceException($"lamp {lampId} is a {def}; a colony without a power source must build the TorchLamp");
        }

        var inside = x >= site.X + Interior.X && x < site.X + Interior.X + Interior.Width && z >= site.Z + Interior.Z && z < site.Z + Interior.Z + Interior.Height;
        if ((x, z) == cell || !inside || Math.Max(Math.Abs(x - cell.X), Math.Abs(z - cell.Z)) > lighting.PlacementRadius)
        {
            throw new AcceptanceException($"lamp at ({x}, {z}): not a free interior cell within {lighting.PlacementRadius} of the work cell {cell}");
        }
    }

    private static Observations.WorkLightCell Bench(Observations.LightingFacts facts, string stoveId) =>
        facts.WorkCells.FirstOrDefault(c => c.Bench?.Id == stoveId) ?? throw new AcceptanceException($"the lighting census lists no work cell for the stove {stoveId}");

    private static async Task<Observations.LightingFacts> ReadAsync(TypedSession s, string label, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope, Planning = false }, cancellationToken).ConfigureAwait(false);
        var facts = TypedReads.Observed(label, NativeOps.ReadColonyFacts, reply, reply.Outcome?.Observed);
        return facts.Upkeep?.Outcome?.Lighting?.Outcome ?? throw new AcceptanceException($"{label}: the lighting facts are unavailable");
    }
}
