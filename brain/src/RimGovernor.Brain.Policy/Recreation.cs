using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// Native building-backed kinds and the native tolerance/bored state for each colonist with a joy need.
/// Vectors follow Kinds. A null census means unavailable, never a complete empty census.
/// </summary>
public sealed record RecreationCensus
{
    public EquatableArray<string> Kinds { get; init; }

    public EquatableArray<JoyTolerance> Pawns { get; init; }

    public EquatableArray<JoyBuildingMethod> Methods { get; init; }
}

public sealed record JoyTolerance
{
    public PawnId Pawn { get; init; }

    public EquatableArray<double> Tolerance { get; init; }

    public EquatableArray<bool> Bored { get; init; }
}

public sealed record JoyBuildingMethod
{
    public string Definition { get; init; } = "";

    public string Kind { get; init; } = "";

    public double PowerW { get; init; }
}

public sealed partial record ComfortObservation
{
    private void ValidateJoy()
    {
        var j = Joy;
        if (j is null)
        {
            return;
        }
        const string Bad = "invalid recreation kind census";
        DomainException.Require(j.Kinds.Count <= 16, Bad);
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in j.Kinds)
        {
            DomainException.Require(PolicyFunctions.FoodId(k) && kinds.Add(k), Bad);
        }
        var available = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Recreation)
        {
            DomainException.Require(kinds.Contains(f.Kind), Bad);
            available.Add(f.Kind);
        }
        DomainException.Require(available.Count == kinds.Count, Bad);
        var pawns = new HashSet<PawnId>();
        foreach (var p in j.Pawns)
        {
            DomainException.Require(People.Contains(p.Pawn) && pawns.Add(p.Pawn) && p.Tolerance.Count == j.Kinds.Count && p.Bored.Count == j.Kinds.Count, Bad);
            foreach (var t in p.Tolerance)
            {
                DomainException.Require(!(double.IsNaN(t) || double.IsInfinity(t) || t < 0 || t > 1), Bad);
            }
        }
        var methods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in j.Methods)
        {
            DomainException.Require(
                PolicyFunctions.FoodId(m.Definition) && methods.Add(m.Definition) && PolicyFunctions.FoodId(m.Kind) && !double.IsNaN(m.PowerW) && !double.IsInfinity(m.PowerW) && m.PowerW >= 0,
                Bad
            );
        }
    }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// A second reachable kind is maintenance for a multi-colonist colony, or for a lone colonist bored with
    /// their only kind. Two kinds cap this slice: tolerance must not cause unbounded construction when all
    /// kinds are bored.
    /// </summary>
    internal static int RecreationVarietyMissing(ComfortObservation v)
    {
        if (v.Joy is null)
        {
            return 0;
        }
        var missing = 0;
        foreach (var p in v.Joy.Pawns)
        {
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in v.Recreation)
            {
                if (f.AccessibleTo.Contains(p.Pawn))
                {
                    kinds.Add(f.Kind);
                }
            }
            var bored = kinds.Count > 0;
            for (var i = 0; i < v.Joy.Kinds.Count; i++)
            {
                if (kinds.Contains(v.Joy.Kinds[i]) && !p.Bored[i])
                {
                    bored = false;
                }
            }
            if (kinds.Count < 2 && (v.Joy.Pawns.Count > 1 || bored))
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
        if (v.Joy is null)
        {
            return ComfortMethod.NoMethod;
        }
        if (v.Joy.Kinds.Count >= 2)
        {
            return ComfortMethod.AccessBlocked;
        }
        // Methods are in the catalog's preference order (JoyBuildings).
        foreach (var m in v.Joy.Methods)
        {
            if (!v.Joy.Kinds.Contains(m.Kind) && available(m))
            {
                return new ComfortMethod(m.Definition);
            }
        }
        return ComfortMethod.AccessBlocked;
    }
}
