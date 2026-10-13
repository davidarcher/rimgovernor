namespace RimGovernor.Brain.Policy;

// A hunter's weapon is the one prerequisite of a hunt that the plan prices as
// its own produce step: craft one bow, at an estimated work and lead.
// The butcher bill and the butcher spot are owed on the food runway alone
// (their executors, ButcherFood and the butcher facility, never wait on a hunt
// row), so they are not repriced here.
public static partial class PolicyConstants
{
    /// <summary>The work of crafting one hunting weapon.</summary>
    public const double HunterWeaponCraftTicks = 6000.0;

    /// <summary>How long the craft takes before the hunt can start; reported as the craft_lead_days term.</summary>
    public const double HunterWeaponLeadDays = 0.1;

    /// <summary>Marks a hunt candidate that waits on a hunter's weapon.</summary>
    internal const string TermNeedsWeapon = "needs_weapon";
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Reports a no_hunter hold where some colonist is only missing the hunting work or its weapon:
    /// arming one of them opens the row.
    /// </summary>
    internal static bool NeedsHunterWeapon(this HuntHold h)
    {
        if (h.Reason != HuntHoldNoHunter)
        {
            return false;
        }
        foreach (var d in h.Detail)
        {
            if (d.EndsWith(" " + HuntHunterInactive, StringComparison.Ordinal) || d.EndsWith(" " + HuntHunterNoWeapon, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The lone hunt channels of the food prey whose only blocker is a hunter's weapon: each is priced as
    /// the hunt plus the craft (upfront HunterWeaponCraftTicks, craft_lead_days term) and carries a
    /// needs_weapon term. The plan opens one only when the hunt is worth it; an opened one is HuntArming.
    /// </summary>
    public static IReadOnlyList<SupplyCandidate> HuntPrerequisiteCandidates(IReadOnlyList<HuntHold> holds)
    {
        var output = new List<SupplyCandidate>();
        foreach (var h in holds)
        {
            if (!h.NeedsHunterWeapon() || !h.Source.Food || h.Source.Tree)
            {
                continue;
            }
            var c = HuntChannel(h.Source.Id, [h.Source], false, 1, 0);
            // The craft's lead is a term, not the candidate's lead: a starving
            // colony has no runway, and any positive lead exceeds it, so the one
            // step that lets it hunt could never open.
            output.Add(c with
            {
                UpfrontCost = c.UpfrontCost with { LaborTicks = Known(HunterWeaponCraftTicks) },
                Terms = [.. c.Terms, new CandidateTerm(TermNeedsWeapon, 1), new CandidateTerm("craft_lead_days", HunterWeaponLeadDays)],
            });
        }
        return output;
    }

    /// <summary>
    /// Reports whether the plan opened a hunt that waits on a hunter's weapon: the colonist who will
    /// carry it needs Hunting before it exists.
    /// </summary>
    public static bool HuntArming(Fact<FoodPlan> plan)
    {
        if (!plan.TryGetValue(out var p))
        {
            return false;
        }
        foreach (var e in p.Portfolio)
        {
            if (e.Channel.Kind != CandidateKind.Hunt || e.Decision != FoodPlanDecision.Open)
            {
                continue;
            }
            foreach (var t in e.Channel.Terms)
            {
                if (t.Name == TermNeedsWeapon)
                {
                    return true;
                }
            }
        }
        return false;
    }
}
