using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The matrix report's provenance: what a rerun would have to match for its numbers to be comparable.</summary>
internal sealed partial class SpeedmatrixRun
{
    // The world (seed, save and its hash, fixture hash) and the installed build's hashes are the report's own "world" and
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
            ("tick_target", Ticks),
            ("stage", OutputJson.Fields(("save", StageSave), ("items", Items))),
            ("warmup", "the reload, the needs freeze and the resume precede the measured interval"),
            ("measured_interval", "per row: wall_seconds from resume to the tick budget"));

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
