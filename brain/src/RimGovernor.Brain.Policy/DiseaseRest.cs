namespace RimGovernor.Brain.Policy;

/// <summary>A forecast in game days, not evidence of recovery. A nonpositive rate has no projected crossing (positive infinity).</summary>
public readonly record struct DiseaseProjection(double DaysToLethal, double DaysToImmune)
{
    public bool NeedsRest() => DaysToImmune > 0 && !double.IsPositiveInfinity(DaysToLethal) && DaysToLethal - DaysToImmune < 1;
}

/// <summary>
/// The conditions that triggered rest. Improved rates while resting do not release it: every tracked condition
/// must be immune or absent from a complete census. Missing pawns and unknown health retain the hold.
/// </summary>
public sealed record DiseaseRest(PawnId Pawn, EquatableArray<string> Conditions);

public static partial class PolicyFunctions
{
    public static Fact<DiseaseProjection> ProjectDisease(CareCondition c)
    {
        if (!c.Severity.TryGetValue(out var s) || !c.Immunity.TryGetValue(out var i)
            || !c.SeverityPerDay.TryGetValue(out var sr) || !c.ImmunityPerDay.TryGetValue(out var ir)
            || s < 0 || i < 0 || i > 1)
        {
            return Unknown<DiseaseProjection>();
        }
        foreach (var v in new[] { s, i, sr, ir })
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                return Unknown<DiseaseProjection>();
            }
        }
        static double Days(double value, double rate)
        {
            if (value >= 1)
            {
                return 0;
            }
            if (rate <= 0)
            {
                return double.PositiveInfinity;
            }
            return (1 - value) / rate;
        }
        return Known(new DiseaseProjection(Days(s, sr), Days(i, ir)));
    }

    private static void ValidateDiseaseRest(IReadOnlyList<DiseaseRest> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!FoodId(row.Pawn.Value) || (i > 0 && rows[i - 1].Pawn.CompareTo(row.Pawn) >= 0) || row.Conditions.Count == 0)
            {
                throw new DomainException("invalid disease rest history");
            }
            for (var j = 0; j < row.Conditions.Count; j++)
            {
                var name = row.Conditions[j];
                if (!ValidResource(new Resource(name)) || (j > 0 && string.CompareOrdinal(row.Conditions[j - 1], name) >= 0))
                {
                    throw new DomainException("invalid disease rest condition");
                }
            }
        }
    }

    /// <summary>Reviews the disease holds against a census. Throws on an invalid observation.</summary>
    public static IReadOnlyList<DiseaseRest> ReviewDiseaseRest(Fact<EquatableArray<CarePawn>> observed, IReadOnlyList<DiseaseRest> previous)
    {
        ValidateDiseaseRest(previous);
        var held = new Dictionary<PawnId, HashSet<string>>();
        foreach (var row in previous)
        {
            held[row.Pawn] = new HashSet<string>(row.Conditions, StringComparer.Ordinal);
        }
        if (!observed.TryGetValue(out var pawns))
        {
            return previous.ToList();
        }
        var seen = new HashSet<PawnId>();
        foreach (var pawn in pawns)
        {
            if (!FoodId(pawn.Id.Value) || !seen.Add(pawn.Id))
            {
                throw new DomainException("invalid disease pawn");
            }
            if (!pawn.Dead.TryGetValue(out var dead) || dead || !pawn.Conditions.TryGetValue(out var conditions))
            {
                continue;
            }
            var heldNames = held.GetValueOrDefault(pawn.Id) ?? [];
            var next = new HashSet<string>(StringComparer.Ordinal);
            var unnamed = false;
            foreach (var c in conditions)
            {
                if (!c.DefName.TryGetValue(out var name) || !ValidResource(new Resource(name)))
                {
                    unnamed = true;
                    continue;
                }
                var immuneKnown = c.Immunity.TryGetValue(out var immune);
                if (heldNames.Contains(name) && (!immuneKnown || double.IsNaN(immune) || double.IsInfinity(immune) || immune < 1))
                {
                    next.Add(name);
                }
                if (ProjectDisease(c).TryGetValue(out var projection) && projection.NeedsRest())
                {
                    next.Add(name);
                }
            }
            if (unnamed)
            {
                foreach (var name in heldNames)
                {
                    next.Add(name);
                }
            }
            held[pawn.Id] = next;
        }
        var result = held
            .Where(kv => kv.Value.Count > 0)
            .OrderBy(kv => kv.Key.Value, StringComparer.Ordinal)
            .Select(kv => new DiseaseRest(kv.Key, EquatableArray.From(kv.Value.Order(StringComparer.Ordinal))))
            .ToList();
        ValidateDiseaseRest(result);
        return result;
    }
}
