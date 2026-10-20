using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>layout/barracks-splits</c> (#3661): three colonists sleep in a staged, roofed three-bed barracks the injected plan holds
/// as its Barracks, and one of them is held near its break (recreation frozen empty). Under the served brain the replan
/// splits that pawn's bed out (BarracksSwitchTests owns the price and the hysteresis): a coupled selection starts a planned
/// bedroom credited to its sleeper, and that pawn ends up owning a bed outside the barracks. A unit test cannot prove the
/// native mood read, the bedroom build or the bed move.
/// </summary>
internal static class LayoutBarracksSplits
{
    private const int Colonists = 3;
    private const string BedroomKey = "bedroom/";
    private static readonly string[] Beds = ["bed-a", "bed-b", "bed-c"];

    // A 16x8 site: the roofed 8x5 barracks (6x3 interior) at its west end with its door east and the three beds along its
    // south wall, the logs and meals east of it, where the split bedroom is sited.
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        World = new Fx.StageWorld { Hour = 8 },
        Site = new Fx.StageSite
        {
            Width = 16,
            Height = 8,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 3,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Heavy],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 16, 8), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones, Fx.StageClearKind.Roofs] }],
        Rooms = [new Fx.StageRoom { Name = "dorm", Rect = new Fx.StageRect(0, 0, 8, 5), Stuff = "WoodLog", Doors = [new(7, 2)], Roof = true }],
        Buildings =
        [
            new Fx.StageBuilding { Name = Beds[0], Def = "Bed", Stuff = "WoodLog", Cells = [new(1, 1)] },
            new Fx.StageBuilding { Name = Beds[1], Def = "Bed", Stuff = "WoodLog", Cells = [new(3, 1)] },
            new Fx.StageBuilding { Name = Beds[2], Def = "Bed", Stuff = "WoodLog", Cells = [new(5, 1)] },
        ],
        Things =
        [
            new Fx.StageThing { Name = "logs", Def = "WoodLog", Count = 75, Cell = new(10, 3) },
            new Fx.StageThing { Name = "logs-2", Def = "WoodLog", Count = 75, Cell = new(10, 4) }, // a log stack holds 75
            new Fx.StageThing { Name = "logs-3", Def = "WoodLog", Count = 75, Cell = new(11, 3) },
            new Fx.StageThing { Name = "meals", Def = "MealSimple", Count = 30, Cell = new(2, 6), Near = true },
        ],
        Pawns =
        [
            new Fx.StagePawn { Name = "a", Colonist = 0, Cell = new(2, 3), Bed = Beds[0] },
            new Fx.StagePawn { Name = "b", Colonist = 1, Cell = new(4, 3), Bed = Beds[1] },
            // Recreation frozen empty: the Joy thought holds this pawn's mood target near its break threshold.
            new Fx.StagePawn { Name = "c", Colonist = 2, Cell = new(6, 3), Bed = Beds[2], Needs = new Dictionary<string, float> { ["Joy"] = 0f } },
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "layout/barracks-splits",
        Scope = "Issue #3661: three colonists in a staged roofed three-bed barracks the injected plan holds, one held near its break "
            + "(Joy frozen empty): under the served brain a coupled selection starts a planned bedroom credited to its sleeper, and "
            + "the near-break pawn ends up owning a bed outside the barracks.",
        Ticks = 20_000, // a hang guard: the replan is hourly and the bedroom a few game hours of work
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Colonists },
        Crew = new Crew { Size = Colonists },
        Stage = Spec,
        Ops = [FixtureStage.Tool],
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Serve = new ServeSpec { Families = ["work", "shelter", "sleeping"], NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "barracks-splits" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("the barracks stage found no site");
        var pawn = staged.Pawns["c"].Id;
        var barracksBeds = Beds.SelectMany(b => staged.Buildings[b].Ids).ToHashSet(StringComparer.Ordinal);
        InteriorPiece Slot(int x) => new() { Slot = "bed", Def = "Bed", Size = new Cell(1, 2), Rot = Rotation.North, Rect = new Rectangle(site.X + x, site.Z + 1, 1, 2) };
        await TypedLayoutPlan.InjectAsync(s, new LayoutPlan
        {
            NextRoomId = 2,
            Rooms =
            [
                new PlannedRoom
                {
                    Id = 1,
                    Role = PlannedRole.Barracks,
                    Region = RegionRules.RectRegion(new(site.X + 1, site.Z + 1, 6, 3)),
                    Door = new(site.X + 7, site.Z + 2),
                    DoorRot = Rotation.East,
                },
            ],
            Pieces = new Dictionary<int, IReadOnlyList<InteriorPiece>> { [1] = [Slot(1), Slot(3), Slot(5)] },
        }, cancellationToken).ConfigureAwait(false);

        var before = CoupledSelections.Of(s, 0).Select(r => r.Sequence).DefaultIfEmpty(0UL).Max();
        CoupledSelectionRow? started = null;
        var owned = "";
        _ = await s.ServeUntilAsync("bedroom-moved", s.TicksLeft, async ct =>
        {
            started ??= CoupledSelections.Of(s, before).FirstOrDefault(r => (r.Selected.Contributions ?? []).Any(c => c.Key.StartsWith(BedroomKey, StringComparison.Ordinal)
                && r.SelectedIds.Any(id => id.StartsWith("planned/room" + c.Key.Split('/')[1] + "/", StringComparison.Ordinal))));
            var row = await TypedPawnOps.PawnAsync(s, "pawn", pawn, ct).ConfigureAwait(false);
            owned = row?.OwnedBed?.Id ?? "";
            var moved = owned != "" && !barracksBeds.Contains(owned);
            return (string.Create(CultureInfo.InvariantCulture, $"started={started is not null} bed={owned}"), started is not null && moved);
        }, cancellationToken).ConfigureAwait(false);

        s.Record("selection", started is null ? "none" : string.Join(' ', started.SelectedIds));
        s.Record("bed", owned);
        _ = started ?? throw new AcceptanceException("no coupled selection started a planned bedroom");
        if (owned == "" || barracksBeds.Contains(owned))
        {
            throw new AcceptanceException($"{pawn} still owns \"{owned}\", a barracks bed or none");
        }
    }
}
