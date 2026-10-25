using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>goals/theme-reform</c> (#3504): the naked-cannibals preset's meme and precept goals, read from the shipped preset book
/// as they resolve over the installed game and seeded in the save's goals blob over a fluid ideoligion with development
/// points, make the served brain apply a first reform that adds one of them, and the precept goals report Pursuing with
/// the reform Need as owner.
/// </summary>
internal static class GoalsThemeReform
{
    private const string Preset = "naked-cannibals";
    private const string PresetTool = "test/theme_preset_read";

    private static readonly FixtureOp<string, Fx.ThemePresetRead> ReadPreset = new(
        PresetTool,
        preset => new JsonObject([new KeyValuePair<string, JsonNode?>("preset", preset)]),
        (label, reply) => reply is not null
            ? Fx.FixtureJson.Deserialize<Fx.ThemePresetRead>(reply.ToJsonString()) is var read && read.Success
                ? read
                : throw new AcceptanceException($"{label}: {PresetTool} refused: {read.Reason}")
            : throw new AcceptanceException($"{label}: {PresetTool} replied nothing"));

    internal static TypedCase Case() => new()
    {
        Name = "goals/theme-reform",
        Scope = "Under the served brain, the naked-cannibals preset's meme and precept goals (read from the shipped preset book) as a theme in "
            + "the save's goals blob over a fluid ideoligion with development points make the brain apply a first reform that adds one of the "
            + "theme's memes or precepts (a native ideoligion read), while the precept goals' goal_review rows report Pursuing with the ideology "
            + "reform Need as owner. A unit test cannot prove the planner against the game's real meme and precept data or the native reform.",
        Ticks = 6_000,
        Tier = CaseTier.OnDemand,
        Expansions = ["ludeon.rimworld.ideology"],
        NoKeep = true,
        QuietWorld = true,
        Ops = [IdeologyReform.PrepareTool, PresetTool],
        Serve = new ServeSpec { Families = ["goals", "ideoligion-reform"], Prefix = "goals-theme-reform" },
        Run = RunAsync,
    };

    private static async Task<RimGovernor.Brain.GameFacts.Observations.IdeologySnapshot?> ReadAsync(TypedSession s, string label, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ReadSnapshot, new RimGovernor.Brain.GameOrders.Observations.SnapshotReadRequest { AfterNumber = 0 }, cancellationToken).ConfigureAwait(false);
        return (reply.Outcome?.Read ?? throw new AcceptanceException($"{label}: snapshot read failed: {reply.Outcome}")).Frame?.Ideology is RimGovernor.Brain.GameFacts.Observations.IdeologyFrame.Installed installed ? installed.Snapshot : null;
    }

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var preset = await s.FixtureAsync("read-preset", ReadPreset, Preset, cancellationToken).ConfigureAwait(false);
        if (preset.Memes.Length == 0 || preset.Precepts.Length == 0)
        {
            throw new AcceptanceException($"the {Preset} preset resolves no meme or precept goal: [{string.Join(' ', preset.Memes)}] / [{string.Join(' ', preset.Precepts)}]");
        }

        s.Record("preset", $"{string.Join(' ', preset.Memes)} / {string.Join(' ', preset.Precepts)}");

        // The staged precondition: the lab's primary ideoligion is fluid with the points for a reform (the fixture does not apply one).
        var prepared = await s.FixtureAsync("prepare-ideoligion", IdeologyReform.Prepare, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        var baseline = IdeologyReform.Decode("prepare-ideoligion", prepared.Expected);
        if (preset.Memes.Any(baseline.Memes.Contains))
        {
            throw new AcceptanceException($"the ideoligion already holds a theme meme: {string.Join(' ', baseline.Memes)}");
        }

        // Goal ids: the memes first, then the precepts; the theme takes the next id.
        var targets = preset.Memes.Select(m => (Kind: GoalKind.Meme, Target: m)).Concat(preset.Precepts.Select(p => (Kind: GoalKind.Precept, Target: p))).ToList();
        var themeId = targets.Count + 1;
        var preceptIds = Enumerable.Range(preset.Memes.Length + 1, preset.Precepts.Length).ToList();
        var world = GoalReviews.Empty(s);
        await GoalReviews.SeedAsync(s, world with
        {
            NextId = themeId + 1,
            Goals = [.. targets.Select((t, i) => new GoalRecord { Id = i + 1, Kind = t.Kind, Target = t.Target, Theme = ThemeMembership.Of(themeId) })],
            Themes = [new ThemeRecord { Id = themeId, Name = "Naked cannibals who worship monkeys", Preset = Preset, Members = [.. Enumerable.Range(1, targets.Count)] }],
        }, cancellationToken).ConfigureAwait(false);

        RimGovernor.Brain.GameFacts.Observations.IdeologySnapshot? reformed = null;
        _ = await s.ServeUntilAsync("theme-reform", s.TicksLeft, async ct =>
        {
            var ideology = await ReadAsync(s, "observe", ct).ConfigureAwait(false);
            var fluid = ideology?.Development as RimGovernor.Brain.GameFacts.Observations.IdeoDevelopment.Fluid;
            var done = fluid is not null && fluid.ReformCount > prepared.Count;
            if (done)
            {
                reformed = ideology;
            }

            return (string.Create(CultureInfo.InvariantCulture, $"{fluid?.ReformCount} {fluid?.Points}"), done);
        }, cancellationToken).ConfigureAwait(false);

        var added = (reformed ?? throw new AcceptanceException("no reform was observed")).Memes.Except(baseline.Memes)
            .Concat(reformed.Precepts.Select(p => p.DefName).Except(baseline.Precepts))
            .ToList();
        s.Record("added", string.Join(' ', added));
        var rows = preceptIds.SelectMany(id => GoalReviews.Of(s, id)).ToList();
        s.Record("precept_rows", string.Join(' ', rows.Select(r => $"{r.Goal}:{r.Verdict}:{r.Owner}")));
        if (!added.Intersect(targets.Select(t => t.Target)).Any())
        {
            throw new AcceptanceException($"the first reform added none of the theme's memes or precepts: [{string.Join(' ', added)}]");
        }

        if (!rows.Any(r => r.Verdict == "pursuing" && r.Owner == IdeologyFamily.Reform.Value))
        {
            throw new AcceptanceException($"no precept goal row reported Pursuing with {IdeologyFamily.Reform.Value} as owner: {string.Join(' ', rows.Select(r => $"{r.Goal}:{r.Verdict}:{r.Owner}"))}");
        }
    }
}
