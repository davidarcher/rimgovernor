namespace RimGovernor.Brain.Domain;

/// <summary>The current observed scope and local permission. Unknown scope cannot authorize dispatch.</summary>
public readonly record struct ControlState(GenerationSnapshot Snapshot, bool ObservationKnown, bool Enabled);
