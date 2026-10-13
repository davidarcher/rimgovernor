using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The combat lab as the sweep command sees it (Go <c>calibration.go</c>, <c>shots.go</c>, <c>metrics_case.go</c>): the
/// tactic variants, the shot dataset builder and where a recorded sweep lands in the source tree. The committed baselines
/// and calibration files live beside this source.
/// </summary>
public sealed class CombatLab : ICombatLab
{
    /// <summary>The baselines directory relative to the repository root.</summary>
    internal const string BaselinesDir = "brain/src/RimGovernor.Brain.Acceptance.Cases/CombatLab/baselines";

    /// <summary>
    /// The calibration directory relative to the repository root: report.json and report.txt (the gate's recorded output)
    /// and variants/, the real-game sweep files <c>&lt;fixture&gt;/&lt;variant&gt;.json</c> of the non-default variants.
    /// </summary>
    internal const string CalibrationDir = "brain/src/RimGovernor.Brain.Acceptance.Cases/CombatLab/calibration";

    /// <summary>The committed shot datasets' directory relative to the repository root.</summary>
    internal const string ShotsDir = "brain/src/RimGovernor.Brain.Acceptance.Cases/CombatLab/calibration/shots";

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

    // The repository root: the nearest directory above the working directory holding .git.
    private static string ModuleRoot() =>
        NativeSourceInputs.FindRepo(Directory.GetCurrentDirectory())
        ?? throw new AcceptanceException("cannot find the source tree (no .git above the working directory) to record the sweep in");
}
