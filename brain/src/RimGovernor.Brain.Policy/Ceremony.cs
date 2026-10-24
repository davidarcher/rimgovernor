namespace RimGovernor.Brain.Policy;

// The bestowing ceremony. Accepting the Empire's bestowing-ceremony quest sends a bestower, who waits at the
// throne spot until the player starts the ritual; the ceremony then fails if the bestower or the colonist is
// lost, drafted out of it or in a mental state. While one is pending the colony keeps the throne room of the
// title being bestowed (CeremonyThroneNeed outranks the favor-driven NextThroneNeed). Attendance has no
// shared voluntary Method yet. Once the bestower waits for the
// player's command (his Wait toil gizmo) CeremonyStart picks the ceremony the colony starts through the
// generic Ritual action.

public static class CeremonyRules
{
    /// <summary>The accepted ceremonies, by colonist.</summary>
    public static List<BestowingCeremony> PendingCeremonies(RoyaltyFacts f) =>
        [
            .. f.Ceremonies.Items
                .Where(c => c.Accepted)
                .OrderBy(c => c.Pawn.Value, StringComparer.Ordinal),
        ];

    /// <summary>
    /// The ceremony to start now: accepted, its bestower standing on the map waiting for the command and the
    /// ritual not started; the lowest colonist breaks a tie.
    /// The throne room is native's refusal, not a gate here: the bestower waits at the throne only once it
    /// stands and native refuses the command while the game offers none.
    /// </summary>
    public static BestowingCeremony? CeremonyStart(RoyaltyFacts f)
    {
        foreach (var c in PendingCeremonies(f))
        {
            if (c.BestowerWaiting && !c.Started)
            {
                return c;
            }
        }

        return null;
    }


    /// <summary>
    /// The throne room the first pending ceremony needs: the requirement of the title it bestows, null when
    /// none is pending or the title asks for no throne or is unknown to the ladder.
    /// </summary>
    public static ThroneNeed? CeremonyThroneNeed(RoyaltyFacts f)
    {
        foreach (var c in PendingCeremonies(f))
        {
            foreach (var rung in f.Ladder.Items)
            {
                if (rung.Title != c.Title)
                {
                    continue;
                }

                var (need, needOk) = ThroneRules.RungRequirement(rung);
                if (needOk)
                {
                    return need with { Holder = c.Pawn, Titled = ThroneRules.HoldsTitle(f, c.Pawn), Held = ThroneRules.HeldRequirement(f, c.Pawn) };
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The offered bestowing-ceremony quests the title claim gate (NextTitleClaim) allows to accept now: the
    /// quest of the claiming colonist for the claimed title, not yet accepted. Accepting it is how the colony
    /// claims the title (SelectQuestMethod).
    /// </summary>
    public static List<QuestId> ClaimQuests(RoyaltyFacts f, TitleClaim claim)
    {
        var output = new List<QuestId>();
        if (!claim.Claim)
        {
            return output;
        }

        foreach (var c in f.Ceremonies.Items)
        {
            if (
                !c.Accepted
                && c.Quest.Length != 0
                && c.Pawn == claim.Holder
                && (c.Title is null || c.Title == claim.Title)
            )
            {
                output.Add(new QuestId(c.Quest));
            }
        }

        return output;
    }
}
