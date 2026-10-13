namespace RimGovernor.Brain.Policy;

/// <summary>
/// One KnowledgeCategoryDef's research slot (Anomaly): the project the research manager funds from that
/// category's knowledge, "" when the slot is empty. Knowledge that arrives for a category with no project is
/// lost unless the category overflows into another, so an empty slot with a project to fund is a spending need.
/// Categories are independent: a project is funded only by its own category's slot. Advanced knowledge
/// overflows into Basic when its project finishes or is unset (ResearchManager.ApplyKnowledge,
/// KnowledgeCategoryDef overflowCategory), never the other way, so Basic knowledge cannot fund an Advanced
/// project and each slot is filled for its own category alone.
/// </summary>
public sealed record KnowledgeSlot
{
    public string Category { get; init; } = "";

    public ResearchProjectId Current { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The knowledge project to put in the first empty knowledge slot (by category name) that has a project to
    /// fund, or "" when none does. A project qualifies when the native census listed it with no lock reason
    /// (prerequisites finished, not hidden by the entity codex, no other native requirement unmet) and it is
    /// unfinished; the cheapest by apparent cost funds first, ties by name, so the slot finishes and unlocks
    /// the next project soonest. Projects with another category than the slot's never qualify, whatever the
    /// stock of the other category.
    /// </summary>
    public static string KnowledgePick(
        IReadOnlyDictionary<string, ResearchProjectFacts> projects, IReadOnlyList<string> finished, IReadOnlyList<KnowledgeSlot> slots)
    {
        var done = new HashSet<string>(finished, StringComparer.Ordinal);
        var ordered = slots.OrderBy(s => s.Category, StringComparer.Ordinal).ToList();
        foreach (var slot in ordered)
        {
            if (!slot.Current.IsEmpty)
            {
                continue;
            }
            var pick = "";
            var cost = 0.0;
            foreach (var (name, row) in projects)
            {
                if (row.KnowledgeCategory != slot.Category || done.Contains(name) || !row.Census || row.LockReasons.Count != 0)
                {
                    continue;
                }
                if (!row.Hidden.TryGetValue(out var hidden) || hidden)
                {
                    continue;
                }
                if (pick.Length == 0 || row.Cost < cost || (row.Cost == cost && string.CompareOrdinal(name, pick) < 0))
                {
                    pick = name;
                    cost = row.Cost;
                }
            }
            if (pick.Length != 0)
            {
                return pick;
            }
        }
        return "";
    }
}
