using RimGovernor.Contracts.Wire.Defs;

namespace RimGovernor.Brain.Policy;

/// <summary>What the drug-policy planner owes: the colony policy's contents (null when it already holds them) and the pawns that do not hold it yet.</summary>
public sealed record DrugPolicyChanges(DrugPolicy? Write, IReadOnlyList<PawnSettings> Assign);

/// <summary>
/// The one owner of the DrugPolicy action (#3498): the player's drug preference becomes one colony policy, written once
/// and assigned to every colonist. Without a preference (and without the Drugs method forbidden) the native policies
/// are left as the game has them.
/// </summary>
public static class DrugPolicyPlanRules
{
    /// <summary>The label of the colony's drug policy.</summary>
    public const string Label = "RimGovernor drugs";

    // The native defaults of a new entry: scheduled at most daily, whatever the mood and joy.
    private const double DefaultDaysFrequency = 1;
    private const double DefaultThreshold = 1;

    /// <summary>The preference the colony plans: forbidding drugs is the None preference whatever else is set; null for no preference.</summary>
    public static DrugPolicyPreference? Effective(PreferencesRecord preferences) =>
        preferences.Forbids(ForbiddenMethod.Drugs) ? DrugPolicyPreference.None : preferences.DrugPolicy;

    /// <summary>
    /// The entries a preference allows, by drug: none for None (medical use is prescribed, not a policy row), joy use of
    /// every social drug for SocialOnly, scheduled use of every medical drug for Medical.
    /// </summary>
    public static IReadOnlyList<DrugPolicyEntry> Entries(DrugPolicyPreference preference, ItemFacts items)
    {
        DrugPolicyEntry Entry(Drug d, bool joy) =>
            new(d.Def.Value, joy, false, !joy, DefaultDaysFrequency, DefaultThreshold, DefaultThreshold, 0);

        return preference switch
        {
            DrugPolicyPreference.SocialOnly => [.. items.RecreationDrugs().Select(d => Entry(d, true))],
            DrugPolicyPreference.Medical => [.. items.MedicalDrugs().Select(d => Entry(d, false))],
            _ => [],
        };
    }

    /// <summary>
    /// The write and assignments that bring the colony's non-captive pawns onto the preference's policy. Nothing when there
    /// is no preference, when the label is held by several native policies (native refuses a shared name), or when the
    /// policy already holds the entries and every pawn already holds the policy.
    /// </summary>
    public static DrugPolicyChanges Changes(PreferencesRecord preferences, ItemFacts items, IReadOnlyList<WorkPawn> pawns, IReadOnlyList<OwnedDrugPolicy> policies)
    {
        var none = new DrugPolicyChanges(null, []);
        if (Effective(preferences) is not { } preference)
        {
            return none;
        }
        var own = policies.Where(p => p.Label == Label).ToList();
        if (own.Count > 1)
        {
            return none;
        }
        var entries = Entries(preference, items);
        var write = own.Count == 0 || !own[0].Entries.OrderBy(e => e.Drug, StringComparer.Ordinal).SequenceEqual(entries.OrderBy(e => e.Drug, StringComparer.Ordinal))
            ? new DrugPolicy(Label, entries)
            : null;
        var held = own.Count == 0 ? new HashSet<PawnId>() : own[0].Pawns.ToHashSet();
        var assign = new List<PawnSettings>();
        foreach (var pawn in pawns)
        {
            if (!pawn.PolicyInputs.TryGetValue(out var inputs) || inputs.GuestStatus is GuestStatus.Slave or GuestStatus.Prisoner || held.Contains(pawn.Id))
            {
                continue;
            }
            assign.Add(PawnSettings.NewDrugPolicySetting(pawn.Id, Label));
        }
        return new DrugPolicyChanges(write, assign);
    }
}
