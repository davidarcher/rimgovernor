using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>bed/built-for-guest</c> (RG-HABITAT-001 "Guests or prisoners need accommodation"): two colonists own the two beds of
/// a staged, roofed barracks the injected plan holds with a third bed slot; a hosted guest (the player its host faction)
/// has no vacant bed, the logs for a bed are in stock and nothing else asks for work. Under the served brain's normal
/// review the guest stays a sleeping target, a bed is selected and built, and the guest comes to own it, read back from
/// the production pawn read. No bedroom step postpones the build (SleepingRules.BedroomsFirst). A unit test cannot prove
/// the native build or the guest's assignment.
/// </summary>
internal static class BedBuiltForGuest
{
    private const int Colonists = 2;
    private const string BedDefinition = "Bed";
    private static readonly string[] StagedBeds = ["bed-a", "bed-b"];

    // The bed-for-bedless site: the roofed 6x5 barracks (4x3 interior) at its west end, door east, with both colonists'
    // beds against its south wall and the logs and meals east of it.
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        World = new Fx.StageWorld { Hour = 8 },
        Site = new Fx.StageSite
        {
            Width = 12,
            Height = 8,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 3,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Heavy],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 12, 8), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones, Fx.StageClearKind.Roofs] }],
        Rooms = [new Fx.StageRoom { Name = "dorm", Rect = new Fx.StageRect(0, 0, 6, 5), Stuff = "WoodLog", Doors = [new(5, 2)], Roof = true }],
        Buildings =
        [
            new Fx.StageBuilding { Name = "bed-a", Def = BedDefinition, Stuff = "WoodLog", Cells = [new(1, 1)] },
            new Fx.StageBuilding { Name = "bed-b", Def = BedDefinition, Stuff = "WoodLog", Cells = [new(2, 1)] },
        ],
        Things =
        [
            new Fx.StageThing { Name = "logs", Def = "WoodLog", Count = 75, Cell = new(8, 3) },
            new Fx.StageThing { Name = "logs-2", Def = "WoodLog", Count = 75, Cell = new(8, 4) }, // a log stack holds 75
            new Fx.StageThing { Name = "meals", Def = "MealSimple", Count = 30, Cell = new(2, 6), Near = true },
        ],
        Pawns =
        [
            new Fx.StagePawn { Name = "a", Colonist = 0, Cell = new(2, 3), Bed = "bed-a" },
            new Fx.StagePawn { Name = "b", Colonist = 1, Cell = new(3, 3), Bed = "bed-b" },
            new Fx.StagePawn { Name = "guest", Kind = "Colonist", Faction = Fx.StageFaction.Ancients, Guest = true, Cell = new(4, 3) },
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "bed/built-for-guest",
        Scope = "RG-HABITAT-001: in a staged roofed barracks whose two beds the two colonists own, with a third bed slot in the "
            + "injected plan, a hosted guest has no vacant bed; with logs in stock and a free builder, under the served brain's "
            + "normal review a bed is selected, built and assigned to the guest within the day, read back natively.",
        Ticks = 20_000, // a hang guard well inside the one day the claim allows: the replan is hourly and a bed a short build
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Colonists },
        Crew = new Crew { Size = Colonists },
        Stage = Spec,
        Ops = [FixtureStage.Tool],
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Serve = new ServeSpec { Families = ["work", "shelter", "sleeping"], NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "bed-for-guest" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("the barracks stage found no site");
        var guest = staged.Pawns["guest"].Id;
        var held = StagedBeds.Select(b => staged.Buildings[b].Ids.Single()).ToHashSet(StringComparer.Ordinal);
        InteriorPiece Slot(int x) => new() { Slot = "bed", Def = BedDefinition, Size = new Cell(1, 2), Rot = Rotation.North, Rect = new Rectangle(site.X + x, site.Z + 1, 1, 2) };
        await TypedLayoutPlan.InjectAsync(s, new LayoutPlan
        {
            NextRoomId = 2,
            Rooms =
            [
                new PlannedRoom
                {
                    Id = 1,
                    Role = PlannedRole.Barracks,
                    Region = RegionRules.RectRegion(new(site.X + 1, site.Z + 1, 4, 3)),
                    Door = new(site.X + 5, site.Z + 2),
                    DoorSide = new DoorSide.Doored(Rotation.East),
                },
            ],
            Pieces = new Dictionary<int, IReadOnlyList<InteriorPiece>> { [1] = [Slot(1), Slot(2), Slot(4)] },
        }, cancellationToken).ConfigureAwait(false);

        if ((await TypedPawnOps.PawnAsync(s, "guest-before", guest, cancellationToken).ConfigureAwait(false))?.OwnedBed?.Id is { Length: > 0 } before)
        {
            throw new AcceptanceException($"{guest} already owns {before}: the precondition hides the claim");
        }
        // A stand-in sleeping spot (ShelterRules.StandInsOwed) may come first; only a built Bed answers the claim.
        var owned = "";
        var def = "";
        _ = await s.ServeUntilAsync("guest-bed-owned", s.TicksLeft, async ct =>
        {
            var row = await TypedPawnOps.PawnAsync(s, "guest", guest, ct).ConfigureAwait(false);
            owned = row?.OwnedBed?.Id ?? "";
            def = "";
            if (owned != "" && !held.Contains(owned))
            {
                var built = await s.BuildingsAsync("guest-bed", new RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest { Ids = [owned], Statuses = [RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built] }, ct).ConfigureAwait(false);
                def = built.FirstOrDefault(b => b.Building.Id == owned)?.Building.DefName ?? "";
            }
            return (string.Create(CultureInfo.InvariantCulture, $"bed={owned}:{def}"), def == BedDefinition);
        }, cancellationToken).ConfigureAwait(false);

        s.Record("bed", $"{owned}:{def}");
        if (def != BedDefinition)
        {
            throw new AcceptanceException($"guest {guest} owns \"{owned}\" ({def}): no new Bed was built and assigned to it");
        }
    }
}
