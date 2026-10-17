using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The colony generator's resolved flags.</summary>
public sealed record ColonyOptions
{
    public string Root { get; init; } = "";

    public string Output { get; init; } = "";

    public string GameId { get; init; } = "rimgovernor-trial";

    /// <summary>The window in game ticks.</summary>
    public int Ticks { get; init; } = ColonyCommand.DefaultTicks;

    /// <summary>The window's wall-clock ceiling: a paused or starved game never reaches <see cref="Ticks"/>.</summary>
    public TimeSpan Watch { get; init; }

    /// <summary>The checkpoint ring's cadence in run time.</summary>
    public TimeSpan Every { get; init; } = Checkpoints.DefaultCheckpointEvery;

    /// <summary>The whole run's hang guard, from the game open to the ring index.</summary>
    public TimeSpan Timeout { get; init; }
}

/// <summary>
/// <c>acceptance colony</c>: the save-fixture generator, outside the case runner and the case catalog. It opens the
/// <see cref="SustainedFoodCase.BaselineSave"/> colony in this process on <c>-root</c> with live needs, a quiet
/// storyteller and a crew of three, serves every routine family for the window (<see cref="SustainedFood.ObserveAsync"/>)
/// and keeps a checkpoint ring of the live game under <c>&lt;root&gt;/checkpoints/sustained/colony</c> whether the window
/// passes or fails. The fixture factory publishes that ring; <c>acceptance fetch-fixture</c> and
/// <c>acceptance profile-capture</c> load its newest bundle. The window fails on a keep-alive resume costing more than one
/// native generation (#259), a colonist past Malnutrition 0.3 (#260) or the stockpile audit; the ring and
/// <c>&lt;output&gt;/result.json</c> are written either way.
/// </summary>
public static class ColonyCommand
{
    /// <summary>The ring's name: the directory under <c>&lt;root&gt;/checkpoints</c> and the bundles' save name.</summary>
    public const string RingName = "sustained/colony";

    /// <summary>The default window: five game days (60000 ticks a day), the shortest stretch on which the foothold goals settle and the maintenance tier gets a turn.</summary>
    public const int DefaultTicks = 5 * 60000;

    /// <summary>The usage lines for the command's help text.</summary>
    public const string Usage = """
          acceptance colony -root <dir> [-output <dir> -ticks 300000 -watch <d> -every 1m -timeout <d> -game <id>]
            serves every routine family on the tribal8 baseline for -ticks game ticks (-watch wall ceiling) and keeps the checkpoint
            ring under <root>/checkpoints/sustained/colony, passed or failed (the fixture factory's bundle); result.json in -output
        """;

    // Load, acquire, stop and reattach on top of the window's wall ceiling.
    private static readonly TimeSpan Overhead = TimeSpan.FromMinutes(7);

    /// <summary>Runs the subcommand with the arguments after <c>colony</c>; returns the exit code (2 refused, 1 a failed window).</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ColonyOptions opts;
        try
        {
            opts = Parse(args);
        }
        catch (UsageException e)
        {
            await stderr.WriteLineAsync(e.Message + "\n" + Usage).ConfigureAwait(false);
            return 2;
        }

        var error = await GenerateAsync(opts, cancellationToken).ConfigureAwait(false);
        await stdout.WriteLineAsync($"colony: ring {RingDir(opts.Root)}, result {Path.Combine(opts.Output, "result.json")}").ConfigureAwait(false);
        if (error is null)
        {
            await stdout.WriteLineAsync("PASS").ConfigureAwait(false);
            return 0;
        }

