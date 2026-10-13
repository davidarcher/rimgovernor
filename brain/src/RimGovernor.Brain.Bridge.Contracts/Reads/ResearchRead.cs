using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's <c>ResearchRead</c>: the native research snapshot translated into EnsureResearch's policy shapes, which
/// project (if any) is already current, the finished projects, and every project's facts from the definition
/// catalog with the current projects' native lock reasons. It reviews only the fields EnsureResearch's dispatch
/// vertical consumes. The read itself (<c>ReadResearch</c>, <c>readResearchSnapshot</c>) is wire plumbing and not
/// ported.
/// </summary>
public sealed record ResearchRead
{
    public ObservationContext? Context { get; init; }

    public string CurrentProject { get; init; } = "";

    public IReadOnlyDictionary<string, ResearchProjectFacts> Projects { get; init; } = new Dictionary<string, ResearchProjectFacts>(StringComparer.Ordinal);

    public IReadOnlyList<string> Finished { get; init; } = [];

    public IReadOnlyList<string> Researchers { get; init; } = [];

    /// <summary>Every Anomaly knowledge category's slot, sorted by category; empty when Anomaly is inactive.</summary>
    public IReadOnlyList<KnowledgeSlot> Knowledge { get; init; } = [];
}

public static partial class BridgeFunctions
{
    /// <summary>Go's <c>toProjectIDs</c>: the project ids of the names; empty for none.</summary>
    internal static IReadOnlyList<ResearchProjectId> ToProjectIds(IReadOnlyList<string> names) => [.. names.Select(name => new ResearchProjectId(name))];
}
