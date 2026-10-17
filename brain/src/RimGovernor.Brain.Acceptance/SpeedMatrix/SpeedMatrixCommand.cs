using System.Text.Json;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The speed matrix's resolved flags.</summary>
public sealed record SpeedMatrixOptions
{
    public string Root { get; init; } = "";

    public string Output { get; init; } = "";

    public string GameId { get; init; } = "rimgovernor-trial";

    /// <summary><see cref="SpeedMatrixCommand.Plain"/> or <see cref="SpeedMatrixCommand.Observations"/>.</summary>
    public string Profile { get; init; } = SpeedMatrixCommand.Plain;

    /// <summary>The rows to run (<see cref="SpeedMatrix.ParseSpeedCases"/>); empty runs the profile's.</summary>
    public string Speeds { get; init; } = "";

    /// <summary>The whole run's hang guard, from the game open to result.json.</summary>
    public TimeSpan Timeout { get; init; } = SpeedMatrixCommand.DefaultTimeout;
}

/// <summary>
/// <c>acceptance speedmatrix</c>: the clock-speed measurement (#111, #642), outside the case runner and the case catalog.
/// It opens the throughput fixture's stage (<see cref="SpeedmatrixRun.PrepareTool"/>) with a crew of three, saves it,
/// and per row reloads it, freezes needs and plays it to the same tick target at the row's clock speed, governed by a
/// fresh in-game service or with none (governor-off). Each row writes its own report under <c>&lt;output&gt;/&lt;row&gt;</c>;
/// <c>&lt;output&gt;/result.json</c> carries the speed metrics, the outcome comparison and the run's provenance. The
/// <see cref="Plain"/> profile is the whole speed sweep on a quiet flat headless debug colony; <see cref="Observations"/>
/// is the rendered observation baseline on the tribal8 colony (governor-off, uncapped, observation-load, player).
/// </summary>
public static class SpeedMatrixCommand
{
    /// <summary>The whole speed sweep on a quiet flat debug colony.</summary>
    public const string Plain = "plain";

    /// <summary>The rendered observation baseline on the tribal8 colony.</summary>
    public const string Observations = "observations";

    /// <summary>The default hang guard: the case budget the matrix ran under.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);

    /// <summary>The usage lines for the command's help text.</summary>
    public const string Usage = """
          acceptance speedmatrix -root <dir> [-profile plain|observations -speeds <rows> -output <dir> -timeout 15m -game <id>]
            plays the throughput stage to one tick target per clock speed row (-speeds narrows them, e.g. uncapped,governor-off) and
            compares the outcomes; per-row reports and result.json in -output (default <root>/acceptance/speedmatrix-<profile>)
        """;

    /// <summary>Runs the subcommand with the arguments after <c>speedmatrix</c>; returns the exit code (2 refused, 1 a failed matrix).</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        SpeedMatrixOptions opts;
        try
        {
            opts = Parse(args);
        }
        catch (UsageException e)
        {
            await stderr.WriteLineAsync(e.Message + "\n" + Usage).ConfigureAwait(false);
            return 2;
        }

        var error = await MeasureAsync(opts, cancellationToken).ConfigureAwait(false);
        await stdout.WriteLineAsync($"speedmatrix: result {Path.Combine(opts.Output, "result.json")}").ConfigureAwait(false);
        if (error is null)
        {
            await stdout.WriteLineAsync("PASS").ConfigureAwait(false);
            return 0;
        }

