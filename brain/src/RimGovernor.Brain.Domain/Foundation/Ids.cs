namespace RimGovernor.Brain.Domain;

// Identifier value types. String ids compare and order by ordinal bytes (Go's `==` and `<` on a defined
// string type); the default value is the empty id, as Go's zero value. Numeric ids order by value.
// Naming: Go's two-letter initialism `ID` becomes `Id` (ColonyID -> ColonyId).

/// <summary>Names the colony (save).</summary>
public readonly record struct ColonyId(string Value) : IComparable<ColonyId>
{
    public string Value { get => field ?? string.Empty; } = Value;

    public bool IsEmpty => Value.Length == 0;

    /// <summary>Go's <c>validID(string(id))</c>.</summary>
    public bool IsValid => Identity.IsValidText(Value);

    public bool Equals(ColonyId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(ColonyId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    public static bool operator <(ColonyId left, ColonyId right) => left.CompareTo(right) < 0;

    public static bool operator >(ColonyId left, ColonyId right) => left.CompareTo(right) > 0;

    public static bool operator <=(ColonyId left, ColonyId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ColonyId left, ColonyId right) => left.CompareTo(right) >= 0;
}

/// <summary>Names one load of a save.</summary>
public readonly record struct LoadId(string Value) : IComparable<LoadId>
{
    public string Value { get => field ?? string.Empty; } = Value;

    public bool IsEmpty => Value.Length == 0;

    /// <summary>Go's <c>validID(string(id))</c>.</summary>
    public bool IsValid => Identity.IsValidText(Value);

    public bool Equals(LoadId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(LoadId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    public static bool operator <(LoadId left, LoadId right) => left.CompareTo(right) < 0;

    public static bool operator >(LoadId left, LoadId right) => left.CompareTo(right) > 0;

    public static bool operator <=(LoadId left, LoadId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(LoadId left, LoadId right) => left.CompareTo(right) >= 0;
}

/// <summary>Names one action within a plan.</summary>
public readonly record struct ActionId(string Value) : IComparable<ActionId>
{
    public string Value { get => field ?? string.Empty; } = Value;

    public bool IsEmpty => Value.Length == 0;

    /// <summary>Go's <c>validID(string(id))</c>.</summary>
    public bool IsValid => Identity.IsValidText(Value);

    public bool Equals(ActionId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(ActionId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    public static bool operator <(ActionId left, ActionId right) => left.CompareTo(right) < 0;

    public static bool operator >(ActionId left, ActionId right) => left.CompareTo(right) > 0;

    public static bool operator <=(ActionId left, ActionId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ActionId left, ActionId right) => left.CompareTo(right) >= 0;
}

/// <summary>Names a plan.</summary>
public readonly record struct PlanId(string Value) : IComparable<PlanId>
{
    public string Value { get => field ?? string.Empty; } = Value;

    public bool IsEmpty => Value.Length == 0;

    /// <summary>Go's <c>validID(string(id))</c>.</summary>
    public bool IsValid => Identity.IsValidText(Value);

    public bool Equals(PlanId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(PlanId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    public static bool operator <(PlanId left, PlanId right) => left.CompareTo(right) < 0;

    public static bool operator >(PlanId left, PlanId right) => left.CompareTo(right) > 0;

    public static bool operator <=(PlanId left, PlanId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(PlanId left, PlanId right) => left.CompareTo(right) >= 0;
}

/// <summary>Names one occurrence of a Response.</summary>
public readonly record struct IncidentId(string Value) : IComparable<IncidentId>
{
    public string Value { get => field ?? string.Empty; } = Value;

    public bool IsEmpty => Value.Length == 0;

    /// <summary>Go's <c>validID(string(id))</c>.</summary>
    public bool IsValid => Identity.IsValidText(Value);

    public bool Equals(IncidentId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(IncidentId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    public static bool operator <(IncidentId left, IncidentId right) => left.CompareTo(right) < 0;

    public static bool operator >(IncidentId left, IncidentId right) => left.CompareTo(right) > 0;

    public static bool operator <=(IncidentId left, IncidentId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(IncidentId left, IncidentId right) => left.CompareTo(right) >= 0;
}

/// <summary>Names a pawn.</summary>
public readonly record struct PawnId(string Value) : IComparable<PawnId>
{
    public string Value { get => field ?? string.Empty; } = Value;

    public bool IsEmpty => Value.Length == 0;

    /// <summary>Go's <c>validID(string(id))</c>.</summary>
    public bool IsValid => Identity.IsValidText(Value);

    public bool Equals(PawnId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(PawnId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    public static bool operator <(PawnId left, PawnId right) => left.CompareTo(right) < 0;

    public static bool operator >(PawnId left, PawnId right) => left.CompareTo(right) > 0;

    public static bool operator <=(PawnId left, PawnId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(PawnId left, PawnId right) => left.CompareTo(right) >= 0;
}

/// <summary>Names a controller session.</summary>
public readonly record struct ControllerSessionId(string Value) : IComparable<ControllerSessionId>
{
    public string Value { get => field ?? string.Empty; } = Value;

    public bool IsEmpty => Value.Length == 0;

    /// <summary>Go's <c>validID(string(id))</c>.</summary>
    public bool IsValid => Identity.IsValidText(Value);

    public bool Equals(ControllerSessionId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(ControllerSessionId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    public static bool operator <(ControllerSessionId left, ControllerSessionId right) => left.CompareTo(right) < 0;

    public static bool operator >(ControllerSessionId left, ControllerSessionId right) => left.CompareTo(right) > 0;

    public static bool operator <=(ControllerSessionId left, ControllerSessionId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ControllerSessionId left, ControllerSessionId right) => left.CompareTo(right) >= 0;
}

/// <summary>Names a quest.</summary>
public readonly record struct QuestId(string Value) : IComparable<QuestId>
{
    public string Value { get => field ?? string.Empty; } = Value;

    public bool IsEmpty => Value.Length == 0;

    /// <summary>Go's <c>validID(string(id))</c>.</summary>
    public bool IsValid => Identity.IsValidText(Value);

    public bool Equals(QuestId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(QuestId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    public static bool operator <(QuestId left, QuestId right) => left.CompareTo(right) < 0;

    public static bool operator >(QuestId left, QuestId right) => left.CompareTo(right) > 0;

    public static bool operator <=(QuestId left, QuestId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(QuestId left, QuestId right) => left.CompareTo(right) >= 0;
}

/// <summary>Names a Concern (what a Round inspects).</summary>
public readonly record struct ConcernId(string Value) : IComparable<ConcernId>
{
    public string Value { get => field ?? string.Empty; } = Value;

    public bool IsEmpty => Value.Length == 0;

    /// <summary>Go's <c>validID(string(id))</c>.</summary>
    public bool IsValid => Identity.IsValidText(Value);

    public bool Equals(ConcernId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(ConcernId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    public static bool operator <(ConcernId left, ConcernId right) => left.CompareTo(right) < 0;

    public static bool operator >(ConcernId left, ConcernId right) => left.CompareTo(right) > 0;

    public static bool operator <=(ConcernId left, ConcernId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ConcernId left, ConcernId right) => left.CompareTo(right) >= 0;
}

/// <summary>Names one Standard row: "routine-<hex16 world digest>-<concern>-<gen>".</summary>
public readonly record struct StandardId(string Value) : IComparable<StandardId>
{
    public string Value { get => field ?? string.Empty; } = Value;

    public bool IsEmpty => Value.Length == 0;

    /// <summary>Go's <c>validID(string(id))</c>.</summary>
    public bool IsValid => Identity.IsValidText(Value);

    public bool Equals(StandardId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(StandardId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    public static bool operator <(StandardId left, StandardId right) => left.CompareTo(right) < 0;

    public static bool operator >(StandardId left, StandardId right) => left.CompareTo(right) > 0;

    public static bool operator <=(StandardId left, StandardId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(StandardId left, StandardId right) => left.CompareTo(right) >= 0;
}

/// <summary>Names a selected method.</summary>
public readonly record struct MethodId(string Value) : IComparable<MethodId>
{
    public string Value { get => field ?? string.Empty; } = Value;

    public bool IsEmpty => Value.Length == 0;

    /// <summary>Go's <c>validID(string(id))</c>.</summary>
    public bool IsValid => Identity.IsValidText(Value);

    public bool Equals(MethodId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(MethodId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    public static bool operator <(MethodId left, MethodId right) => left.CompareTo(right) < 0;

    public static bool operator >(MethodId left, MethodId right) => left.CompareTo(right) > 0;

    public static bool operator <=(MethodId left, MethodId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(MethodId left, MethodId right) => left.CompareTo(right) >= 0;
}

/// <summary>Names a map within a colony.</summary>
public readonly record struct MapId(int Value) : IComparable<MapId>
{
    public int CompareTo(MapId other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool operator <(MapId left, MapId right) => left.Value < right.Value;

    public static bool operator >(MapId left, MapId right) => left.Value > right.Value;

    public static bool operator <=(MapId left, MapId right) => left.Value <= right.Value;

    public static bool operator >=(MapId left, MapId right) => left.Value >= right.Value;
}

/// <summary>Monotonic within one action; zero means no dispatch yet.</summary>
public readonly record struct AttemptId(ulong Value) : IComparable<AttemptId>
{
    public int CompareTo(AttemptId other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool operator <(AttemptId left, AttemptId right) => left.Value < right.Value;

    public static bool operator >(AttemptId left, AttemptId right) => left.Value > right.Value;

    public static bool operator <=(AttemptId left, AttemptId right) => left.Value <= right.Value;

    public static bool operator >=(AttemptId left, AttemptId right) => left.Value >= right.Value;
}

/// <summary>A plan revision.</summary>
public readonly record struct PlanRevision(ulong Value) : IComparable<PlanRevision>
{
    public int CompareTo(PlanRevision other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool operator <(PlanRevision left, PlanRevision right) => left.Value < right.Value;

    public static bool operator >(PlanRevision left, PlanRevision right) => left.Value > right.Value;

    public static bool operator <=(PlanRevision left, PlanRevision right) => left.Value <= right.Value;

    public static bool operator >=(PlanRevision left, PlanRevision right) => left.Value >= right.Value;
}

/// <summary>The native order generation.</summary>
public readonly record struct NativeGeneration(ulong Value) : IComparable<NativeGeneration>
{
    public int CompareTo(NativeGeneration other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool operator <(NativeGeneration left, NativeGeneration right) => left.Value < right.Value;

    public static bool operator >(NativeGeneration left, NativeGeneration right) => left.Value > right.Value;

    public static bool operator <=(NativeGeneration left, NativeGeneration right) => left.Value <= right.Value;

    public static bool operator >=(NativeGeneration left, NativeGeneration right) => left.Value >= right.Value;
}

/// <summary>A game tick.</summary>
public readonly record struct Tick(long Value) : IComparable<Tick>
{
    public int CompareTo(Tick other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool operator <(Tick left, Tick right) => left.Value < right.Value;

    public static bool operator >(Tick left, Tick right) => left.Value > right.Value;

    public static bool operator <=(Tick left, Tick right) => left.Value <= right.Value;

    public static bool operator >=(Tick left, Tick right) => left.Value >= right.Value;
}

public static class Identity
{
    /// <summary>Go's <c>nativeText</c>: valid id text, or empty when <paramref name="allowEmpty"/>.</summary>
    public static bool IsNativeText(string s, bool allowEmpty) => (s.Length == 0 && allowEmpty) || IsValidText(s);

    /// <summary>
    /// Go's <c>validID</c>: well-formed text that is not blank, at most 256 UTF-8 bytes and free of NUL.
    /// </summary>
    public static bool IsValidText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Contains('\0'))
        {
            return false;
        }
        try
        {
            return new System.Text.UTF8Encoding(false, true).GetByteCount(s) <= 256;
        }
        catch (System.Text.EncoderFallbackException)
        {
            return false;
        }
    }
}
