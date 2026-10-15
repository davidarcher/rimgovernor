namespace RimGovernor.Brain.Policy;

/// <summary>One native drug policy: its load id, label, holders and the entries that allow anything.</summary>
public sealed record OwnedDrugPolicy(string Id, string Label, EquatableArray<PawnId> Pawns, EquatableArray<DrugPolicyEntry> Entries);
