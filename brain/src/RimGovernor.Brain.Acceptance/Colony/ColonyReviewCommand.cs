using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The colony review's resolved flags.</summary>
public sealed record ColonyReviewOptions
{
    public string Root { get; init; } = "";

    public string Output { get; init; } = "";

    public string GameId { get; init; } = "rimgovernor-trial";

    /// <summary>The world seed; it picks the biome.</summary>
    public string Seed { get; init; } = ColonyReviewCommand.PinnedSeed;

    /// <summary>The in-game length in days.</summary>
    public int Days { get; init; } = ColonyReviewCommand.SeasonDays;

    /// <summary>The whole run's hang guard, from the game open to result.json.</summary>
    public TimeSpan Timeout { get; init; }
}

/// <summary>
/// <c>acceptance colony-review</c>: the colony review run, outside the case runner and the case catalog. The governor
/// plays a fresh pinned-seed Crashlanded map (three colonists, a 250 map, the storyteller on) for an in-game season
/// while the <see cref="RecorderTool"/> fixture renders an hourly colony screenshot into <c>&lt;output&gt;/review</c>
/// beside the run timeline in <c>&lt;output&gt;/result.json</c> (the colony census and every
/// <see cref="ColonyAudits.ColonyConcerns"/> concern each in-game hour), for <c>colonyreview report</c>. It gates nothing:
/// result.json is written whatever happens, and only a run that could not play exits nonzero.
/// </summary>
public static class ColonyReviewCommand
{
    /// <summary>The native recorder op (scripts/fixtures/ColonyReviewFixture.cs).</summary>
    public const string RecorderTool = "test/colony_review";

    /// <summary>The nightly's fixed world seed: one run per seed, so a night's result compares with the last.</summary>
    public const string PinnedSeed = "review-pinned-1";

    /// <summary>One in-game quadrum, the default run length.</summary>
    public const int SeasonDays = 15;

    /// <summary>The usage lines for the command's help text.</summary>
    public const string Usage = """
          acceptance colony-review -root <dir> [-output <dir> -seed review-pinned-1 -days 15 -timeout <d> -game <id>]
            plays a fresh Crashlanded map (biome by seed) for -days in-game days with the storyteller on, recording an hourly colony
            screenshot into <output>/review and the timeline into <output>/result.json for colonyreview report
        """;

    // The wall-clock allowance per in-game day on a hosted runner, for the watch.
    private static readonly TimeSpan PerDay = TimeSpan.FromMinutes(12);

    // Launch, scenario start and stop on top of the watch.
    private static readonly TimeSpan Overhead = TimeSpan.FromMinutes(10);

    // The starts a run picks from by seed: survivable without a specialised opening, different enough to show layout and
    // food problems.
    private static readonly string[] Biomes = ["TemperateForest", "AridShrubland", "BorealForest", "TropicalRainforest"];

    /// <summary>Runs the subcommand with the arguments after <c>colony-review</c>; returns the exit code (2 refused, 1 a run that did not play).</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ColonyReviewOptions opts;
        try
        {
            opts = Parse(args);
        }
        catch (UsageException e)
        {
            await stderr.WriteLineAsync(e.Message + "\n" + Usage).ConfigureAwait(false);
            return 2;
        }

        var error = await PlayAsync(opts, cancellationToken).ConfigureAwait(false);
        await stdout.WriteLineAsync($"colony-review: result {Path.Combine(opts.Output, "result.json")}").ConfigureAwait(false);
        if (error is null)
        {
            await stdout.WriteLineAsync("PASS").ConfigureAwait(false);
            return 0;
        }

