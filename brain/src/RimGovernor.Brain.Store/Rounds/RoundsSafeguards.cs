using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>The Safeguards read through the journalled review (Go <c>Rounds.refuse</c>, <c>refuseNeed</c>, <c>vetoNeed</c>).</summary>
internal static class RoundsSafeguards
{
    public static (SafeguardRefusal Refusal, bool Refused) Refuse(this Rounds rounds, SafeguardProposal proposal) =>
        SafeguardsRules.RefuseProposal(new SafeguardContext { Enabled = rounds.Enabled, Unsafe = rounds.Unsafe }, proposal);

    public static (SafeguardRefusal Refusal, bool Refused) RefuseNeed(this Rounds rounds, ConcernId need) =>
        rounds.Refuse(new SafeguardProposal { Need = need });

    public static string VetoNeed(this Rounds rounds, ConcernId need) => rounds.RefuseNeed(need).Refusal.Reason;
}
