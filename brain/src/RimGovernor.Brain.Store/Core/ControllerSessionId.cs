namespace RimGovernor.Brain.Store;

/// <summary>
/// One persistent controller execution namespace: independent of HTTP process sessions, it survives
/// controller restarts. 64 lowercase hex characters (32 random bytes).
/// </summary>
public readonly record struct ControllerSessionId(string Value)
{
    public string Value { get => field ?? string.Empty; } = Value;

    public bool Equals(ControllerSessionId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}
