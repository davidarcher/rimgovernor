using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>The Safeguards read through the journalled review.</summary>
internal static class Safeguards
{
    public static (SafeguardRefusal Refusal, bool Refused) Refuse(this ReviewState rounds, SafeguardProposal proposal) =>
        SafeguardsRules.RefuseProposal(new SafeguardContext { Enabled = rounds.Enabled, Unsafe = rounds.Unsafe }, proposal);

    public static (SafeguardRefusal Refusal, bool Refused) RefuseNeed(this ReviewState rounds, FilingKey need) =>
        rounds.Refuse(new SafeguardProposal { Need = need });

    public static string VetoNeed(this ReviewState rounds, FilingKey need) => rounds.RefuseNeed(need).Refusal.Reason;
}
