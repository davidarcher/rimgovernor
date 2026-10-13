using System.Reflection;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One ThoughtDef as the mood ledger reads it: the bridge projects the catalog row and the audit table into it,
/// so policy never sees the catalog. Immunity is data, not a verdict: the consumer matches the pawn's traits and
/// precepts.
/// </summary>
public sealed record ThoughtFacts
{
    public string Def { get; init; } = "";

    /// <summary>The lowest BaseMoodEffect of the def's stages (the most negative, or the least positive when none is negative); 0 without stages.</summary>
    public double WorstOffset { get; init; }

    /// <summary>The def's stages by index, null ones not Present: the stage a worker picks (a room's impressiveness stage) indexes them (#2954).</summary>
    public IReadOnlyList<MoodStage> Stages { get; init; } = [];

    public IReadOnlyList<string> NullifyingTraits { get; init; } = [];

    public IReadOnlyList<string> NullifyingPrecepts { get; init; } = [];

    /// <summary>Traits a pawn must have for the thought to apply.</summary>
    public IReadOnlyList<string> RequiredTraits { get; init; } = [];

    /// <summary>The lowest expectation that grants the thought, empty when it applies at any.</summary>
    public string MinExpectation { get; init; } = "";

    /// <summary>The audit table's ';'-joined state tokens, or <see cref="PolicyFunctions.ThoughtUnclassified"/>.</summary>
    public string Dependency { get; init; } = "";

    /// <summary>The table's hand-kept concern, empty when no concern removes the thought.</summary>
    public string Owner { get; init; } = "";
}

/// <summary>One row of thought_triggers.tsv.</summary>
public sealed record ThoughtAudit(string Dependency, string Owner);

public static partial class PolicyFunctions
{
    /// <summary>The dependency of a def the audit table has no row for (a modded thought, a def new since the last regeneration, or a name that is not a ThoughtDef).</summary>
    public const string ThoughtUnclassified = "unclassified";

    /// <summary>The parsed table, built once on first use from the embedded copy of thought_triggers.tsv.</summary>
    private static readonly Lazy<IReadOnlyDictionary<string, ThoughtAudit>> ThoughtAuditRows = new(LoadThoughtAuditRows);

    private static Dictionary<string, ThoughtAudit> LoadThoughtAuditRows()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("thought_triggers.tsv")
            ?? throw new DomainException("thought_triggers.tsv is not embedded in the policy assembly");
        using var reader = new StreamReader(stream);
        var rows = new Dictionary<string, ThoughtAudit>(StringComparer.Ordinal);
        var header = true;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (header)
            {
                header = false;
                continue;
            }
            var f = line.TrimEnd('\r').Split('\t');
            if (f.Length == 5)
            {
                rows[f[0]] = new ThoughtAudit(f[3], f[4]);
            }
        }
        return rows;
    }

    /// <summary>
    /// The generated thought-to-trigger row of the def, false when the table has none (go run ./cmd/thoughtaudit
    /// regenerates the table; see docs/developers/contracts/mood-control.md).
    /// </summary>
    public static (ThoughtAudit Row, bool Ok) ThoughtAuditRow(string def) =>
        ThoughtAuditRows.Value.TryGetValue(def, out var row) ? (row, true) : (new ThoughtAudit("", ""), false);
}
