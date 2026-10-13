using System.Text;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Commands.CombatCalibrate;

/// <summary>
/// Where the gate's measurements come from: every calibration and held-out lab fixture played in the simulator under each
/// tactic variant over the baseline's seeds, paired with the committed real-game sweeps (the baselines, and the variant
/// sweeps under the calibration directory). Go built them in <c>combatlab.CalibrationInputs</c>, which needs the lab
/// fixtures (<c>combatlab.Build</c>, <c>SimSpec</c>) and the recorded def catalog; the C# lab fixtures belong to the
/// acceptance case slices (#3090, #3093), which register the implementation.
/// </summary>
internal interface ICalibrationInputs
{
    /// <summary>Measures the fixtures; <paramref name="calibrationDir"/> holds the real variant sweeps under <c>variants/</c>.</summary>
    IReadOnlyList<FixtureInput> Measure(string calibrationDir, CancellationToken cancellationToken);
}

/// <summary>
/// The sim-vs-real calibration gate for the combatsim ranged slice (Go: <c>cmd/combatcalibrate</c>, #2844).
/// <code>RimGovernor.cmd combatcalibrate [-record] [-show report.json]</code>
/// Plays the fixtures (see <see cref="ICalibrationInputs"/>), prints the report and exits 1 when the gate fails. -record
/// also writes report.json and report.txt into the calibration directory; a failing report is a valid record. -show prints
/// an already recorded report.json as text, with no game, sim or fixture needed.
/// </summary>
internal static class CombatCalibrateCommand
{
    private const string Usage = "usage: combatcalibrate [-record] [-show report.json]";

    private static readonly string[] CalibrationDir = ["brain", "src", "RimGovernor.Brain.Acceptance.Cases", "CombatLab", "calibration"];

    /// <summary>The fixture measurements provider; null until the acceptance case slices register one.</summary>
    public static ICalibrationInputs? Inputs { get; set; }

    public static Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var flags = Flags.Parse(args, new HashSet<string> { "record" }, new HashSet<string> { "show" });
        if (flags.HelpRequested)
        {
            Log.Info(Usage);
            Log.Info("  -record  write report.json and report.txt into the calibration directory");
            Log.Info("  -show    print a recorded report.json as text and exit with its verdict");
            return Task.FromResult(0);
        }
        if (flags.Positional.Count > 0)
        {
            throw new UsageException(Usage);
        }
        CalibrationReport report;
        var show = flags.String("show");
        var dir = Path.Combine([Repo.Require(Directory.GetCurrentDirectory()), .. CalibrationDir]);
        if (show.Length > 0)
        {
            report = CalibrationReport.Parse(File.ReadAllText(show));
        }
        else
        {
            var inputs = Inputs ?? throw new ToolException(
                "the combat lab fixtures and simulator import are not available in the C# tool host yet (they arrive with the acceptance case slices, #3090 and #3093); use -show to print a recorded report");
            report = CalibrationGate.Evaluate(CalibrationGate.CurrentStamp(), inputs.Measure(dir, ct));
        }
        Console.Out.Write(report.Text());
        if (flags.Bool("record") && show.Length == 0)
        {
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(Path.Combine(dir, "report.json"), report.Marshal() + "\n", utf8);
            File.WriteAllText(Path.Combine(dir, "report.txt"), report.Text(), utf8);
        }
        return Task.FromResult(report.ExitCode());
    }
}