        await stderr.WriteLineAsync("FAIL: " + error.Message).ConfigureAwait(false);
        return 1;
    }

    /// <summary>Resolves the subcommand's flags; throws <see cref="UsageException"/> on a refused invocation.</summary>
    public static SpeedMatrixOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var fs = new FlagSet("speedmatrix");
        var root = fs.String("root", "");
        var output = fs.String("output", "");
        var game = fs.String("game", "rimgovernor-trial");
        var profile = fs.String("profile", Plain);
        var speeds = fs.String("speeds", "");
        var timeout = fs.Duration("timeout", DefaultTimeout);
        fs.Parse(args);
        if (fs.Args.Count > 0)
        {
            throw new UsageException("unexpected arguments: [" + string.Join(' ', fs.Args) + "]");
        }

        if (root.Value.Length == 0 || !Path.IsPathFullyQualified(root.Value))
        {
            throw new UsageException("-root <absolute dir> is required");
        }

        if (profile.Value is not (Plain or Observations))
        {
            throw new UsageException($"-profile must be {Plain} or {Observations}");
        }

        if (timeout.Value <= TimeSpan.Zero)
        {
            throw new UsageException("-timeout must be positive");
        }

        if (speeds.Value.Length > 0)
        {
            try
            {
                _ = SpeedMatrix.ParseSpeedCases(speeds.Value);
            }
            catch (AcceptanceException e)
            {
                throw new UsageException("-speeds: " + e.Message);
            }
        }

        return new SpeedMatrixOptions
        {
            Root = root.Value,
            Output = Path.GetFullPath(output.Value.Length == 0 ? Path.Combine(root.Value, "acceptance", "speedmatrix-" + profile.Value) : output.Value),
            GameId = game.Value,
            Profile = profile.Value,
            Speeds = speeds.Value,
            Timeout = timeout.Value,
        };
    }

    // The throughput fixture's stage on the profile's colony: a flat debug map, or the committed tribal8 baseline.
    private static FixtureStart Start(string profile) => new()
    {
        Op = SpeedmatrixRun.PrepareTool,
        Args = FixtureRecordJson.Args(new Fx.ThroughputPrepareRequest { ItemCount = SpeedmatrixRun.Items, WallSegments = SpeedmatrixRun.Segments }),
        On = profile == Observations ? new SaveStart { Name = Baselines.BaselineName } : new DebugStart { Size = new DebugStartSize { Flat = true } },
    };

    // Opens the game on the stage, runs the matrix and writes result.json; returns the failure instead of throwing it.
    private static async Task<AcceptanceException?> MeasureAsync(SpeedMatrixOptions opts, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(opts.Output);
        var observations = opts.Profile == Observations;
        var profile = observations ? SpeedmatrixRun.ObservationsProfile : SpeedmatrixRun.PlainProfile;
        var report = Report.Create(
            observations
                ? "Observation baseline (#642): the committed tribal8 colony with the throughput stage applied, played rendered governor-off, governed and governed under observation load at one clock speed and one tick budget; each row reports the clock, step and hold phases, with the run's provenance."
                : "Speed matrix (#111): one staged colony reloaded per clock speed under the in-game service with an identical tick target; wall TPS, paused fraction, steps, reads/step, hold counts by reason, resume latency and effective speed per speed; pawn outcomes equal within a tolerance.",
            headless: !observations);
        var cfg = new Config
        {
            Root = opts.Root,
            Output = opts.Output,

            // Update intervals only mean what #642 asks of them when the game is drawing, so the observations profile is
            // windowed; the plain stage is hauling and wall building inside the home area, so its wild map is quiet.
            Headless = !observations,
            QuietWorld = !observations,
            GameId = opts.GameId,
        };
        cfg.LaunchEnv[HeadlessProfile.StateVariable] = Path.Combine(opts.Output, Checkpoints.CheckpointStoreFile);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(opts.Timeout);
        AcceptanceException? error = null;
        try
        {
            var session = await Session.OpenAsync(cfg, SessionHost.Default, report, Start(opts.Profile), QuietMode.Required, [], timeout.Token).ConfigureAwait(false);
            try
            {
                var (colonists, added) = await DebugGame.StaffCrewAsync(session.Harness, 3, timeout.Token).ConfigureAwait(false);
                report["crew"] = OutputJson.Fields(("declared", 3), ("colonists", colonists), ("added", added));
                await SpeedmatrixRun.RunAsync(session, cfg, report, profile, opts.Speeds, timeout.Token).ConfigureAwait(false);
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
}
