using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>power/fuel</c>: the native side of the power/basic refuel hold. A staged wood-fired generator spawns empty and
/// reads out of fuel with its stove unpowered; ordinary colonist hauling refuels it from loose wood, and the native
/// building read shows the generator fuelled and the stove powered. The planner's hold (an out-of-fuel producer with
/// enough capacity waits for refuelling instead of building) is <c>PowerMethodTests</c>.
/// </summary>
internal static class PowerFuel
{
    private const string Generator = "generator";
    private const string Stove = "stove";

    internal static TypedCase Case() => new()
    {
        Name = "power/fuel",
        Scope = "A staged empty WoodFiredGenerator wired to an ElectricStove reads out of fuel, a producer, and the stove "
            + "unpowered; a colonist on hauling refuels it from staged wood, and observations_list_buildings then reads the "
            + "generator fuelled and the stove powered. The planner's refuel hold is PowerMethodTests.",
        Ticks = 10000,
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 14,
                Height = 8,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 4,
                Radius = 40,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged],
            },
            Cells = [new Fx.StageCells { Rect = new Fx.StageRect(0, 0, 14, 8), Terrain = "Concrete" }],
            Buildings =
            [
                new Fx.StageBuilding { Name = Generator, Def = "WoodFiredGenerator", Cells = [new(2, 3)] },
                new Fx.StageBuilding { Name = "conduit", Def = "HiddenConduit", Cells = [.. Enumerable.Range(1, 11).Select(x => new Fx.StageCell(x, 1))] },
                new Fx.StageBuilding { Name = Stove, Def = "ElectricStove", Cells = [new(8, 2)] },
            ],
            Things = [new Fx.StageThing { Name = "wood", Def = "WoodLog", Count = 150, Cell = new(12, 5), Near = true }],
            Pawns =
            [
                new Fx.StagePawn
                {
                    Name = "hauler",
                    Colonist = 0,
                    Cell = new(6, 5),
                    Work = new Dictionary<string, int> { ["Hauling"] = 1 },
                    Timetable = Fx.StageTimeAssignment.Work,
                },
            ],
        },
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var generatorId = s.Staged!.Buildings[Generator].Ids.Single();
        var stoveId = s.Staged.Buildings[Stove].Ids.Single();

        async Task<(Observations.BuildingServiceState Generator, Observations.BuildingServiceState Stove)> ReadAsync(string label, CancellationToken ct)
        {
            var rows = await s.BuildingsAsync(label, new Observations.ListBuildingsRequest { Ids = [generatorId, stoveId] }, ct).ConfigureAwait(false);
            Observations.BuildingServiceState Service(string id) => rows.FirstOrDefault(r => r.Building?.Id == id)?.Service
                ?? throw new AcceptanceException($"{label}: no service row for {id}");
            return (Service(generatorId), Service(stoveId));
        }

        static bool OutOfFuel(Observations.BuildingServiceState s) => s.Fuel is Observations.BuildingFuel.Refuelable { OutOfFuel: true };
        static bool PowerOn(Observations.BuildingServiceState s) => s.Power is Observations.BuildingPower.Trader { PowerOn: true };

        var before = await ReadAsync("before", cancellationToken).ConfigureAwait(false);
        s.RecordWire("generator_before", before.Generator);
        s.RecordWire("stove_before", before.Stove);
        if (!OutOfFuel(before.Generator) || before.Generator.Power is not Observations.BuildingPower.Trader { Producer: true } || PowerOn(before.Stove))
        {
            throw new AcceptanceException($"before: want an out-of-fuel producer and an unpowered stove, got {before.Generator} / {before.Stove}");
        }

        var after = before;
        _ = await s.RunUntilAsync("refuelled", 10000, async ct =>
        {
            after = await ReadAsync("after", ct).ConfigureAwait(false);
            return (Wait.Signature(after.Generator.Fuel, PowerOn(after.Stove)), after.Generator.Fuel is Observations.BuildingFuel.Refuelable { OutOfFuel: false } && PowerOn(after.Stove));
        }, cancellationToken).ConfigureAwait(false);
        s.RecordWire("generator_after", after.Generator);
        s.RecordWire("stove_after", after.Stove);
    }
}
