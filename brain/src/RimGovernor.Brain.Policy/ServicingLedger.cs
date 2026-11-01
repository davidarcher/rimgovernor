using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the servicing choices of one review hold of the colony's stock (ColonyReading.Servicing; one per reading, nothing is
/// remembered between reviews): the component a breakdown fix consumes, the fuel a refuel carries and the pawns a repair, fix or
/// refuel sends. Home upkeep, power restoration and defense rearm each call <see cref="ServicingChoiceRules.Decide"/> on the
/// same stock, so a component or fuel item one of them chose for its target is not counted again for another's.
/// Servicing underway holds against every target. A chosen alternative is held against the targets decided after it, keyed by its
/// target and ordered by first decision, so deciding the same target again replaces its hold instead of taking it twice.
/// </summary>
public sealed class ServicingLedger
{
    private readonly Dictionary<(string Target, ServicingKind Kind, string Pawn), ServicingUnderway> _underway = [];
    private readonly Dictionary<string, Hold> _chosen = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    private sealed record Hold(string Pawn, bool Component, string Fuel, int Items);

    /// <summary>Records the servicing underway; an issued fix holds its component, an issued refuel its carried items.</summary>
    internal void Underway(IEnumerable<ServicingUnderway> rows)
    {
        foreach (var u in rows) _underway[(u.Target, u.Kind, u.Pawn)] = u;
    }

    /// <summary>Registers <paramref name="target"/> in decision order; a target already decided keeps its place.</summary>
    internal void Begin(string target)
    {
        if (!_order.Contains(target, StringComparer.Ordinal)) _order.Add(target);
    }

    /// <summary>
    /// Holds what the alternative chosen for <paramref name="target"/> takes, replacing its earlier hold; null (servicing underway
    /// continues, which the underway rows already hold) or a choice that takes nothing releases it.
    /// </summary>
    internal void Take(string target, ServicingAlternative? chosen)
    {
        if (chosen is null)
        {
            _chosen.Remove(target);
            return;
        }
        var pawn = chosen.Kind is ServicingChoiceKind.Repair or ServicingChoiceKind.FixBreakdown or ServicingChoiceKind.Refuel ? chosen.Pawn : "";
        var refuel = chosen.Work as RefuelWork;
        var hold = new Hold(pawn, chosen.Kind == ServicingChoiceKind.FixBreakdown, refuel?.Fuel.Value ?? "", refuel?.Items ?? 0);
        if (hold.Pawn.Length == 0 && !hold.Component && hold.Items == 0) _chosen.Remove(target);
        else _chosen[target] = hold;
    }

    internal bool PawnHeld(string pawn, string target) =>
        _underway.Values.Any(u => u.Pawn == pawn) || Earlier(target).Any(h => h.Pawn == pawn);

    internal int ComponentsHeld(string target) =>
        _underway.Values.Count(u => u.Issued && u.Kind == ServicingKind.FixBreakdown) + Earlier(target).Count(h => h.Component);

    internal int FuelHeld(string fuel, string target) =>
        _underway.Values.Where(u => u.Issued && u.Kind == ServicingKind.Refuel && u.Fuel == fuel).Sum(u => u.Items)
        + Earlier(target).Where(h => h.Fuel == fuel).Sum(h => h.Items);

    private IEnumerable<Hold> Earlier(string target)
    {
        var at = _order.FindIndex(t => t == target);
        for (var i = 0; i < (at < 0 ? _order.Count : at); i++)
        {
            if (_chosen.TryGetValue(_order[i], out var hold)) yield return hold;
        }
    }
}
