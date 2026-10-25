namespace RimGovernor.Brain.Runtime;

/// <summary>
/// Prevents two planners running concurrently within one scheduler step from both claiming the same pawn or the
/// same non-pawn resource (a bench, an item slot, ...). It is scoped to exactly one step: fresh at the top,
/// discarded at the end, never shared across ticks.
/// </summary>
public sealed class StepArbiter
{
    private readonly Lock _mu = new();

    internal Lock Mu => _mu;

    internal HashSet<PawnId> Pawns { get; } = [];

    /// <summary>Namespaced entities, e.g. "haul-item:id" or "bench:id".</summary>
    internal HashSet<string> Resources { get; } = new(StringComparer.Ordinal);

    /// <summary>Reserves every given pawn and resource atomically, or none and false if any is already claimed this step.</summary>
    public bool TryClaim(IReadOnlyList<PawnId> pawns, params string[] resources)
    {
        lock (_mu)
        {
            if (pawns.Any(Pawns.Contains) || resources.Any(Resources.Contains))
            {
                return false;
            }
            foreach (var p in pawns)
            {
                Pawns.Add(p);
            }
            foreach (var r in resources)
            {
                Resources.Add(r);
            }
            return true;
        }
    }
}
