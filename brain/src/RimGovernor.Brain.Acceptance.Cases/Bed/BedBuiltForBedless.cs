using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>bed/built-for-bedless</c> (#3592): two colonists share a staged, roofed two-bed barracks the injected plan holds as
/// its Barracks; one owns the only bed, the other has none, the logs for a bed are in stock and nothing else asks for work.
/// Under the served brain a bed is built in the barracks and the bedless colonist comes to own it, read back from the
/// production pawn read. A unit test cannot prove the selection admitting the bed, the native build or the assignment.
/// </summary>
internal static class BedBuiltForBedless
{
    private const int Colonists = 2;
    private const string BedDefinition = "Bed";

    // A 12x8 site: the roofed 6x5 barracks (4x3 interior) at its west end with its door east and the one staged bed against
    // its south wall, the logs and meals east of it.
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
        Buildings = [new Fx.StageBuilding { Name = "bed-a", Def = "Bed", Stuff = "WoodLog", Cells = [new(1, 1)] }],
        Things =
        [
            new Fx.StageThing { Name = "logs", Def = "WoodLog", Count = 75, Cell = new(8, 3) },
            new Fx.StageThing { Name = "logs-2", Def = "WoodLog", Count = 75, Cell = new(8, 4) }, // a log stack holds 75
            new Fx.StageThing { Name = "meals", Def = "MealSimple", Count = 30, Cell = new(2, 6), Near = true },
        ],
        Pawns =
        [
            new Fx.StagePawn { Name = "a", Colonist = 0, Cell = new(2, 3), Bed = "bed-a" },
            new Fx.StagePawn { Name = "bedless", Colonist = 1, Cell = new(3, 3) },
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "bed/built-for-bedless",
        Scope = "Issue #3592: in a staged roofed two-bed barracks the injected plan holds, one colonist owns the only bed and the "
            + "other has none, with logs in stock and a free builder: under the served brain a second bed is built and the "
            + "bedless colonist owns it within the day, read back natively.",
        Ticks = 20_000, // a hang guard well inside the one day the claim allows: the replan is hourly and a bed a short build
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Colonists },
        Crew = new Crew { Size = Colonists },
        Stage = Spec,
        Ops = [FixtureStage.Tool],
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Serve = new ServeSpec { Families = ["work", "shelter", "sleeping"], NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "bed-for-bedless" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("the barracks stage found no site");
        var pawn = staged.Pawns["bedless"].Id;
        var held = staged.Buildings["bed-a"].Ids.Single();
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
                    Region = RegionRules.RectRegion(new(site.X + 1, site.Z + 1, 4, 3)),
                    Door = new(site.X + 5, site.Z + 2),
                    DoorSide = new DoorSide.Doored(Rotation.East),
                },
            ],
            Pieces = new Dictionary<int, IReadOnlyList<InteriorPiece>> { [1] = [Slot(1), Slot(3)] },
        }, cancellationToken).ConfigureAwait(false);

        if ((await TypedPawnOps.PawnAsync(s, "bedless-before", pawn, cancellationToken).ConfigureAwait(false))?.OwnedBed?.Id is { Length: > 0 } before)
        {
            throw new AcceptanceException($"{pawn} already owns {before}: the precondition hides the claim");
        }
        // A stand-in sleeping spot (ShelterRules.StandInsOwed) may come first; only a built Bed answers the claim.
        var owned = "";
        var def = "";
        _ = await s.ServeUntilAsync("bed-owned", s.TicksLeft, async ct =>
        {
            var row = await TypedPawnOps.PawnAsync(s, "pawn", pawn, ct).ConfigureAwait(false);
            owned = row?.OwnedBed?.Id ?? "";
            def = "";
            if (owned != "" && owned != held)
            {
                var built = await s.BuildingsAsync("owned-bed", new Observations.ListBuildingsRequest { Ids = [owned], Statuses = [Observations.BuildingStatus.Built] }, ct).ConfigureAwait(false);
                def = built.FirstOrDefault(b => b.Building.Id == owned)?.Building.DefName ?? "";
            }
            return (string.Create(CultureInfo.InvariantCulture, $"bed={owned}:{def}"), def == BedDefinition);
        }, cancellationToken).ConfigureAwait(false);

        s.Record("bed", $"{owned}:{def}");
        if (def != BedDefinition)
        {
            throw new AcceptanceException($"{pawn} owns \"{owned}\" ({def}): no new Bed was built and assigned");
        }
    }
}
