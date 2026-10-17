namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the ring owes now: the cut cells where an undesignated plant stands and the wooden ruins with no
/// deconstruction designated. The <c>layout/firebreak</c> Need is unmet while any stands;
/// an order is the method, so the Need settles on the census, never on a receipt.
/// </summary>
public sealed record FirebreakWork
{
    public IReadOnlyList<Cell> Cut { get; init; } = [];

    public IReadOnlyList<Cell> Deconstruct { get; init; } = [];

    /// <summary>Reports whether any ring work stands.</summary>
    public bool Owed => Cut.Count + Deconstruct.Count > 0;
}

public static class FirebreakConcernRules
{
    /// <summary>
    /// The ring work plan leaves: a cut cell where standing reports an undesignated
    /// plant, and a planned wooden ruin that open still reports undesignated. Paved cells are the rooms/flooring Need's;
    /// the plan's order is kept.
    /// </summary>
    public static FirebreakWork FirebreakOwed(FirebreakPlan plan, IReadOnlySet<Cell> standing, IReadOnlySet<Cell> open) =>
        new()
        {
            Cut = [.. plan.Cells.Where(c => c.Treatment == FirebreakTreatment.Cut && standing.Contains(c.Cell)).Select(c => c.Cell)],
            Deconstruct = [.. plan.Deconstruct.Where(open.Contains)],
        };
}
