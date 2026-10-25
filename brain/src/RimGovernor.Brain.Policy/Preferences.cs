namespace RimGovernor.Brain.Policy;

/// <summary>
/// How the player's preferences (#3495) reach the policies that own what they forbid: the facts carry them, each
/// owner reads them and never proposes the forbidden thing. There is no veto layer.
/// </summary>
public static class PreferenceRules
{
    /// <summary>The ThingDef of human leather; a stuff choice for clothing and furniture, removed under <see cref="ForbiddenMethod.HumanLeather"/>. Native has no typed route from the brain.</summary>
    public static readonly Resource HumanLeather = new("Leather_Human");

    /// <summary>
    /// The facts with the preferences stamped: the field itself, the colony side of the prisoner decisions, and, under
    /// <see cref="ForbiddenMethod.HumanLeather"/>, the stuff catalog and every pawn's apparel options without human
    /// leather.
    /// </summary>
    public static RoundsFacts WithPreferences(this RoundsFacts f, PreferencesRecord preferences)
    {
        f = f with { Preferences = preferences };
        f = f with { Population = f.Population with { Colony = f.Population.Colony with { Preferences = preferences } } };
        var leather = preferences.Forbids(ForbiddenMethod.HumanLeather);
        if (leather)
        {
            f = f with
            {
                Items = f.Items with
                {
                    StuffCategories = f.Items.StuffCategories.Where(kv => kv.Key != HumanLeather).ToDictionary(kv => kv.Key, kv => kv.Value),
                    StuffBeauty = f.Items.StuffBeauty.Where(kv => kv.Key != HumanLeather).ToDictionary(kv => kv.Key, kv => kv.Value),
                },
            };
        }
        var apparel = preferences.Apparel;
        if (!leather && apparel == ApparelPreference.Dressed)
        {
            return f;
        }
        var gear = f.Gear;
        GearPawn Stamp(GearPawn p)
        {
            if (p.Policy is { } state && apparel != ApparelPreference.Dressed)
            {
                state = state with { Preference = apparel };
                p = p with { Policy = state };
                // The model's options were cut by the Dressed outfit rule; the preference's rule only narrows it.
                if (p.Model is { } model)
                {
                    var allowed = ApparelPolicyRules.RoleApparelDefinitions(GearLoadoutRules.DeriveGearRole(model.Role), state).ToHashSet(StringComparer.Ordinal);
                    p = p with { LoadoutModel = new GearModel.Modeled(model with { Options = EquatableArray.From(model.Options.Where(o => allowed.Contains(o.Definition.ToString()))) }) };
                }
            }
            return leather && p.Model is { } input
                ? p with { LoadoutModel = new GearModel.Modeled(input with { Options = EquatableArray.From(input.Options.Where(o => o.Stuff != HumanLeather)) }) }
                : p;
        }
        gear = gear with { Pawns = [.. gear.Pawns.Select(Stamp)] };
        f = f with { Gear = gear };
        if (apparel == ApparelPreference.Dressed)
        {
            return f;
        }
        // The clothing demand follows the outfits: only the garments some pawn's outfit still allows are replaced.
        var wearable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in gear.Pawns)
        {
            if (p.Policy is { } state)
            {
                wearable.UnionWith(ApparelPolicyRules.RoleApparelDefinitions(GearLoadoutRules.DeriveGearRole(state.Role), state));
            }
        }
        return f with { Garments = EquatableArray.From(f.Garments.Where(g => wearable.Contains(g.Definition.Value))) };
    }

    /// <summary>
    /// The pawns held back from a draft: the protected ones while some other defender is eligible, none when every
    /// eligible defender is protected (the draft then takes them and says so).
    /// </summary>
    public static IReadOnlySet<PawnId> HeldBackFromDraft(PreferencesRecord preferences, IEnumerable<SquadDefenderFacts> defenders)
    {
        if (preferences.ProtectedPawns.Count == 0)
        {
            return new HashSet<PawnId>();
        }
        var eligible = defenders.Where(SquadDefenseRules.SquadDefenderEligible).ToList();
        var protectedIds = eligible.Where(d => preferences.ProtectsPawn(d.Id.Value)).Select(d => d.Id).ToHashSet();
        return protectedIds.Count == eligible.Count ? new HashSet<PawnId>() : protectedIds;
    }
}
