using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The combat lab as the sweep command sees it (Go <c>calibration.go</c>, <c>shots.go</c>, <c>metrics_case.go</c>): the
/// tactic variants, the shot dataset builder and where a recorded sweep lands in the source tree. The committed baselines
/// and calibration files stay in the Go module's tree until the #3085 cutover moves them.
/// </summary>
public sealed class CombatLab : ICombatLab
{
    /// <summary>The baselines directory relative to the Go module root.</summary>
    internal const string BaselinesDir = "internal/nativeaccept/cases/combatlab/baselines";

    /// <summary>
    /// The calibration directory relative to the Go module root: report.json and report.txt (the gate's recorded output)
    /// and variants/, the real-game sweep files <c>&lt;fixture&gt;/&lt;variant&gt;.json</c> of the non-default variants.
    /// </summary>
    internal const string CalibrationDir = "internal/nativeaccept/cases/combatlab/calibration";

    /// <summary>The committed shot datasets' directory relative to the Go module root.</summary>
    internal const string ShotsDir = "internal/nativeaccept/cases/combatlab/calibration/shots";

    /// <summary>
    /// The ranged slice's tactic variants: the two tuning knobs the ranged fallback and raid grouping read, each moved
    /// both ways (Go <c>combatlab.Variants</c>).
    /// </summary>
    public IReadOnlyList<CombatVariant> Variants()
    {
        var shipped = PolicyFunctions.DefaultCombatTuning();
        return
        [
            new CombatVariant(SweepCommand.DefaultVariant, shipped),
            new CombatVariant("scatter-short", shipped with { ScatterCells = 8 }),
            new CombatVariant("scatter-long", shipped with { ScatterCells = 45 }),
            new CombatVariant("group-gap-tight", shipped with { GroupGap = 3 }),
            new CombatVariant("group-gap-wide", shipped with { GroupGap = 20 }),
        ];
    }

    /// <inheritdoc />
    public void BuildShotDataset(Stream output, IReadOnlyList<int> seeds, Func<int, string> caseDir) =>
        CombatLabShots.BuildShotDatasetAsync(output, seeds, caseDir, CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc />
    public string RecordPath(string fixture, string variant)
    {
        var root = ModuleRoot();
        return variant.Length == 0
            ? Path.Combine(root, BaselinesDir, fixture + ".json")
            : Path.Combine(root, CalibrationDir, "variants", fixture, variant + ".json");
    }

    // The Go module root: the nearest directory above the working directory holding go.mod, or a go/ child that does.
    private static string ModuleRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "go.mod")))
            {
                return dir.FullName;
            }

            var child = Path.Combine(dir.FullName, "go");
            if (File.Exists(Path.Combine(child, "go.mod")))
            {
                return child;
            }
        }

        throw new AcceptanceException("cannot find the source tree (no go.mod above the working directory) to record the sweep in");
    }
}
