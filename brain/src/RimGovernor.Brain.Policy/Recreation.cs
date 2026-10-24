using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

public sealed record JoyBuildingMethod
{
    public string Definition { get; init; } = "";

    public string Kind { get; init; } = "";

    public double PowerW { get; init; }
}

public static partial class RecreationRules
{
    /// <summary>
    /// A second reachable kind is maintenance for a multi-colonist colony, or for a lone colonist bored with
    /// their only kind. Two kinds cap this slice: tolerance must not cause unbounded construction when all
    /// kinds are bored.
    /// </summary>
    internal static int RecreationVarietyMissing(ComfortObservation v)
    {
        var joy = v.Census.Joy;
        var missing = 0;
        foreach (var p in joy.Pawns)
        {
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in v.Census.Recreation)
            {
                if (f.AccessibleTo.Contains(p.Pawn))
                {
                    kinds.Add(f.Kind);
                }
            }
            var bored = kinds.Count > 0;
            for (var i = 0; i < joy.Kinds.Count; i++)
            {
                if (kinds.Contains(joy.Kinds[i]) && !p.Bored[i])
                {
                    bored = false;
                }
            }
            if (kinds.Count < 2 && (joy.Pawns.Count > 1 || bored))
            {
                missing++;
            }
        }
        return missing;
    }

    /// <summary>
    /// Only adds a distinct kind. An already standing but inaccessible second kind is an access blocker, not
    /// permission to duplicate it. Native definitions supply kinds, research availability and power demand.
    /// </summary>
    public static ComfortMethod SelectRecreationVariety(ComfortObservation v, Func<JoyBuildingMethod, bool> available)
    {
        if (v.Census.Joy.Kinds.Count >= 2)
        {
            return ComfortMethod.AccessBlocked;
        }
        // Methods are in the catalog's preference order (JoyBuildings).
        foreach (var m in v.JoyMethods)
        {
            if (!v.Census.Joy.Kinds.Contains(m.Kind) && available(m))
            {
                return new ComfortMethod(m.Definition);
            }
        }
        return ComfortMethod.AccessBlocked;
    }
}
