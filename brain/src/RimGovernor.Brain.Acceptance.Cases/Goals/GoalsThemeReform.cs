using System.Globalization;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>goals/theme-reform</c> (#3504): a theme that follows the naked-cannibals preset, seeded in the save's goals blob over a
/// fluid ideoligion with development points, makes the served brain apply a first reform that adds the Nudism meme or a
/// nudity precept, and the precept goals report Pursuing with the reform Need as owner.
/// </summary>
internal static class GoalsThemeReform
{
    private const string Meme = "Nudism";
    private static readonly string[] Precepts = ["Nudity_Male_NoRules", "Nudity_Female_NoRules"];

    private const int MemeGoal = 1;
    private const int ThemeId = 4;

    internal static TypedCase Case() => new()
    {
        Name = "goals/theme-reform",
        Scope = "Under the served brain, a naked-cannibals theme in the save's goals blob over a fluid ideoligion with development "
            + "points makes the brain apply a first reform that adds the Nudism meme or a nudity precept (a native ideoligion read), "
            + "while the precept goals' goal_review rows report Pursuing with the ideology reform Need as owner. A unit test "
            + "cannot prove the planner against the game's real meme and precept data or the native reform.",
        Ticks = 6_000,
        Tier = CaseTier.OnDemand,
        Expansions = ["ludeon.rimworld.ideology"],
        NoKeep = true,
        QuietWorld = true,
        Ops = [IdeologyReform.PrepareTool],
        Serve = new ServeSpec { Families = ["goals", "ideoligion-reform"], Prefix = "goals-theme-reform" },
        Run = RunAsync,
    };

    private static async Task<Observations.IdeologySnapshot?> ReadAsync(TypedSession s, string label, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ReadSnapshot, new Observations.SnapshotReadRequest { AfterNumber = 0 }, cancellationToken).ConfigureAwait(false);
        return (reply.Outcome?.Read ?? throw new AcceptanceException($"{label}: snapshot read failed: {reply.Outcome}")).Frame?.Ideology;
    }

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        // The staged precondition: the lab's primary ideoligion is fluid with the points for a reform (the fixture does not apply one).
        var prepared = await s.FixtureAsync("prepare-ideoligion", IdeologyReform.Prepare, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        var baseline = IdeologyReform.Decode("prepare-ideoligion", prepared.Expected);
        if (baseline.Memes.Contains(Meme) || Precepts.Any(baseline.Precepts.Contains))
        {
            throw new AcceptanceException($"the ideoligion already holds the theme's meme or precept: {string.Join(' ', baseline.Memes)} / {string.Join(' ', baseline.Precepts)}");
        }

        var world = GoalReviews.Empty(s);
        GoalRecord Member(GoalKind kind, string target, int id) => new() { Id = id, Kind = kind, Target = target, Theme = ThemeId };
        await GoalReviews.SeedAsync(s, world with
        {
            NextId = ThemeId + 1,
            Goals = [Member(GoalKind.Meme, Meme, MemeGoal), Member(GoalKind.Precept, Precepts[0], 2), Member(GoalKind.Precept, Precepts[1], 3)],
            Themes = [new ThemeRecord { Id = ThemeId, Name = "Naked cannibals who worship insects", Preset = "naked-cannibals", Members = [1, 2, 3] }],
        }, cancellationToken).ConfigureAwait(false);

        Observations.IdeologySnapshot? reformed = null;
        _ = await s.ServeUntilAsync("theme-reform", s.TicksLeft, async ct =>
        {
            var ideology = await ReadAsync(s, "observe", ct).ConfigureAwait(false);
            var done = ideology is { ReformCount: { } count } && count > prepared.Count;
            if (done)
            {
                reformed = ideology;
            }

            return (string.Create(CultureInfo.InvariantCulture, $"{ideology?.ReformCount} {ideology?.DevelopmentPoints}"), done);
        }, cancellationToken).ConfigureAwait(false);

        var added = (reformed ?? throw new AcceptanceException("no reform was observed")).Memes.Except(baseline.Memes)
            .Concat(reformed.Precepts.Select(p => p.DefName ?? "").Except(baseline.Precepts))
            .ToList();
        s.Record("added", string.Join(' ', added));
        var rows = Enumerable.Range(2, 2).SelectMany(id => GoalReviews.Of(s, id)).ToList();
        s.Record("precept_rows", string.Join(' ', rows.Select(r => $"{r.Goal}:{r.Verdict}:{r.Owner}")));
        if (!added.Contains(Meme) && !added.Intersect(Precepts).Any())
        {
            throw new AcceptanceException($"the first reform added neither {Meme} nor a nudity precept: [{string.Join(' ', added)}]");
        }

        if (!rows.Any(r => r.Verdict == "pursuing" && r.Owner == IdeologyFamily.Reform.Value))
        {
            throw new AcceptanceException($"no precept goal row reported Pursuing with {IdeologyFamily.Reform.Value} as owner: {string.Join(' ', rows.Select(r => $"{r.Goal}:{r.Verdict}:{r.Owner}"))}");
        }
    }
}
