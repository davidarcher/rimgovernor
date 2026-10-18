using System.Globalization;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>production/devilstrand-investment-starving</c> (#3532, variant 2 of the devilstrand case; variant 1 is #3560): the
/// devilstrand stage (research bench and researcher, Devilstrand the player's research goal, a devilstrand growing zone,
/// a Plants-10 grower) with every colonist's food near zero and prepared meals lying on the ground. While the urgent
/// floor binds, the coupled selection picks the food task and no research or sow task: investment never outranks starvation.
/// </summary>
internal static class ProductionDevilstrandStarving
{
    private const string Project = "Devilstrand";
    private const float Hungry = 0.03f;

    internal static TypedCase Case() => new()
    {
        Name = "production/devilstrand-investment-starving",
        Scope = "Under the served brain, a colony with a research bench, a researcher, Devilstrand as its research goal, a devilstrand "
            + "growing zone and a Plants-10 grower, whose colonists are nearly out of food with meals at hand, has its first coupled "
            + "selection pick the food task, with no research or sow task: the urgent-zero floor outranks the investment term. A unit "
            + "test cannot prove the native candidates, the live nutrition or the logged selection.",
        Ticks = 12_000,
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 9,
                Height = 7,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 3,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Unroofed],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 9, 7), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones] }],
            Cells = [new Fx.StageCells { Rect = new Fx.StageRect(0, 0, 9, 7), Terrain = "Soil" }],
            Buildings = [new Fx.StageBuilding { Name = "bench", Def = "SimpleResearchBench", Stuff = "WoodLog", Cells = [new(4, 3)] }],
            Things = [new Fx.StageThing { Name = "meals", Def = "MealSimple", Count = 6, Cell = new(1, 5), Near = true }],
            Zones =
            [
                new Fx.StageZone
                {
                    Name = "field",
                    Kind = Fx.StageZoneKind.Growing,
                    Plant = "Plant_Devilstrand",
                    Cells = [.. Enumerable.Range(5, 3).SelectMany(x => Enumerable.Range(0, 2).Select(z => new Fx.StageCell(x, z)))],
                },
            ],
            Pawns =
            [
                new Fx.StagePawn { Name = "researcher", Colonist = 0, Work = new Dictionary<string, int> { ["Research"] = 1 }, Needs = Starving() },
                new Fx.StagePawn { Name = "grower", Colonist = 1, SkillLevel = 10, Work = new Dictionary<string, int> { ["Growing"] = 1 }, Needs = Starving() },
                new Fx.StagePawn { Name = "idle", Colonist = 2, Needs = Starving() },
            ],
        },
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "devilstrand-starving" },
        Run = RunAsync,
    };

    private static Dictionary<string, float> Starving() => new() { ["Food"] = Hungry, ["Rest"] = 1f };

    // Food candidates (CoupledFoodMethodCandidates): a harvest, a prepared or raw lot, or a need task that eats a meal (need/pawn/meal/n).
    private static bool Food(string task) => task.StartsWith("harvest/", StringComparison.Ordinal) || task.StartsWith("food/", StringComparison.Ordinal)
        || task.StartsWith("raw/", StringComparison.Ordinal) || (task.StartsWith("need/", StringComparison.Ordinal) && task.Contains("Meal", StringComparison.Ordinal));

    private static bool Investment(string task) => task.StartsWith("research/", StringComparison.Ordinal) || task.StartsWith("sow/", StringComparison.Ordinal);

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        await GoalReviews.SeedAsync(s, GoalReviews.Empty(s).WithNewGoal(new GoalRecord { Kind = GoalKind.ResearchProject, Target = Project }), cancellationToken).ConfigureAwait(false);

        var before = CoupledSelections.Of(s, 0).Select(r => r.Sequence).DefaultIfEmpty(0UL).Max();
        IReadOnlyList<CoupledSelectionRow> rows = [];
        _ = await s.ServeUntilAsync("devilstrand-starving", s.TicksLeft, ct =>
        {
            rows = CoupledSelections.Of(s, before);
            return Task.FromResult((string.Create(CultureInfo.InvariantCulture, $"{rows.Count} {rows.Count(r => r.Tasks.Count > 0)}"), rows.Any(r => r.Tasks.Count > 0)));
        }, cancellationToken).ConfigureAwait(false);

        var first = rows.FirstOrDefault(r => r.Tasks.Count > 0)
            ?? throw new AcceptanceException($"no coupled selection chose any task in {rows.Count} rows");
        s.Record("selection", string.Create(CultureInfo.InvariantCulture,
            $"{first.Verdict} tasks=[{string.Join(' ', first.Tasks)}] baseline_hunger_zero={first.Baseline.UrgentZeroExcess} selected_hunger_zero={first.Selected.UrgentZeroExcess} investment_logged={first.Selected.InvestmentLogged}"));

        // The precondition held: the do-nothing outcome starves someone, so the urgent floor is what the comparison must clear.
        if (first.Baseline.UrgentZeroExcess is not > 0)
        {
            throw new AcceptanceException($"the baseline did not starve (urgent-zero excess {first.Baseline.UrgentZeroExcess}): the stage did not bind the floor");
        }

        // Every selection made while the floor binds (a later review, after the colonists ate, is free to invest).
        var invested = rows.Where(r => r.Baseline.UrgentZeroExcess > 0).SelectMany(r => r.Tasks).Where(Investment).ToList();
        if (invested.Count > 0)
        {
            throw new AcceptanceException("a research or sow task was selected while the urgent floor bound: " + string.Join(' ', invested));
        }

        // The investment candidate was in the comparison and its term is logged: it lost, it was not absent.
        if (!first.RejectedIds.Any(Investment) || !first.Selected.InvestmentLogged)
        {
            throw new AcceptanceException($"no research or sow alternative was compared and rejected (rejected [{string.Join(' ', first.RejectedIds)}], investment logged {first.Selected.InvestmentLogged})");
        }

        if (!first.Tasks.Any(Food) || first.Selected.UrgentZeroExcess is not { } excess || excess >= first.Baseline.UrgentZeroExcess)
        {
            throw new AcceptanceException($"the selection did not take food: tasks [{string.Join(' ', first.Tasks)}], hunger-zero {first.Selected.UrgentZeroExcess} against baseline {first.Baseline.UrgentZeroExcess}");
        }
    }
}
