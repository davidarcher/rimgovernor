namespace RimGovernor.Brain.Policy;

/// <summary>
/// One KnowledgeCategoryDef's research slot (Anomaly): the project the research manager funds from that
/// category's knowledge, "" when the slot is empty. Knowledge that arrives for a category with no project goes
/// to its overflow category (KnowledgeCategoryDef.overflowCategory, <see cref="Overflow"/>) and is lost when it
/// has none, so an empty slot with a project to fund is a spending need. Advanced knowledge overflows into Basic
/// when its project finishes or is unset (ResearchManager.ApplyKnowledge), never the other way, so Basic
/// knowledge cannot fund an Advanced project and each slot is filled for its own category alone.
/// </summary>
public sealed record KnowledgeSlot
{
    public required RimGovernor.Brain.GameFacts.Observations.ResearchSlot Observed { get; init; }

    public string Category => Observed.Category;

    public ResearchProjectId Current => new(Observed.CurrentProject);

    /// <summary>The category's KnowledgeCategoryDef.overflowCategory from the definition catalog; empty when it has none.</summary>
    public string Overflow { get; init; } = "";
}

public static partial class ResearchRules
{
    /// <summary>
    /// The knowledge project to put in the first empty knowledge slot (by category name) that has a project to
    /// fund (<see cref="KnowledgeFunding"/>), or "" when none does.
    /// </summary>
    public static string KnowledgePick(
        IReadOnlyDictionary<string, ResearchProjectFacts> projects, IReadOnlyList<string> finished, IReadOnlyList<KnowledgeSlot> slots)
    {
        var done = new HashSet<string>(finished, StringComparer.Ordinal);
        foreach (var slot in slots.OrderBy(s => s.Category, StringComparer.Ordinal))
        {
            if (slot.Current.IsEmpty && KnowledgeFunding(projects, done, slot.Category) is { Length: > 0 } pick)
            {
                return pick;
            }
        }
        return "";
    }

    /// <summary>
    /// The project an empty slot of <paramref name="category"/> funds, or "" when none qualifies. A project qualifies
    /// when the native census listed it with no lock reason (prerequisites finished, not hidden by the entity codex, no
    /// other native requirement unmet) and it is unfinished; the cheapest by apparent cost funds first, ties by name, so
    /// the slot finishes and unlocks the next project soonest. Projects with another category never qualify, whatever
    /// the stock of the other category.
    /// </summary>
    public static string KnowledgeFunding(IReadOnlyDictionary<string, ResearchProjectFacts> projects, IReadOnlySet<string> finished, string category)
    {
        var pick = "";
        var cost = 0.0;
        foreach (var (name, row) in projects)
        {
            if (row.KnowledgeCategory != category || finished.Contains(name) || !row.Census || row.LockReasons.Count != 0 || row.Hidden)
            {
                continue;
            }
            if (pick.Length == 0 || row.Cost < cost || (row.Cost == cost && string.CompareOrdinal(name, pick) < 0))
            {
                pick = name;
                cost = row.Cost;
            }
        }
        return pick;
    }
}