        await stderr.WriteLineAsync("FAIL: " + error.Message).ConfigureAwait(false);
        return 1;
    }

    /// <summary>Resolves the subcommand's flags; throws <see cref="UsageException"/> on a refused invocation.</summary>
    public static ColonyOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var fs = new FlagSet("colony");
        var root = fs.String("root", "");
        var output = fs.String("output", "");
        var game = fs.String("game", "rimgovernor-trial");
        var ticks = fs.Int("ticks", DefaultTicks);
        var watch = fs.Duration("watch", SustainedFoodCase.Window());
        var every = fs.Duration("every", Checkpoints.DefaultCheckpointEvery);
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

        if (ticks.Value <= 0 || watch.Value <= TimeSpan.Zero || every.Value <= TimeSpan.Zero || timeout.Value < TimeSpan.Zero)
        {
            throw new UsageException("-ticks, -watch and -every must be positive and -timeout not negative");
        }

        return new ColonyOptions
        {
            Root = root.Value,
            Output = Path.GetFullPath(output.Value.Length == 0 ? Path.Combine(root.Value, "acceptance", "colony") : output.Value),
            GameId = game.Value,
            Ticks = ticks.Value,
            Watch = watch.Value,
            Every = every.Value,
            Timeout = timeout.Value > TimeSpan.Zero ? timeout.Value : watch.Value + Overhead,
        };
    }

    /// <summary>The ring directory under <paramref name="root"/>.</summary>
    public static string RingDir(string root) => Path.Combine(root, "checkpoints", RingName.Replace('/', Path.DirectorySeparatorChar));

    // Opens the game, serves the window under the ring, audits, and writes the ring index and result.json; returns the
    // window's failure instead of throwing it.
    private static async Task<AcceptanceException?> GenerateAsync(ColonyOptions opts, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(opts.Output);
        var report = Report.Create(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Colony generator: every routine family on the {SustainedFoodCase.BaselineSave} save under a quiet storyteller for {opts.Ticks} game ticks (wall ceiling {GoDuration.Format(opts.Watch)}), sampling the foothold and first maintenance standards together, with a checkpoint ring every {GoDuration.Format(opts.Every)}."),
            headless: true);
        var cfg = new Config { Root = opts.Root, Output = opts.Output, Headless = true, GameId = opts.GameId };
        cfg.LaunchEnv[HeadlessProfile.StateVariable] = Path.Combine(opts.Output, Checkpoints.CheckpointStoreFile);
        var ringDir = RingDir(opts.Root);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(opts.Timeout);
        AcceptanceException? error = null;
        try
        {
            // Every run is a fresh timeline: the ring holds this window's bundles only.
            Checkpoints.DeleteTree(ringDir);
            var start = new SaveStart { Name = SustainedFoodCase.BaselineSave };
            var session = await Session.OpenAsync(cfg, SessionHost.Default, report, start, QuietMode.Required, [NeedDef.LiveNeeds], timeout.Token).ConfigureAwait(false);
            try
            {
                error = await ServeWindowAsync(opts, cfg, report, session, start, ringDir, timeout.Token).ConfigureAwait(false);
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

    // The window on the opened game: the crew, the ring, the served watch and the audits, then the ring's index.
    private static async Task<AcceptanceException?> ServeWindowAsync(
        ColonyOptions opts,
        Config cfg,
        Report report,
        Session session,
        Start start,
        string ringDir,
        CancellationToken cancellationToken)
    {
        var (colonists, added) = await DebugGame.StaffCrewAsync(session.Harness, 3, cancellationToken).ConfigureAwait(false);
        report["crew"] = new JsonObject { { "declared", 3 }, { "colonists", colonists }, { "added", added } };
        var spec = new ServeSpec
        {
            NativeTimeout = TimeSpan.FromSeconds(15),
            StepStall = TimeSpan.FromSeconds(90),
            Prefix = "sustained-colony",
        };
        var ring = new CheckpointRing(ringDir, RingName, cfg)
        {
            Every = opts.Every,
            Keep = Checkpoints.CheckpointKeep,
            StorePath = Path.Combine(opts.Output, "service.sqlite"),
            SnapshotStore = StoreBackup.SnapshotAsync,
            Output = opts.Output,
            Fingerprint = Fingerprint(start, cfg.Configuration),
            SourceRevision = MetricSeries.SourceRevision(),
            Bridge = () => session.Harness,
            Service = () => ServiceProcess.LatestService(),
            Serve = new JsonObject { { "families", null }, { "env", new JsonArray() }, { "resume", spec.Resume } },
            Prepared = session.Prepared?.DeepClone() as JsonObject,
        };
        report["checkpointing"] = string.Create(CultureInfo.InvariantCulture, $"every {GoDuration.Format(opts.Every)} into {ringDir}");
        ring.Activate();
        AcceptanceException? error = null;
        try
        {
            await SustainedFood.ObserveAsync(
                ObservedSession.Of(session, cfg, report, spec),
                new SustainedObservation
                {
                    Watch = new WatchConfig
                    {
                        Watch = opts.Watch,
                        Window = (ulong)opts.Ticks,
                        Poll = TimeSpan.FromSeconds(10),
                        Concern = FoodRules.FoodSupplyNeed.Value,
                        Extra = ColonyAudits.ColonyConcerns,
                    },
                    Audit = async (h, r, ct) =>
                    {
                        var failure = SustainedFoodCase.Join(
                        [
                            await SustainedFoodCase.CaptureAsync(() =>
                            {
                                ColonyAudits.AuditReacquisitions(ColonyJsonContext.Reacquisitions(r.AuthorityReacquisitions));
                                return Task.CompletedTask;
                            }).ConfigureAwait(false),
                            await SustainedFoodCase.CaptureAsync(() => ColonyAudits.AuditNutritionAsync(h, r, ct)).ConfigureAwait(false),
                            await SustainedFoodCase.CaptureAsync(() => ColonyAudits.AuditStockpilesAsync(h, session.Identity, opts.Output, r, ct)).ConfigureAwait(false),
                        ]);
                        if (failure is not null)
                        {
                            throw failure;
                        }
                    },
                },
                cancellationToken).ConfigureAwait(false);
            StartupRows.Check(opts.Output, headless: true);
        }
        catch (AcceptanceException e)
        {
            error = e;
        }
        finally
        {
            ring.Deactivate();
        }

        await CloseRingAsync(ring, error, report).ConfigureAwait(false);
        return error;
    }

    // What the sidecars stamp: the installed native package, the Start's description and the environment's expansions.
    // An unreadable package leaves the package hash "".
    private static Fingerprint Fingerprint(Start start, string configDir)
    {
        var config = Path.Combine(HeadlessProfile.MustAbs(configDir), "config.json");
        if (!File.Exists(config))
        {
            throw new FileNotFoundException($"{config} does not exist", config);
        }

        var game = HeadlessProfile.GameSection(HeadlessProfile.LoadConfig(config)).WorkingDir ?? "";
        if (game.Length == 0)
        {
            throw new AcceptanceException($"{config}: the game has no workingDir");
        }

        IReadOnlyDictionary<string, string>? files = null;
        try
        {
            files = Harness.StampFiles(game);
        }
        catch (AcceptanceException)
        {
            // The package hash stays "".
        }

        return new Fingerprint(Checkpoints.PackageHash(files), Checkpoints.StartHash(start.Describe()), Expansions.FromEnvironment() ?? []);
    }

    // Writes the ring index whether the window passed or failed (a failed window adds its final bundle, taken here while
    // the game is still open), so the factory always has the window's newest bundle to publish.
    private static async Task CloseRingAsync(CheckpointRing ring, AcceptanceException? error, Report report)
    {
        Checkpoint? failed = null;
        if (error is not null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            failed = await ring.FailAsync(cts.Token).ConfigureAwait(false);
        }

        var index = new Ring
        {
            Case = ring.Case,
            SourceRevision = ring.SourceRevision,
            Failed = failed,
            FailedTick = failed?.Tick ?? 0,
            FailedOffsetMs = error is null ? 0 : (long)ring.Offset().TotalMilliseconds,
            FailedOutput = error is null ? "" : ring.Output,
        };
        foreach (var entry in ring.Entries().Where(e => e.Label != Checkpoints.FailedCheckpoint).OrderBy(e => e.OffsetMs))
        {
            index.Entries.Add(entry);
        }

        // The factory and profile-capture take the newest entry.
        index.Next = index.Entries.Count == 0 ? "" : index.Entries[^1].Label;
        report["checkpoints"] = new JsonArray([.. index.Entries.Select(e => (JsonNode?)JsonValue.Create(e.Label))]);
        var errors = ring.Errors().ToList();
        try
        {
            Directory.CreateDirectory(ring.Dir);
            index.Write(ring.Dir);
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
        {
            errors.Add("write ring: " + e.Message);
        }

        if (errors.Count > 0)
        {
            report["checkpoint_errors"] = new JsonArray([.. errors.Select(e => (JsonNode?)JsonValue.Create(e))]);
        }
    }
}
