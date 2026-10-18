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
        if (f.PrisonerColony.TryGetValue(out var colony))
        {
            f = f with { PrisonerColony = Known(colony with { Preferences = preferences }) };
        }
        if (!preferences.Forbids(ForbiddenMethod.HumanLeather))
        {
            return f;
        }
        f = f with
        {
            Items = f.Items with
            {
                StuffCategories = f.Items.StuffCategories.Where(kv => kv.Key != HumanLeather).ToDictionary(kv => kv.Key, kv => kv.Value),
                StuffBeauty = f.Items.StuffBeauty.Where(kv => kv.Key != HumanLeather).ToDictionary(kv => kv.Key, kv => kv.Value),
            },
        };
        if (!f.Gear.TryGetValue(out var gear))
        {
            return f;
        }
        GearPawn Stamp(GearPawn p) =>
            p.LoadoutModel.TryGetValue(out var input)
                ? p with { LoadoutModel = Known(input with { Options = EquatableArray.From(input.Options.Where(o => o.Stuff != HumanLeather)) }) }
                : p;
        return f with { Gear = Known(gear with { Pawns = [.. gear.Pawns.Select(Stamp)] }) };
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
