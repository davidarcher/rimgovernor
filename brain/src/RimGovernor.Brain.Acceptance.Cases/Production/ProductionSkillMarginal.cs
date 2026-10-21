using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>production/skill-marginal</c> (#3663): a live review's tier-2 run builds the colonist's skill stocks from the review frame's
/// biography and prices one XP of Medicine: the <c>colony_model</c> event carries a <c>skill/&lt;pawn&gt;/Medicine</c> value per XP.
/// The colony prices its trajectory (food for the horizon, Cassandra on day 25 for a raid schedule), so the perturbations run.
/// </summary>
internal static class ProductionSkillMarginal
{
    private const string Skill = "/Medicine=";

    internal static TypedCase Case() => new()
    {
        Name = "production/skill-marginal",
        Scope = "Under the served brain, a staged one-colonist colony with food in stock has its review's colony_model event carry a "
            + "skill/<pawn>/Medicine value per XP: the tier-2 run built the colonist's skill stocks from the live review frame. A unit "
            + "test cannot prove the native frame carries the skills' learning mechanics to the review.",
        Ticks = TypedCase.StagedTickCap, // a hang guard: the first review lands within the first minute of game time
        Start = new LabStart { Colonists = 1 },
        Crew = new Crew { Size = 1 },
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            World = new Fx.StageWorld { Storyteller = "Cassandra", DaysPassed = 25, OutdoorTemperature = 21, GrowingClimate = true },
            Site = new Fx.StageSite
            {
                Width = 5,
                Height = 3,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 3,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Unfogged],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 5, 3), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones] }],
            // About 375 meals: the 60-day horizon needs more than 30, so the run's hunger is priced.
            Things = [.. Enumerable.Range(0, 5).Select(i => new Fx.StageThing { Name = "meals" + i, Def = "MealSimple", Count = 75, Cell = new(i, 1), Near = true })],
            Pawns = [new Fx.StagePawn { Name = "colonist", Colonist = 0, Skills = new Dictionary<string, int> { ["Medicine"] = 5 }, Needs = new Dictionary<string, float> { ["Food"] = 0.8f, ["Rest"] = 1f } }],
        },
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Serve = new ServeSpec { Families = ["work"], Prefix = "skill-marginal" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        IReadOnlyList<ColonyModelRow> rows = [];
        _ = await s.ServeUntilAsync("skill-marginal", s.TicksLeft, ct =>
        {
            rows = ColonyModelReviews.Of(s);
            return Task.FromResult((string.Create(CultureInfo.InvariantCulture, $"{rows.Count}"), rows.Count > 0));
        }, cancellationToken).ConfigureAwait(false);

        var last = rows.Count == 0 ? null : rows[^1];
        s.Record("colony_model", $"{rows.Count} rows; last: skill_per_xp=[{string.Join(' ', last?.SkillPerXp ?? [])}] findings=[{last?.Findings}]");
        var medicine = rows.SelectMany(r => r.SkillPerXp).FirstOrDefault(p => p.StartsWith("skill/", StringComparison.Ordinal) && p.Contains(Skill, StringComparison.Ordinal));
        if (medicine is null || !double.TryParse(medicine[(medicine.LastIndexOf('=') + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var perXp) || !double.IsFinite(perXp))
        {
            throw new AcceptanceException($"no colony_model event prices a Medicine skill stock in {rows.Count} rows; last findings [{last?.Findings}]");
        }
    }
}
