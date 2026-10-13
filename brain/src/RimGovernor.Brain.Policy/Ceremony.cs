namespace RimGovernor.Brain.Policy;

// Go: ceremony.go
// The bestowing ceremony. Accepting the Empire's bestowing-ceremony quest sends a bestower, who waits at the
// throne spot until the player starts the ritual; the ceremony then fails if the bestower or the colonist is
// lost, drafted out of it or in a mental state. While one is pending the colony keeps the throne room of the
// title being bestowed (CeremonyThroneNeed outranks the favor-driven NextThroneNeed) and holds the colonist and
// the attendees off the Sleep timetable (CeremonyHold, PlanSchedulesHeld). Once the bestower waits for the
// player's command (his Wait toil gizmo) CeremonyStart picks the ceremony the colony starts through the
// generic Ritual action.

/// <summary>
/// One pending bestowing-ceremony quest, decoded from the royalty read. <see cref="Quest"/> is the quest
/// census id. <see cref="Pawn"/> is the colonist to be bestowed with <see cref="Title"/>, <see cref="Bestower"/>
/// the Empire pawn. <see cref="Accepted"/> is the quest Ongoing (else offered); <see cref="BestowerWaiting"/>
/// the bestower standing on the map waiting for the command; <see cref="Started"/> the ritual running.
/// <see cref="Attendees"/> are the colonists the ritual lord holds.
/// </summary>
public sealed record BestowingCeremony
{
    public QuestId Quest { get; init; }

    public PawnId Pawn { get; init; }

    public PawnId Bestower { get; init; }

    public string Faction { get; init; } = "";

    public string Title { get; init; } = "";

    public Fact<bool> Accepted { get; init; }

    public Fact<bool> BestowerWaiting { get; init; }

    public Fact<bool> Started { get; init; }

    public Fact<Cell> Spot { get; init; }

    public EquatableArray<PawnId> Attendees { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>The accepted ceremonies, by colonist.</summary>
    public static List<BestowingCeremony> PendingCeremonies(RoyaltyFacts f) =>
        [
            .. f.Ceremonies.Items
                .Where(c => c.Accepted.TryGetValue(out var accepted) && accepted)
                .OrderBy(c => c.Pawn.Value, StringComparer.Ordinal),
        ];

    /// <summary>
    /// The ceremony to start now: accepted, its bestower standing on the map waiting for the command and the
    /// ritual known not to have started (an unknown flag emits nothing); the lowest colonist breaks a tie.
    /// The throne room is native's refusal, not a gate here: the bestower waits at the throne only once it
    /// stands and native refuses the command while the game offers none.
    /// </summary>
    public static BestowingCeremony? CeremonyStart(RoyaltyFacts f)
    {
        foreach (var c in PendingCeremonies(f))
        {
            var wok = c.BestowerWaiting.TryGetValue(out var waiting);
            var sok = c.Started.TryGetValue(out var started);
            if (wok && waiting && sok && !started)
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>CeremonyStart of a royalty read that may be unknown.</summary>
    public static BestowingCeremony? CeremonyStartOf(Fact<RoyaltyFacts> royalty) =>
        royalty.TryGetValue(out var f) ? CeremonyStart(f) : null;

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

                if (RungRequirement(rung) is { } need)
                {
                    return need with { Holder = c.Pawn, Titled = HoldsTitle(f, c.Pawn) };
                }
            }
        }

        return null;
    }

    /// <summary>CeremonyHold of a royalty read that may be unknown (no hold).</summary>
    public static HashSet<PawnId> CeremonyHoldOf(Fact<RoyaltyFacts> royalty) =>
        royalty.TryGetValue(out var f) ? CeremonyHold(f) : [];

    /// <summary>The colonists a pending ceremony needs off the Sleep timetable: the one bestowed and the attendees the ritual lord holds.</summary>
    public static HashSet<PawnId> CeremonyHold(RoyaltyFacts f)
    {
        var hold = new HashSet<PawnId>();
        foreach (var c in PendingCeremonies(f))
        {
            hold.Add(c.Pawn);
            foreach (var a in c.Attendees.Items)
            {
                hold.Add(a);
            }
        }

        return hold;
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
                c.Accepted.TryGetValue(out var accepted)
                && !accepted
                && c.Quest.Value.Length != 0
                && c.Pawn == claim.Holder
                && (c.Title.Length == 0 || c.Title == claim.Title)
            )
            {
                output.Add(c.Quest);
            }
        }

        return output;
    }
}
