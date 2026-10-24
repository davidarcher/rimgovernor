namespace RimGovernor.Brain.Domain;

/// <summary>A measured tri-state result: Met, Unmet, or Unclear when the facts do not say. Unclear is the default value.</summary>
public enum Finding
{
    Unclear,
    Unmet,
    Met,
}

/// <summary>
/// A measured situation: Active, Clear, or Unclear. It is a distinct type so a situation cannot be read as Met or
/// Unmet; both reduce to the same tri-state fact. Members mirror <see cref="Finding"/> one for one (same order and
/// wire names).
/// </summary>
public enum Situation
{
    Unclear,

    [WireName("unmet")]
    Active,

    [WireName("met")]
    Clear,
}

public static class FindingExtensions
{
    /// <summary>The situation reading of <paramref name="f"/>: Unmet is Active, Met is Clear.</summary>
    public static Situation Situation(this Finding f) => (Situation)(int)f;

    /// <summary>The tri-state fact under <paramref name="s"/>.</summary>
    public static Finding Finding(this Situation s) => (Finding)(int)s;
}

public static class FindingConstants
{
    /// <summary>
    /// The bot's own colony size target. The colony grows toward it only as fast as policy.JoinerCapacity's population
    /// and food gates allow, so in practice the target rises with the colony's means.
    /// </summary>
    public const int PopulationTarget = 100;
}

public static class WorkProgress
{
    /// <summary>Whether any of the progress values is still open work.</summary>
    public static bool Open(IEnumerable<Progress> progress)
    {
        foreach (var p in progress)
        {
            var v = p.View;
            if (v.Unresolved || v.Stage is Stage.Pending or Stage.Prepared or Stage.Dispatched or Stage.AwaitingObservation)
            {
                return true;
            }
        }
        return false;
    }
}
