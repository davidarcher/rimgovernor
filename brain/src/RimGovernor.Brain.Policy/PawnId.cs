namespace RimGovernor.Brain.Policy;

/// <summary>
/// Go <c>policy.PawnID</c>: a pawn's id as the policy rules see it. Distinct from <see cref="Domain.PawnId"/>;
/// the explicit casts are Go's <c>domain.PawnID(id)</c> / <c>PawnID(id)</c> conversions. Orders by ordinal bytes.
/// </summary>
public readonly record struct PawnId(string Value) : IComparable<PawnId>
{
    public string Value { get => field ?? string.Empty; } = Value;

    public bool IsEmpty => Value.Length == 0;

    public bool Equals(PawnId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(PawnId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    public static explicit operator Domain.PawnId(PawnId id) => new(id.Value);

    public static explicit operator PawnId(Domain.PawnId id) => new(id.Value);

    public static bool operator <(PawnId left, PawnId right) => left.CompareTo(right) < 0;

    public static bool operator >(PawnId left, PawnId right) => left.CompareTo(right) > 0;

    public static bool operator <=(PawnId left, PawnId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(PawnId left, PawnId right) => left.CompareTo(right) >= 0;
}
