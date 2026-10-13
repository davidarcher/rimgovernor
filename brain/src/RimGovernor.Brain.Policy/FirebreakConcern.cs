namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the ring owes now: the cut cells where an undesignated plant stands and the wooden ruins with no
/// deconstruction designated. (Go <c>FirebreakWork</c>.)
/// </summary>
public sealed record FirebreakWork
{
    public IReadOnlyList<Cell> Cut { get; init; } = [];

    public IReadOnlyList<Cell> Deconstruct { get; init; } = [];

    /// <summary>Go <c>Owed</c>: reports whether any ring work stands.</summary>
    public bool Owed => Cut.Count + Deconstruct.Count > 0;
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Keeps the firebreak ring clear: its cut cells free of standing plants and its wooden ruins taken down.
    /// The goal is in deficit while <see cref="FirebreakOwed"/> finds work; an order is the method, so the goal
    /// settles on the census, never on a receipt.
    /// </summary>
    public static readonly ConcernId MaintainFirebreak = new("MaintainFirebreak");

    /// <summary>
    /// Go <c>FirebreakOwed</c>: the ring work plan leaves: a cut cell where standing reports an undesignated
    /// plant, and a planned wooden ruin that open still reports undesignated. Paved cells are MaintainFlooring's;
    /// the plan's order is kept.
    /// </summary>
    public static FirebreakWork FirebreakOwed(FirebreakPlan plan, IReadOnlySet<Cell> standing, IReadOnlySet<Cell> open) =>
        new()
        {
            Cut = [.. plan.Cells.Where(c => c.Treatment == FirebreakTreatment.Cut && standing.Contains(c.Cell)).Select(c => c.Cell)],
            Deconstruct = [.. plan.Deconstruct.Where(open.Contains)],
        };
}
