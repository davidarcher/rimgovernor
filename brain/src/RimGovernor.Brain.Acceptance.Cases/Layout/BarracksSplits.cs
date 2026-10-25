using System.Globalization;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>layout/barracks-splits</c> (#3661): three colonists sleep in a staged, roofed three-bed barracks the injected plan holds
/// as its Barracks, and one of them is held near its break. Under the served brain the replan splits that pawn's bed out
/// (BarracksSwitchTests owns the price and the hysteresis): a coupled selection starts a planned bedroom credited to its
/// sleeper, and that pawn ends up owning a bed outside the barracks. Ring first (#3743): the new bed is first seen (a
/// blueprint, at the latest built) in a proper room its ring encloses, never on open ground; the roof follows the ring on
/// its own and is not part of the claim. A unit test cannot prove the native mood read, the bedroom build, its order or
/// the bed move.
/// </summary>
/// <remarks>
/// Near its break (#3680): the bed merge prices a sleeper's barracks loss at a flat mood target, and a break tier's cost
/// is the same anywhere inside the tier, so the loss pays for a bedroom only when the bedroom's bed thought lifts the
/// target over the minor threshold (0.35) and the barracks' does not. The staged mood target (StagePawn.MoodTarget) holds
/// the pawn's target at <see cref="NearBreak"/> while expectations, the room it stands in and its meals move its other
/// thoughts: under the threshold by less than the bedroom's bed-thought lift over the barracks' (5 to 8 points at the
/// planned stage). Mood is frozen with the other needs, so the pawn never breaks; food and rest stay live, so the coupled
/// schedule fits the bedroom's work between meals rather than after a frozen stomach's far one.
/// </remarks>
internal static class LayoutBarracksSplits
{
    private const int Colonists = 3;
    // A bedroom's or a barracks' credit row (#3796: keyed by the room's role).
    private static bool BedroomKey(string key) => key.StartsWith("bedroom/", StringComparison.Ordinal) || key.StartsWith("barracks/", StringComparison.Ordinal);
    private static readonly string[] Beds = ["bed-a", "bed-b", "bed-c"];
    private const float NearBreak = 0.32f;

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
            new Fx.StagePawn { Name = "c", Colonist = 2, Cell = new(6, 3), Bed = Beds[2], MoodTarget = NearBreak },
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "layout/barracks-splits",
        Scope = "Issue #3661: three colonists in a staged roofed three-bed barracks the injected plan holds, one held near its break "
            + "(its mood target held just under the minor threshold): under the served brain a coupled selection starts a planned "
            + "bedroom credited to its sleeper, and the near-break pawn ends up owning a bed outside the barracks.",
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
                    DoorSide = new DoorSide.Doored(Rotation.East),
                },
            ],
            Pieces = new Dictionary<int, IReadOnlyList<InteriorPiece>> { [1] = [Slot(1), Slot(3), Slot(5)] },
        }, cancellationToken).ConfigureAwait(false);

        var before = CoupledSelections.Of(s, 0).Select(r => r.Sequence).DefaultIfEmpty(0UL).Max();
        CoupledSelectionRow? started = null;
        var owned = "";
        RimGovernor.Brain.GameFacts.Observations.BuildingState? newBed = null;
        RimGovernor.Brain.GameFacts.Observations.RoomState? bedRoom = null;
        _ = await s.ServeUntilAsync("bedroom-moved", s.TicksLeft, async ct =>
        {
            // The first bed placed outside the barracks, at whatever stage, and the room its cell lies in then.
            if (newBed is null)
            {
                var buildings = await s.BuildingsAsync("beds", new RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest(), ct).ConfigureAwait(false);
                newBed = buildings.FirstOrDefault(b => (b.Construction is RimGovernor.Brain.GameFacts.Observations.ConstructionState.Build build ? build.Definition : b.Building.DefName) == "Bed" && !barracksBeds.Contains(b.Building.Id));
                if (newBed?.Building.Place is RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap { Cell: var at })
                {
                    var request = new RimGovernor.Brain.GameOrders.Observations.ListRoomsRequest { Scope = s.Scope, Region = new RimGovernor.Brain.GameFacts.Observations.Rectangle { Minimum = at, Maximum = at } };
                    var reply = await s.CallAsync("bed-room", NativeOps.ListRooms, request, ct).ConfigureAwait(false);
                    var rooms = reply.Rooms;
                    bedRoom = rooms.Count > 0 ? rooms[0] : null;
                }
            }
            started ??= CoupledSelections.Of(s, before).FirstOrDefault(r => (r.Selected.Contributions ?? []).Any(c => BedroomKey(c.Key)
                && r.SelectedIds.Any(id => id.StartsWith("planned/room" + c.Key.Split('/')[1] + "/", StringComparison.Ordinal))));
            var row = await TypedPawnOps.PawnAsync(s, "pawn", pawn, ct).ConfigureAwait(false);
            owned = row?.OwnedBed?.Id ?? "";
            var moved = owned != "" && !barracksBeds.Contains(owned);
            return (string.Create(CultureInfo.InvariantCulture, $"started={started is not null} bed={owned}"), started is not null && moved);
        }, cancellationToken).ConfigureAwait(false);

        s.Record("selection", started is null ? "none" : string.Join(' ', started.SelectedIds));
        s.Record("bed", owned);
        s.Record("bed-room", bedRoom is null ? "none" : string.Create(CultureInfo.InvariantCulture, $"{newBed?.Status} {bedRoom.Id}:{bedRoom.ProperRoom}:{bedRoom.TouchesMapEdge}"));
        _ = started ?? throw new AcceptanceException("no coupled selection started a planned bedroom");
        if (bedRoom is not { ProperRoom: true, Doorway: not true, TouchesMapEdge: not true })
        {
            throw new AcceptanceException($"the new bed ({newBed?.Status}) was placed in room {bedRoom?.Id ?? "none"}, not a proper room its ring encloses: furniture came before the ring");
        }
        if (owned == "" || barracksBeds.Contains(owned))
        {
            throw new AcceptanceException($"{pawn} still owns \"{owned}\", a barracks bed or none");
        }
    }
}
