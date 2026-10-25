using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>animals/containment</c>: a handler ropes an uncontained pen animal into a standing pen, and the pawn read's animal details then
/// shows it contained with a pen id; the colony facts' upkeep census reads a pet as not requiring a pen, with none. The pen
/// is staged finished (the 8-day build the old case played is construction, covered elsewhere).
/// </summary>
internal static class AnimalsContainment
{
    private const string Cow = "cow";
    private const string Pet = "pet";
    private const int Side = 6;

    internal static TypedCase Case() => new()
    {
        Name = "animals/containment",
        Scope = "Over a staged finished 6x6 WoodLog Fence ring with a FenceGate and a PenMarker, a colonist on Handling ropes a "
            + "staged uncontained player Cow into the pen: observations_list_pawns animal details read it in a pen by its id and no "
            + "release or slaughter designation, and observations_read_colony_facts' upkeep animals read the staged Husky "
            + "unroped, with no pen.",
        Ticks = 15000,
        Tier = CaseTier.Nightly,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 14,
                Height = 10,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 4,
                Radius = 40,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Unroofed],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 14, 10), Kinds = [Fx.StageClearKind.Things] }],
            Cells = [new Fx.StageCells { Rect = new Fx.StageRect(0, 0, 14, 10), Terrain = "Soil" }],
            Buildings =
            [
                new Fx.StageBuilding { Name = "fence", Def = "Fence", Stuff = "WoodLog", Cells = Ring() },
                new Fx.StageBuilding { Name = "gate", Def = "FenceGate", Stuff = "WoodLog", Cells = [new(2 + (Side / 2), 2)] },
                new Fx.StageBuilding { Name = "marker", Def = "PenMarker", Cells = [new(3, 3)] },
            ],
            Pawns =
            [
                new Fx.StagePawn
                {
                    Name = "handler",
                    Colonist = 0,
                    Cell = new(10, 1),
                    AllWork = 0,
                    Work = new Dictionary<string, int> { ["Handling"] = 1 },
                    Timetable = Fx.StageTimeAssignment.Work,
                },
                new Fx.StagePawn { Name = "idle-1", Colonist = 1, AllWork = 0 },
                new Fx.StagePawn { Name = "idle-2", Colonist = 2, AllWork = 0 },
                new Fx.StagePawn { Name = Cow, Kind = "Cow", Faction = Fx.StageFaction.Player, Age = 4, Cell = new(11, 6) },
                new Fx.StagePawn { Name = Pet, Kind = "Husky", Faction = Fx.StageFaction.Player, Age = 4, Cell = new(12, 3) },
            ],
        },
        Run = RunAsync,
    };

    // The pen's ring, its south-west corner at (2, 2), less the gate cell at the south side's centre.
    private static List<Fx.StageCell> Ring() =>
        [.. Enumerable.Range(0, Side).SelectMany(x => Enumerable.Range(0, Side).Select(z => (x, z)))
            .Where(c => (c.x is 0 or Side - 1 || c.z is 0 or Side - 1) && c != (Side / 2, 0))
            .Select(c => new Fx.StageCell(2 + c.x, 2 + c.z))];

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var cowId = s.Staged!.Pawns[Cow].Id;
        var petId = s.Staged.Pawns[Pet].Id;

        Observations.AnimalState? cow = null;
        _ = await s.RunUntilAsync("roped-in", s.TicksLeft, async ct =>
        {
            var request = new Observations.ListPawnsRequest { Scope = s.Scope, Filter = new() { Ids = [cowId] } };
            var reply = await s.CallAsync("cow-animal", NativeOps.ListPawns, request, ct).ConfigureAwait(false);
            cow = reply.Pawns.FirstOrDefault(p => p.Pawn?.Id == cowId)?.AnimalState is Observations.PawnAnimalState.Animal animal
                ? animal.State
                : throw new AcceptanceException($"cow-animal: the staged cow {cowId} is not read as an animal");
            return (Wait.Signature(cow.PenId), cow is { PenId.Length: > 0 });
        }, cancellationToken).ConfigureAwait(false);
        s.RecordWire("cow", cow);
        if (cow!.Release || cow.Slaughter)
        {
            throw new AcceptanceException($"the contained cow carries a release or slaughter designation: {cow}");
        }

        var facts = await s.CallAsync("pet-upkeep", NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        var pet = facts.Upkeep.Animals.FirstOrDefault(a => a.Pawn.Value == petId)
            ?? throw new AcceptanceException($"pet-upkeep: the staged pet {petId} is not in the upkeep census");
        s.RecordWire("pet", pet);
        if (pet.Roping is not Observations.AnimalRoping.Unroped)
        {
            throw new AcceptanceException($"pet-upkeep: the pet should need no pen and have none: {pet}");
        }
    }
}
