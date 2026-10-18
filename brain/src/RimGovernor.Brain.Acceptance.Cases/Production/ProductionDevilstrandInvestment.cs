using System.Globalization;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>production/devilstrand-investment</c> (#3560, variant 1 of the devilstrand case; variant 2 is #3532): the devilstrand
/// stage (research bench, a tailoring bench and a joy source, Devilstrand the player's research goal with ComplexClothing finished,
/// a devilstrand growing zone under a growing climate, food for the horizon). The coupled selection picks a research task whose
/// investment term is a credit, and the <c>colony_model</c> event names the devilstrand project with a positive dV/dt. The ledger
/// reports investment as a loss, so "a positive investment" of the issue is <c>value.investment &lt; 0</c>.
/// <para>
/// One colonist is both researcher and Plants-10 grower: the exposure table is calibrated for a ratio of raid points to defense
/// capacity of 1 to 2 for gunners, and a second armed pawn halves the ratio into the uncalibrated 0.5 to 1 bin (an unarmed one is a
/// noncombatant, which the table does not cover). The colonist keeps every combat skill at 0 (<see cref="Fx.StagePawn.Skills"/>
/// raises Plants alone) with a short bow: defense capacity about 38 against raid points of 36 and rising with colony wealth.
/// </para>
/// <para>
/// The credit is apparel harm avoided, so the stage prices harm: Cassandra on day 25 gives the threat schedule raids, and the quiet
/// colony's raid points sit at the game's floor of 35.
/// </para>
/// </summary>
internal static class ProductionDevilstrandInvestment
{
    private const string Project = "Devilstrand";

    internal static TypedCase Case() => new()
    {
        Name = "production/devilstrand-investment",
        Scope = "Under the served brain, a colony with a research bench, a researcher, Devilstrand as its research goal, a devilstrand "
            + "growing zone, a Plants-10 grower and food in stock has its coupled selection pick a research task whose investment term "
            + "is a credit (the devilstrand cloth's apparel harm avoided, priced through the calibrated exposure table), and its "
            + "colony_model event name the Devilstrand project with a positive dV/dt. A unit test cannot prove the native frame "
            + "inputs (raid points, defense capacity, hostile factions, bill options) or the logged selection.",
        Ticks = TypedCase.StagedTickCap, // a hang guard: the decisive review lands within the first minute of game time
        Start = new LabStart { Colonists = 1 },
        Crew = new Crew { Size = 1 },
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            World = new Fx.StageWorld { Storyteller = "Cassandra", DaysPassed = 25, OutdoorTemperature = 21, GrowingClimate = true },
            Research = [new Fx.StageResearch { Def = "ComplexClothing" }, new Fx.StageResearch { Def = Project, Progress = 0.05f }],
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
            Buildings =
            [
                new Fx.StageBuilding { Name = "bench", Def = "SimpleResearchBench", Stuff = "WoodLog", Cells = [new(4, 3)] },
                new Fx.StageBuilding { Name = "tailor", Def = "HandTailoringBench", Cells = [new(1, 1)] },
                new Fx.StageBuilding { Name = "pin", Def = "HorseshoesPin", Cells = [new(7, 5)] },
            ],
            // About 375 meals: the 60-day horizon needs more than 30.
            Things = [.. Enumerable.Range(0, 5).Select(i => new Fx.StageThing { Name = "meals" + i, Def = "MealSimple", Count = 75, Cell = new(1 + i % 3, 5 + i / 3), Near = true })],
            Zones =
            [
                new Fx.StageZone
                {
                    Name = "field",
                    Kind = Fx.StageZoneKind.Growing,
                    Plant = "Plant_Devilstrand",
                    Cells = [.. Enumerable.Range(5, 4).SelectMany(x => Enumerable.Range(0, 5).Select(z => new Fx.StageCell(x, z)))],
                },
            ],
            Pawns =
            [
                new Fx.StagePawn
                {
                    Name = "colonist",
                    Colonist = 0,
                    SkillLevel = 0,
                    Skills = new Dictionary<string, int> { ["Plants"] = 10 },
                    ClearTraits = true,
                    // Both priorities off: only the brain's blocks research or sow, as in production/research-block.
                    Work = new Dictionary<string, int> { ["Research"] = 0, ["Growing"] = 0 },
                    Gear = new Fx.StageGear { Weapon = "Bow_Short", Strip = true },
                    // Hungry enough that the meal the first review chooses is eaten promptly; a protected eating Method blocks every other choice.
                    Needs = new Dictionary<string, float> { ["Food"] = 0.2f, ["Rest"] = 1f },
                },
            ],
        },
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Serve = new ServeSpec { Families = ["goals", "research", "work"], Prefix = "devilstrand-investment" },
        Run = RunAsync,
    };

    private static bool Research(string task) => task.StartsWith("research/", StringComparison.Ordinal);

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        await GoalReviews.SeedAsync(s, GoalReviews.Empty(s).WithNewGoal(new GoalRecord { Kind = GoalKind.ResearchProject, Target = Project }), cancellationToken).ConfigureAwait(false);

        var before = CoupledSelections.Of(s, 0).Select(r => r.Sequence).DefaultIfEmpty(0UL).Max();
        IReadOnlyList<CoupledSelectionRow> rows = [];
        _ = await s.ServeUntilAsync("devilstrand-investment", s.TicksLeft, ct =>
        {
            rows = CoupledSelections.Of(s, before);
            return Task.FromResult((string.Create(CultureInfo.InvariantCulture, $"{rows.Count} {rows.Count(r => r.Tasks.Any(Research))}"), rows.Any(r => r.Tasks.Any(Research))));
        }, cancellationToken).ConfigureAwait(false);

        var chosen = rows.FirstOrDefault(r => r.Tasks.Any(Research))
            ?? throw new AcceptanceException($"no coupled selection chose a research task in {rows.Count} rows");

        // The ledger reports the investment as a loss, so a credit is negative.
        s.Record("selection", string.Create(CultureInfo.InvariantCulture,
            $"{chosen.Verdict} tasks=[{string.Join(' ', chosen.Tasks)}] investment={chosen.Selected.Investment} unknown=[{string.Join(' ', chosen.Selected.Unknown ?? [])}]"));
        if (chosen.Selected.Investment is not < 0)
        {
            throw new AcceptanceException($"the research selection carries no investment credit: investment {chosen.Selected.Investment}, unknown [{string.Join(' ', chosen.Selected.Unknown ?? [])}]");
        }

        var model = ColonyModelReviews.Of(s);
        var named = model.Any(m => m.Dvdt.Split(';').Any(p => p.Contains(Project, StringComparison.OrdinalIgnoreCase)
            && double.TryParse(p[(p.LastIndexOf('=') + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) && rate > 0));
        if (!named)
        {
            throw new AcceptanceException($"no colony_model event names {Project} with a positive dV/dt in {model.Count} rows");
        }
    }
}
