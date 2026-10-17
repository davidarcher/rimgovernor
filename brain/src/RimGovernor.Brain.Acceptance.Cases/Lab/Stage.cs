using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lab/stage</c> (#3393, #3395): one test/stage call stages a spec that uses every core primitive on the blank lab, and
/// the typed reply names what each entry made at the cells the spec put it, relative to the site the search found.
/// Incidents are proven by the cases that use them (trade/open).
/// </summary>
internal static class LabStage
{
    private const string Rifle = "Gun_AssaultRifle";

    // A 12x12 site 15 cells east and 20 south of the lab centre, clear of the colonists' start.
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,

        // Stonecutting finished; MultiAnalyzer (deep in the tree, never finished on the lab) a quarter done.
        Research = [new Fx.StageResearch { Def = "Stonecutting" }, new Fx.StageResearch { Def = "MultiAnalyzer", Progress = 0.25f }],
        World = new Fx.StageWorld { OutdoorTemperature = 5, Hour = 13 },
        Site = new Fx.StageSite
        {
            Width = 12,
            Height = 12,
            OffsetX = 15,
            OffsetZ = -20,
            Radius = 10,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unfogged],
        },
        Clear =
        [
            new Fx.StageClear
            {
                Rect = new Fx.StageRect(0, 0, 12, 12),
                Kinds =
                [
                    Fx.StageClearKind.Things, Fx.StageClearKind.Pawns, Fx.StageClearKind.Zones, Fx.StageClearKind.Roofs,
                    Fx.StageClearKind.Fog, Fx.StageClearKind.Snow, Fx.StageClearKind.Home,
                ],
            },
        ],
        Cells = [new Fx.StageCells { Rect = new Fx.StageRect(0, 0, 6, 6), Terrain = "Concrete", Home = true, Fogged = false }],
        Rooms = [new Fx.StageRoom { Name = "hut", Rect = new Fx.StageRect(0, 0, 5, 5), Stuff = "WoodLog", Doors = [new(2, 0)], Roof = true }],
        Buildings =
        [
            new Fx.StageBuilding { Name = "frame", Def = "Wall", Stuff = "WoodLog", Cells = [new(7, 0)], State = Fx.StageBuildState.Frame },
            new Fx.StageBuilding { Name = "blueprint", Def = "Wall", Stuff = "WoodLog", Cells = [new(8, 0)], State = Fx.StageBuildState.Blueprint },
            new Fx.StageBuilding { Name = "sandbags", Def = "Sandbags", Cells = [new(10, 0), new(11, 0)], HitPoints = 50 },
        ],
        Things = [new Fx.StageThing { Name = "steel", Def = "Steel", Count = 120, Cell = new(8, 3), Near = true, Forbidden = true }],
        Pawns =
        [
            new Fx.StagePawn
            {
                Name = "cook",
                Colonist = 0,
                Cell = new(2, 2),
                AllWork = 3,
                Work = new Dictionary<string, int> { ["Cooking"] = 1 },
                Timetable = Fx.StageTimeAssignment.Work,
                Needs = new Dictionary<string, float> { ["Food"] = 0.5f },
                Inspiration = "Inspired_Creativity",
            },

            // No cell: the second colonist stays where it stands.
            new Fx.StagePawn { Name = "sad", Colonist = 1, MentalState = "Wander_Sad" },
            new Fx.StagePawn
            {
                Name = "raider",
                Kind = CombatFixtures.Gunner,
                Faction = Fx.StageFaction.Hostile,
                Age = 30,
                ClearTraits = true,
                SkillLevel = 8,
                Cell = new(10, 10),
                Gear = new Fx.StageGear { Weapon = Rifle, Strip = true },
            },
            new Fx.StagePawn { Name = "muffalo", Kind = "Muffalo", Faction = Fx.StageFaction.Player, Age = 3, Cell = new(7, 10) },
        ],
        Zones =
        [
            new Fx.StageZone { Name = "store", Kind = Fx.StageZoneKind.Stockpile, Cells = [new(6, 7), new(7, 7)], Priority = Fx.StageStoragePriority.Important, Allow = ["Steel"] },
            new Fx.StageZone { Name = "field", Kind = Fx.StageZoneKind.Growing, Cells = [new(9, 7), new(10, 7)], Plant = "Plant_Potato" },
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "lab/stage",
        Scope = "Fixture layer (#3393, #3395): test/stage stages one spec using every core primitive (research finished and in "
            + "progress, a pinned outdoor temperature and clock hour, site search, clear, set cells, room, "
            + "complete, frame and blueprint buildings, items placed near and forbidden, an existing colonist with work, timetable, "
            + "needs and an inspiration set, a second colonist left in place in a mental state, a generated armed raider and a "
            + "player animal, a stockpile and a growing zone) and its typed StageReply keys each entry's ids and cells by the "
            + "spec's names at the found site.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 2 },
        Stage = Spec,
        QuietWorld = true,
        Run = RunAsync,
    };

    private static Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var reply = s.Staged!;
        var site = reply.Site ?? throw new AcceptanceException("test/stage replied no site");
        Fx.StageCell At(int x, int z) => new(site.X + x, site.Z + z);
        var problems = new List<string>();
        void Want(bool ok, string what)
        {
            if (!ok)
            {
                problems.Add(what);
            }
        }

        Want(site.Width == 12 && site.Height == 12, $"site {site}, want 12x12");
        Want(reply.Research.GetValueOrDefault("Stonecutting") == 1f, "Stonecutting not finished");
        Want(Math.Abs(reply.Research.GetValueOrDefault("MultiAnalyzer") - 0.25f) < 0.001f, $"MultiAnalyzer at {reply.Research.GetValueOrDefault("MultiAnalyzer")}, want 0.25");
        Want(Math.Abs(reply.OutdoorTemperature - 5) < 0.5f, $"outdoor {reply.OutdoorTemperature} C, want 5");
        Want(reply.Hour == 13, $"hour {reply.Hour}, want 13");
        var hut = reply.Rooms.GetValueOrDefault("hut");
        Want(hut is { Walls.Count: 15, Doors.Count: 1, Interior.Count: 9 }, $"hut {Show(hut)}, want 15 walls, 1 door, 9 interior cells");
        Want(hut?.Interior is [var first, ..] && first == At(1, 1), $"hut interior does not start at {At(1, 1)}");
        Want(reply.Buildings.GetValueOrDefault("frame") is { Ids.Count: 1 } frame && frame.Cells[0] == At(7, 0), "frame not at (7,0)");
        Want(reply.Buildings.GetValueOrDefault("blueprint") is { Ids.Count: 1 } blueprint && blueprint.Cells[0] == At(8, 0), "blueprint not at (8,0)");
        Want(reply.Buildings.GetValueOrDefault("sandbags") is { Ids.Count: 2 } bags && bags.Cells.SequenceEqual([At(10, 0), At(11, 0)]), "sandbags not at (10,0) and (11,0)");
        Want(reply.Things.GetValueOrDefault("steel") is { Ids.Count: > 0 } steel && steel.Cells.All(c => Math.Abs(c.X - At(8, 3).X) <= 2 && Math.Abs(c.Z - At(8, 3).Z) <= 2), "steel not placed near (8,3)");
        var cook = reply.Pawns.GetValueOrDefault("cook");
        Want(
            cook is { Spawned: true, Hostile: false, Inspiration: "Inspired_Creativity" } && cook.Cell == At(2, 2),
            $"cook {cook}, want a spawned inspired colonist at {At(2, 2)}");
        Want(reply.Pawns.GetValueOrDefault("sad") is { Spawned: true, MentalState: "Wander_Sad" }, $"sad {reply.Pawns.GetValueOrDefault("sad")}, want a colonist in Wander_Sad");
        var raider = reply.Pawns.GetValueOrDefault("raider");
        Want(
            raider is { Hostile: true, Weapon: Rifle, LordJob: "LordJob_AssaultColony", Worn.Count: 0 } && raider.Cell == At(10, 10),
            $"raider {raider}, want a hostile stripped rifleman under an assault lord at {At(10, 10)}");
        var muffalo = reply.Pawns.GetValueOrDefault("muffalo");
        Want(muffalo is { Kind: "Muffalo", Hostile: false } && muffalo.Cell == At(7, 10), $"muffalo {muffalo}, want a player Muffalo at {At(7, 10)}");
        var store = reply.Zones.GetValueOrDefault("store") ?? "";
        var field = reply.Zones.GetValueOrDefault("field") ?? "";
        Want(store.Length > 0 && field.Length > 0 && store != field, $"zones store \"{store}\" and field \"{field}\", want two zone ids");
        Want(reply.Digest.Length > 0 && reply.DigestRows.Count > 0, "no digest");
        s.Record("site", $"{site.X},{site.Z}");
        s.Record("digest", reply.Digest);
        return problems.Count == 0 ? Task.CompletedTask : throw new AcceptanceException(string.Join("; ", problems));
    }

    private static string Show(Fx.StagedRoom? room) =>
        room is null ? "missing" : $"{room.Walls.Count} walls, {room.Doors.Count} doors, {room.Interior.Count} interior cells";
}
