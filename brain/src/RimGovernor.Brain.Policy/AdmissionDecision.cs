namespace RimGovernor.Brain.Policy;

// Go: admission_decision.go
public readonly partial record struct Reason
{
    /// <summary>Native previewed the action and refused it.</summary>
    public static readonly Reason NativeIneligible = new("native_ineligible");
}

/// <summary>One admission verdict: admitted, or the refusals and the emergency facts behind them.</summary>
public sealed record AdmissionDecision
{
    public bool Admitted { get; init; }

    public EquatableArray<Refusal> Refused { get; init; }

    public EmergencyDecision Emergency { get; init; } = new();
}
