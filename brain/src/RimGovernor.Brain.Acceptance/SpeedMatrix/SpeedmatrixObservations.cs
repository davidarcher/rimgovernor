using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The observations profile compares the same staged colony at the same clock speed and useful-work budget across
/// governor-off, ordinary governed, concurrent-reader and player runs.
/// </summary>
internal sealed partial class SpeedmatrixRun
{
    /// <summary>
    /// The workload: the same stage size as the plain matrix (so the useful work is the work that matrix already
    /// measures) kept in its own save, and the rows compared.
    /// </summary>
    internal static SpeedmatrixProfile ObservationsProfile { get; } = new()
    {
        Items = Items,
        Segments = Segments,
        Ticks = Ticks,
        Save = "RimGovernor-observations-stage",
        Speeds = "governor-off,uncapped,observation-load,player",
    };

    // What the baseline was measured on: what a rerun would have to match for the numbers to be comparable. The world
    // (seed, save and its hash, fixture hash) and the installed build's hashes are the report's own "world" and
    // "package_files" blocks; this names the rest.
    private JsonObject Provenance()
    {
        var cfg = _config;
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "darwin" : "other";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            var other => other.ToString().ToLowerInvariant(),
        };
        var output = OutputJson.Fields(
            ("revision", MetricSeries.SourceRevision()),
            ("headless", cfg.Headless),
            ("host", OutputJson.Fields(
                ("name", Environment.MachineName),
                ("os", os),
                ("arch", arch),
                ("cpus", Environment.ProcessorCount),
                ("gomaxprocs", Environment.ProcessorCount))),
            ("speeds", _profile.Speeds),
            ("tick_target", _profile.Ticks),
            ("stage", OutputJson.Fields(("save", _profile.Save), ("items", _profile.Items), ("wall_segments", _profile.Segments))),

            // The rows' fixed requirements: the same clock speed and the same useful work, so only the observation load differs.
            ("fixed", "every row asks for the same clock speed and the same staged work; rows differ only by the controller and the observation load"),
            ("camera", "the stage save's own camera position, restored by the reload; no row commands the camera"),
            ("warmup", "the reload, the needs freeze, the plan submission and the resume precede the measured interval"),
            ("measured_interval", "per row: wall_seconds from resume to the tick budget"),
            ("load", "the observation-load row adds concurrent /api/state readers (#656); the other rows hold none"),
            ("readers", OutputJson.Fields(
                ("count", SpeedMatrix.ObservationLoadReaders),
                ("interval_ms", (long)SpeedMatrix.ObservationLoadInterval.TotalMilliseconds))));

        // The mod set the launch activated: the headless profile when the run is headless, the windowed profile otherwise.
        var dir = cfg.Headless ? "headless-profile" : "profile";
        try
        {
            output.Add("mods", OutputJson.Array(HeadlessProfile.ActiveMods(Path.Combine(cfg.Root, dir, "Config", "ModsConfig.xml"))));
        }
        catch (Exception e) when (e is AcceptanceException or IOException)
        {
            _ = e;
        }

        try
        {
            // The launch arguments carry the resolution and window mode of a rendered run, and batch mode of a headless one.
            output.Add("launch_args", cfg.GameSection().Args is { } args ? OutputJson.Array(args) : null);
        }
        catch (Exception e) when (e is AcceptanceException or IOException or JsonException)
        {
            _ = e;
        }

        return output;
    }
}
