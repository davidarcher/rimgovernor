using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// speedmatrix/observations compares the same staged colony at the same clock speed and useful-work budget across
/// governor-off, ordinary governed, concurrent-reader and player runs. Frame and observation distributions separate
/// controller observation cost from simulation work; absent timing samples remain unknown (Go <c>observations.go</c>).
/// </summary>
internal sealed partial class SpeedmatrixRun
{
    // The committed tribal8 colony the workload starts from.
    private const string BaselineSave = "RimGovernor-tribal8-baseline";

    // The workload: the same stage size as the plain matrix (so the useful work is the work that matrix already
    // measures) kept in its own save, and the rows compared.
    internal static SpeedmatrixProfile ObservationsProfile { get; } = new()
    {
        Items = Items,
        Segments = Segments,
        Ticks = Ticks,
        Save = "RimGovernor-observations-stage",
        Speeds = "governor-off,uncapped,observation-load,player",
        Observations = true,
    };

    /// <summary><c>speedmatrix/observations</c>.</summary>
    internal static AcceptanceCase ObservationsCase() => new()
    {
        Name = "speedmatrix/observations",
        Scope = "Observation baseline (#642): the committed tribal8 colony with the throughput stage applied, played rendered "
            + "governor-off, governed and governed under observation load at one clock speed and one tick budget; each row reports the "
            + "companion's observation capture/format split, its per-section costs and its update-interval account beside the "
            + "clock and step phases, with the run's provenance.",
        Start = new FixtureStart
        {
            Op = PrepareTool,
            Args = new JsonObject { ["itemCount"] = Items, ["wallSegments"] = Segments },
            On = new SaveStart { Name = BaselineSave },
        },

        // A windowed launch: update intervals only mean what the issue asks them to mean when the game is drawing.
        Rendered = true,
        Service = true,
        Budget = AcceptanceCase.MaxBudget,
        Crew = new Crew { Size = 3 },
        Matrix = true,
        Run = RunBody(ObservationsProfile),
    };

    // The observation half of a metrics row: the companion's own account of where an observation hop's main-thread time
    // went, and of the update intervals it ran between. The nested blocks carry the full distributions (a Samples of 0
    // inside one means the recording carried none of that phase, which the report shows as unknown, never as zero
    // work); the flat keys beside them are the headline numbers a reader compares across rows.
    private static JsonObject ObservationRow(ObservationSample obs, FrameSample frames) => new()
    {
        ["observation"] = obs.ToJson(),
        ["frames"] = frames.ToJson(),
        ["observation_hops"] = obs.Hops,
        ["capture_ms"] = obs.CaptureMs,
        ["format_ms"] = obs.FormatMs,
        ["capture_p95_ms"] = obs.Capture.P95,
        ["format_p95_ms"] = obs.Format.P95,
        ["queue_p95_ms"] = obs.Queue.P95,
        ["execute_p95_ms"] = obs.Execute.P95,
        ["format_passes"] = obs.FormatPasses,
        ["payload_bytes"] = obs.PayloadBytes,
        ["frame_updates"] = frames.Updates,
        ["frame_updates_per_second"] = frames.UpdatesPerSecond(),
        ["frame_max_interval_ms"] = frames.MaxIntervalMs,

        // Interval tails: all updates, then only those that ran main-thread observation work, so a tail is attributable.
        ["frame_interval_samples"] = frames.Intervals.Samples,
        ["frame_p95_ms"] = frames.Intervals.P95,
        ["frame_p99_ms"] = frames.Intervals.P99,
        ["observed_frame_samples"] = frames.Observed.Samples,
        ["observed_frame_p95_ms"] = frames.Observed.P95,
        ["observed_frame_p99_ms"] = frames.Observed.P99,
        ["observed_frame_max_ms"] = frames.Observed.Max,
        ["encode_queue_p95_ms"] = obs.EncodeQueue.P95,
        ["encode_p95_ms"] = obs.Encode.P95,
        ["frame_observation_share"] = frames.ObservationShare(),
        ["frame_recorder_ms"] = frames.RecorderMs,
        ["frame_cancelled_hops"] = frames.Cancelled,

        // Frame blocking and intentional pauses are separate measures: the clock's own paused_ms/paused_fraction_native
        // on this row is the governor's deliberate stop time, never counted here.
        ["frame_intervals_are"] = "Unity update-to-update wall on a monotonic clock, not GPU presentation intervals",
    };

