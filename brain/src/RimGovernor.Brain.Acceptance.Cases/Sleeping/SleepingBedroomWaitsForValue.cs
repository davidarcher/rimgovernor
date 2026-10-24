using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>sleeping/bedroom-waits-for-value</c> (#3655): a calm colony of two sharing a staged barracks, an injected plan holding
/// one planned bedroom with the logs for it, and a research goal tier 2 credits. The bedroom's schedules are credited its
/// first unhoused colonist's mood gain (the barracks thought replaced by the bedroom's) from closure to tier 2's horizon,
/// the research its goal's value for the same hours; the selection holds the bedroom, and the <c>coupled_selection</c> row
/// shows both values. A unit test cannot prove the native room prediction, the bed-thought stages or the logged comparison.
/// </summary>
internal static class SleepingBedroomWaitsForValue
{
    private const string Project = "Brewing";
    // Any room's credit: the first hourly replan sites the unbuilt bedroom again under a new id (tile_sited_again), so
    // the case reads the room from the credit key, not the injected id.
    // A bedroom's or a barracks' credit row (#3796: keyed by the room's role).
    private static bool BedroomKey(string key) => key.StartsWith("bedroom/", StringComparison.Ordinal) || key.StartsWith("barracks/", StringComparison.Ordinal);

    // A 16x8 site: the roofed 8x5 barracks with both colonists' beds at its west end, the planned bedroom's 3x3 interior at
    // (12..14, 2..4) with its door south, and the research bench, the logs and the meals between them.
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        World = new Fx.StageWorld { Hour = 8 },
        Research = [new Fx.StageResearch { Def = Project, Progress = 0.05f }],
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
            new Fx.StageBuilding { Name = "bed-a", Def = "Bed", Stuff = "WoodLog", Cells = [new(1, 1)] },
            new Fx.StageBuilding { Name = "bed-b", Def = "Bed", Stuff = "WoodLog", Cells = [new(3, 1)] },
            new Fx.StageBuilding { Name = "bench", Def = "SimpleResearchBench", Stuff = "WoodLog", Cells = [new(9, 6)] },
        ],
        Things =
        [
            new Fx.StageThing { Name = "logs", Def = "WoodLog", Count = 75, Cell = new(10, 3) },
            new Fx.StageThing { Name = "logs-2", Def = "WoodLog", Count = 75, Cell = new(10, 4) }, // a log stack holds 75
            new Fx.StageThing { Name = "meals", Def = "MealSimple", Count = 30, Cell = new(2, 6), Near = true },
        ],
        Pawns =
        [
            // Both priorities off: only the brain's blocks research or build.
            new Fx.StagePawn { Name = "a", Colonist = 0, Cell = new(2, 3), Bed = "bed-a", Work = new Dictionary<string, int> { ["Research"] = 0, ["Construction"] = 0 } },
            new Fx.StagePawn { Name = "b", Colonist = 1, Cell = new(4, 3), Bed = "bed-b", Work = new Dictionary<string, int> { ["Research"] = 0, ["Construction"] = 0 } },
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "sleeping/bedroom-waits-for-value",
        Scope = "Issue #3655: under the served brain a calm colony of two in a staged barracks, with one planned bedroom (logs in stock) "
            + "and a research goal tier 2 credits, logs a coupled_selection whose vectors carry the bedroom's investment credit "
            + "(bedroom/<room>/<pawn>) beside the research's value, and the selection holds the bedroom (no root of that planned room selected). "
            + "A unit test cannot prove the native room prediction, the bed-thought stages or the logged comparison.",
        Ticks = TypedCase.StagedTickCap, // a hang guard: the decisive review lands within the first minute of game time
        Start = new LabStart { Colonists = 2 },
        Crew = new Crew { Size = 2 },
        Tier = CaseTier.OnDemand,
        Stage = Spec,
        Ops = [FixtureStage.Tool],
        Keep = ["Food", "Rest", "Joy"],
        QuietWorld = true,
        // Planned rooms become candidates only under a housing Need (ForecastCoupledCandidates.GenerateCoupledActivities);
        // sleeping turns on shelter/sleeping.
        Serve = new ServeSpec { Families = ["goals", "research", "work", "sleeping"], Prefix = "bedroom-waits" },
        Run = RunAsync,
    };

    private static bool Bedroom(CoupledOutcome outcome) => outcome.Contributions?.Any(c => BedroomKey(c.Key)) == true;

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var site = s.Staged!.Site ?? throw new AcceptanceException("the bedroom stage found no site");
        await TypedLayoutPlan.InjectAsync(s, new LayoutPlan
        {
            NextRoomId = 2,
            Rooms =
            [
                new PlannedRoom
                {
                    Id = 1,
                    Role = PlannedRole.Bedroom,
                    Region = RegionRules.RectRegion(new(site.X + 12, site.Z + 2, 3, 3)),
                    Door = new(site.X + 13, site.Z + 1),
                    DoorSide = new DoorSide.Doored(Rotation.South),
                },
            ],
        }, cancellationToken).ConfigureAwait(false);
        await GoalReviews.SeedAsync(s, GoalReviews.Empty(s).WithNewGoal(new GoalRecord { Kind = GoalKind.ResearchProject, Target = Project }), cancellationToken).ConfigureAwait(false);

        var before = CoupledSelections.Of(s, 0).Select(r => r.Sequence).DefaultIfEmpty(0UL).Max();
        CoupledSelectionRow? priced = null;
        _ = await s.ServeUntilAsync("bedroom-priced", s.TicksLeft, ct =>
        {
            var rows = CoupledSelections.Of(s, before);
            priced = rows.FirstOrDefault(r => Bedroom(r.Selected) || r.Rejected.Any(Bedroom));
            return Task.FromResult((string.Create(CultureInfo.InvariantCulture, $"{rows.Count} {priced is not null}"), priced is not null));
        }, cancellationToken).ConfigureAwait(false);

        var row = priced ?? throw new AcceptanceException("no coupled selection priced the planned bedroom's credit");
        var credit = (row.Rejected.Prepend(row.Selected).First(Bedroom).Contributions ?? []).First(c => BedroomKey(c.Key));
        s.Record("selection", string.Create(CultureInfo.InvariantCulture,
            $"{row.Verdict} ids=[{string.Join(' ', row.SelectedIds)}] goals={row.Selected.Goals} investment={row.Selected.Investment} bedroom={credit.Key}:{credit.Value} unknown=[{string.Join(' ', row.Selected.Unknown ?? [])}]"));
        if (!(credit.Value < 0))
        {
            throw new AcceptanceException($"the bedroom's credit {credit.Key} is not a gain: {credit.Value}");
        }
        var room = "planned/room" + credit.Key.Split('/')[1] + "/";
        if (row.SelectedIds.Any(id => id.StartsWith(room, StringComparison.Ordinal)))
        {
            throw new AcceptanceException($"the selection started the bedroom over the research: [{string.Join(' ', row.SelectedIds)}]");
        }
        if (!row.Tasks.Any(t => t.StartsWith("research/", StringComparison.Ordinal)) || row.Selected.Goals is not < 0)
        {
            throw new AcceptanceException($"the selection carries no research value: tasks [{string.Join(' ', row.Tasks)}], goals {row.Selected.Goals}");
        }
    }
}
