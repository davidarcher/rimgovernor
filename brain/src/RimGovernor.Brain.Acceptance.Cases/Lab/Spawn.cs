using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>lab/spawn</c> (Go <c>spawn.go</c>): what the fixture layer stages on the lab, the production reads see.</summary>
internal static class LabSpawn
{
    // A wall and an unowned muffalo on a clear 6x6 site beside the lab centre.
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 6,
            Height = 6,
            OffsetX = 4,
            Radius = 10,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Heavy],
        },
        Buildings = [new Fx.StageBuilding { Name = "wall", Def = "Wall", Stuff = "WoodLog", Cells = [new(0, 4)] }],
        Pawns = [new Fx.StagePawn { Name = "muffalo", Kind = "Muffalo", Faction = Fx.StageFaction.None, Cell = new(4, 0) }],
    };

    internal static TypedCase Case() => new()
    {
        Name = "lab/spawn",
        Scope = "Lab runner: a wall and an unowned muffalo staged by test/stage on the blank lab read back through the typed "
            + "observations_list_buildings and observations_list_pawns.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 2 },
        Stage = Spec,
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var wall = staged.Buildings["wall"].Ids.Single();
        var animal = staged.Pawns["muffalo"].Id;
        var walls = await s.BuildingsAsync("wall", new Observations.ListBuildingsRequest { Ids = [wall] }, cancellationToken).ConfigureAwait(false);
        if (walls is not [{ Stuff: "WoodLog" } row])
        {
            throw new AcceptanceException($"list_buildings {wall}: want one WoodLog wall, got {walls.Count} rows: {string.Join("; ", walls)}");
        }

        var pawns = await s.CallAsync(
            "animal",
            NativeOps.ListPawns,
            new Observations.ListPawnsRequest { Scope = s.Scope, Filter = new() { Ids = [animal] } },
            cancellationToken).ConfigureAwait(false);
        var observed = TypedReads.Observed("animal", NativeOps.ListPawns, pawns, pawns.Outcome?.Observed);
        var rows = observed.Pawns.ToList();
        if (rows is not [{ KindDefName: "Muffalo" } pawn] || pawn.Pawn.Id != animal)
        {
            throw new AcceptanceException($"list_pawns {animal}: want the staged Muffalo, got {string.Join("; ", rows)}");
        }

        s.RecordWire("wall", row);
        s.RecordWire("animal", pawn);
    }
}