    // What the baseline was measured on: what a rerun would have to match for the numbers to be comparable. The world
    // (seed, save and its hash, fixture hash) and the installed build's hashes are the report's own "world" and
    // "package_files" blocks; this names the rest.
    private JsonObject Provenance()
    {
        var cfg = _session.Config;
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "darwin" : "other";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            var other => other.ToString().ToLowerInvariant(),
        };
        var output = new JsonObject
        {
            ["revision"] = MetricSeries.SourceRevision(),
            ["headless"] = cfg.Headless,
            ["host"] = new JsonObject
            {
                ["name"] = Environment.MachineName,
                ["os"] = os,
                ["arch"] = arch,
                ["cpus"] = Environment.ProcessorCount,
                ["gomaxprocs"] = Environment.ProcessorCount,
            },
            ["speeds"] = _profile.Speeds,
            ["tick_budget"] = _profile.Ticks,
            ["stage"] = new JsonObject { ["save"] = _profile.Save, ["items"] = _profile.Items, ["wall_segments"] = _profile.Segments },

            // The rows' fixed requirements: the same clock speed and the same useful work, so only the observation load differs.
            ["fixed"] = "every row asks for the same clock speed and the same staged work; rows differ only by the controller and the observation load",
            ["camera"] = "the stage save's own camera position, restored by the reload; no row commands the camera",
            ["warmup"] = "the reload, the needs freeze, the plan submission and the resume precede the measured interval",
            ["measured_interval"] = "per row: wall_seconds from resume to the tick budget; the frames and observation blocks are "
                + "differenced over the service's OTLP recording, which starts when the service attaches",
            ["load"] = "the observation-load row adds concurrent /api/state readers (#656); the other rows hold none",
            ["readers"] = new JsonObject
            {
                ["count"] = SpeedMatrix.ObservationLoadReaders,
                ["interval_ms"] = (long)SpeedMatrix.ObservationLoadInterval.TotalMilliseconds,
            },
        };

        // The mod set the launch activated: the headless profile when the run is headless, the windowed profile otherwise.
        var dir = cfg.Headless ? "headless-profile" : "profile";
        try
        {
            output["mods"] = new JsonArray([.. HeadlessProfile.ActiveMods(Path.Combine(cfg.Root, dir, "Config", "ModsConfig.xml")).Select(mod => (JsonNode?)mod)]);
        }
        catch (Exception e) when (e is AcceptanceException or IOException)
        {
            _ = e;
        }

        try
        {
            // The launch arguments carry the resolution and window mode of a rendered run, and batch mode of a headless one.
            output["launch_args"] = cfg.GameSection()["args"]?.DeepClone();
        }
        catch (Exception e) when (e is AcceptanceException or IOException)
        {
            _ = e;
        }

        return output;
    }

    // Rejects an observation report that cannot support a comparison: every row must carry update-interval samples, and
    // every governed row observation hops, so an absent frame hook or an unaccounted controller reads as a failure
    // rather than as zero cost.
    private static List<string> ObservationRowProblems(List<JsonObject> rows)
    {
        var problems = new List<string>();
        foreach (var row in rows)
        {
            var name = row["case"].AsString();
            if (row["frame_interval_samples"].AsNumber() == 0)
            {
                problems.Add(name + ": no update-interval samples (frame histogram absent)");
            }

            if (row["governor_off"].AsBool() != true && row["observation_hops"].AsNumber() == 0)
            {
                problems.Add(name + ": no observation hops recorded");
            }
        }

        if (rows.Count == 0)
        {
            problems.Add("no rows");
        }

        return problems;
    }
}