        await stderr.WriteLineAsync("FAIL: " + error.Message).ConfigureAwait(false);
        return 1;
    }

    /// <summary>Resolves the subcommand's flags; throws <see cref="UsageException"/> on a refused invocation.</summary>
    public static ColonyReviewOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var fs = new FlagSet("colony-review");
        var root = fs.String("root", "");
        var output = fs.String("output", "");
        var game = fs.String("game", "rimgovernor-trial");
        var seed = fs.String("seed", PinnedSeed);
        var days = fs.Int("days", SeasonDays);
        var timeout = fs.Duration("timeout", TimeSpan.Zero);
        fs.Parse(args);
        if (fs.Args.Count > 0)
        {
            throw new UsageException("unexpected arguments: [" + string.Join(' ', fs.Args) + "]");
        }

        if (root.Value.Length == 0 || !Path.IsPathFullyQualified(root.Value))
        {
            throw new UsageException("-root <absolute dir> is required");
        }

        if (seed.Value.Length == 0 || days.Value <= 0 || timeout.Value < TimeSpan.Zero)
        {
            throw new UsageException("-seed must be nonempty, -days positive and -timeout not negative");
        }

        return new ColonyReviewOptions
        {
            Root = root.Value,
            Output = Path.GetFullPath(output.Value.Length == 0 ? Path.Combine(root.Value, "acceptance", "colony-review") : output.Value),
            GameId = game.Value,
            Seed = seed.Value,
            Days = days.Value,
            Timeout = timeout.Value > TimeSpan.Zero ? timeout.Value : (PerDay * days.Value) + Overhead,
        };
    }

    /// <summary>The biome <paramref name="seed"/> picks (FNV-1a, 32 bits, over its UTF-8 bytes).</summary>
    public static string Biome(string seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(seed))
        {
            hash ^= b;
            hash *= 16777619u;
        }

        return Biomes[hash % (uint)Biomes.Length];
    }

    /// <summary>The run's start: Crashlanded's three colonists on a 250 map in the seed's biome, saved as colony-review.</summary>
    public static ScenarioStart Start(string seed) => new()
    {
        Spec = new ScenarioSpec
        {
            Scenario = "Crashlanded",
            Count = 3,
            Seed = seed,
            Biome = Biome(seed),
            Difficulty = "Medium",
            WorldTemperature = "Normal",
            Size = new DebugStartSize { MapSize = 250, PlanetCoverage = 0.3 },
            SaveName = "colony-review",
        },
    };

    // Opens a fresh rendered game on the scenario start, serves the season under the recorder and writes result.json;
    // returns the failure instead of throwing it.
    private static async Task<AcceptanceException?> PlayAsync(ColonyReviewOptions opts, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(opts.Output);
        var start = Start(opts.Seed);
        var report = Report.Create(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Review run, not a gate: the governor plays a Crashlanded map (seed {opts.Seed}, {start.Spec.Biome}) for {opts.Days} in-game days under the storyteller while {RecorderTool} records an hourly colony screenshot into <output>/review beside the run timeline."),
            headless: true);
        report["review_dir"] = Path.Combine(opts.Output, "review");

        // What map this was, for colonyreview's header.
        report["review"] = new JsonObject
        {
            { "seed", opts.Seed },
            { "scenario", start.Spec.Scenario },
            { "biome", start.Spec.Biome },
            { "colonists", start.Spec.Count },
            { "days", opts.Days },
        };

        // The headless profile, but the recorder renders through a graphics device: Graphics drops -Headless and always
        // launches a fresh game.
        var cfg = new Config { Root = opts.Root, Output = opts.Output, Headless = true, Graphics = true, GameId = opts.GameId, FixtureOps = [RecorderTool] };
        cfg.LaunchEnv[HeadlessProfile.StateVariable] = Path.Combine(opts.Output, Checkpoints.CheckpointStoreFile);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(opts.Timeout);
        AcceptanceException? error = null;
        try
        {
            var session = await Session.OpenAsync(cfg, SessionHost.Default, report, start, QuietMode.Loud, [NeedDef.LiveNeeds], timeout.Token).ConfigureAwait(false);
            try
            {
                await ServeSeasonAsync(opts, cfg, report, session, timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                if (ServiceProcess.LatestService() is { } running)
                {
                    _ = await running.StopAsync().ConfigureAwait(false);
                }

                session.Close();
            }
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException or JsonException
            || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            error = e as AcceptanceException ?? new AcceptanceException(
                e is OperationCanceledException ? $"the run timed out after {GoDuration.Format(opts.Timeout)} (-timeout)" : e.Message,
                e);
        }

        report["passed"] = error is null;
        if (error is not null)
        {
            report["error"] = error.Message;
        }

        report.Write(opts.Output);
        return error;
    }

    // The season on the opened game: the crew, then the served watch with the recorder started before it and stopped after.
    private static async Task ServeSeasonAsync(ColonyReviewOptions opts, Config cfg, Report report, Session session, CancellationToken cancellationToken)
    {
        var (colonists, added) = await DebugGame.StaffCrewAsync(session.Harness, 3, cancellationToken).ConfigureAwait(false);
        report["crew"] = new JsonObject { { "declared", 3 }, { "colonists", colonists }, { "added", added } };
        var spec = new ServeSpec { NativeTimeout = TimeSpan.FromSeconds(15), StepStall = TimeSpan.FromSeconds(90), Prefix = "colony-review" };
        var dir = Path.Combine(opts.Output, "review");
        await SustainedFood.ObserveAsync(
            ObservedSession.Of(session, cfg, report, spec),
            new SustainedObservation
            {
                Watch = new WatchConfig
                {
                    Watch = PerDay * opts.Days,
                    Window = (ulong)opts.Days * 60000,
                    Poll = TimeSpan.FromSeconds(30),
                    PollTicks = 2500,
                    Concern = PolicyFunctions.FoodSupplyNeed.Value,
                    Extra = ColonyAudits.ColonyConcerns,
                },
                Prepare = async (h, r, ct) =>
                {
                    var status = await h.CallAsync("review-start", RecorderTool, new JsonObject { { "action", "start" }, { "dir", dir } }, ct).ConfigureAwait(false);
                    r["recorder"] = status.DeepClone();
                },
                Audit = async (h, r, ct) =>
                {
                    var status = await h.CallAsync("review-stop", RecorderTool, new JsonObject { { "action", "stop" } }, ct).ConfigureAwait(false);
                    r["recorder"] = status.DeepClone();
                },
            },
            cancellationToken).ConfigureAwait(false);
    }
}
