namespace RimGovernor.Brain.Policy;

/// <summary>One native drug policy: its label, holders and the entries that allow anything.</summary>
public sealed record OwnedDrugPolicy(string Label, EquatableArray<PawnId> Pawns, EquatableArray<DrugPolicyEntry> Entries);
