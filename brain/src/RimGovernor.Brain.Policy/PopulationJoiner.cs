using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

/// <summary>Names why the joiner planner did or did not propose a quest acceptance.</summary>
public enum JoinerPlanReason
{
    [WireName("")]
    None = 0,
    [WireName("no_joiner_offer")]
    NoOffer,
    [WireName("no_population_capacity")]
    NoCapacity,
    [WireName("no_quest_offer")]
    QuestNoOffer,
}

/// <summary>
/// The one offer chosen for a QuestAccept write, chosen deterministically.
/// Quest is set whenever Reason is None; RewardChoice is the QuestAccept reward index (-1 without a
/// reward-choice part, otherwise the game's own first option).
/// </summary>
public sealed record JoinerChoice
{
    public PawnId Accepter { get; init; }

    public JoinerPlanReason Reason { get; init; }

    public QuestId Quest { get; init; }

    public int RewardChoice { get; init; }
}

/// <summary>
/// The population facts one more colonist is admitted against: every living person the colony already hosts
/// (admitted colonists, guests and prisoners, from the same population census CustodyFacts reads) and the food
/// runway in days. No spare bed is required (#3749): a joiner sleeps on a shelter/sleeping stand-in spot, and the new
/// population changes the layout key, so the next replan adds their bed. The target is the bot's own
/// FindingConstants.PopulationTarget; there is no player knob.
/// </summary>
public sealed record JoinerCapacityFacts
{
    public required PrisonerCensus Population { get; init; }

    public FoodRunway? FoodDays { get; init; }

    /// <summary>The player's colonist goal (#3494), 0 for none.</summary>
    public int ColonistGoal { get; init; }
}

public static partial class PopulationRules
{
    /// <summary>The population the colony grows toward: the bot's own target, raised to the player's colonist goal (#3494).</summary>
    public static int PopulationTargetFor(int colonistGoal) => Math.Max(FindingConstants.PopulationTarget, colonistGoal);

    /// <summary>
    /// The food runway one more colonist is admitted against: 3 days for a colony of one, rising linearly to
    /// 15 days at 20 or more hosted people.
    /// </summary>
    public static double JoinerFoodFloorDays(long hosted)
    {
        const long lowPop = 1, highPop = 20;
        const double lowDays = 3.0, highDays = 15.0;
        var n = Math.Min(Math.Max(hosted, lowPop), highPop);
        return lowDays + ((n - lowPop) * (highDays - lowDays) / (highPop - lowPop));
    }

    /// <summary>
    /// Reports whether a quest root is one of the refugee-chased-by-a-threat family (Core's
    /// ThreatReward_Raid_Joiner and the expansions' ThreatReward_*_Joiner roots): accepting it walks one
    /// joiner into the colony and brings the threat after them. Disclosed narrowing: the wanderer-joins offer
    /// is a hidden auto-accepted quest answered from a ChoiceLetter_AcceptJoiner letter, not a census row, and
    /// the opportunity-site joiners need a caravan, so neither is answered here.
    /// </summary>
    public static bool IsJoinerOffer(string scriptDef)
    {
        var profile = QuestFamiliesPolicy.QuestFamilyForRoot(scriptDef);
        return profile.Family == QuestFamily.Joiner && !profile.NeverAct;
    }

    /// <summary>
    /// Reports whether an offer is a joiner quest the colony could accept right now: not yet accepted,
    /// native-acceptable, and needing no accepter (a joiner offer never names one).
    /// </summary>
    internal static bool JoinerAnswerable(QuestState offer) =>
        IsJoinerOffer(offer.ScriptDef) && offer.State == QuestStatus.NotYetAccepted && offer.CanAccept && !offer.RequiresAccepter;

    /// <summary>
    /// Counts the living people the colony hosts: admitted colonists plus every guest (a prisoner is a guest
    /// of the player faction too).
    /// </summary>
    internal static long HostedPopulation(IReadOnlyList<CustodyFacts> custody) => custody.LongCount(row => !row.Dead && (row.Admitted || row.Guest));

    /// <summary>
    /// Reports whether the colony can host one more colonist: hosted population below
    /// FindingConstants.PopulationTarget and the food runway at or above JoinerFoodFloorDays. No unowned bed is
    /// required (#3749). Any unknown ingredient leaves it unknown.
    /// </summary>
    public static Fact<bool> JoinerCapacity(JoinerCapacityFacts f)
    {
        if (f.FoodDays is not { } food)
        {
            return Unknown<bool>();
        }
        var hosted = HostedPopulation(f.Population.Custody);
        return Known(hosted < PopulationTargetFor(f.ColonistGoal) && food.Reaches(JoinerFoodFloorDays(hosted)));
    }

    /// <summary>
    /// Reports whether an answerable joiner offer is waiting while the colony has capacity for it. An offer
    /// the colony cannot host is not a deficit: it is left to expire. A waiting offer while the capacity is
    /// unread raises <see cref="RequiredFactException"/>.
    /// </summary>
    public static bool JoinerDeficit(IReadOnlyList<QuestState> offers, Fact<bool> capacity, RoundsFacts facts) =>
        offers.Any(offer => JoinerAnswerable(offer) && QuestSelection.JoinerThreatReason(offer, facts).IsNone) && Room(capacity);

    /// <summary>
    /// Picks the lowest-ID answerable joiner offer to accept next, for a deterministic order, once the
    /// colony's capacity for one more colonist is known.
    /// </summary>
    public static JoinerChoice SelectJoinerMethod(IReadOnlyList<QuestState> offers, Fact<bool> capacity, RoundsFacts facts)
    {
        var refused = false;
        foreach (var offer in offers.OrderBy(o => o.Id))
        {
            if (!JoinerAnswerable(offer))
            {
                continue;
            }
            if (!QuestSelection.JoinerThreatReason(offer, facts).IsNone)
            {
                refused = true;
                continue;
            }
            return Room(capacity)
                ? new JoinerChoice { Quest = offer.Id, RewardChoice = offer.Rewards.Count > 0 ? 0 : -1 }
                : new JoinerChoice { Reason = JoinerPlanReason.NoCapacity };
        }
        return new JoinerChoice { Reason = refused ? JoinerPlanReason.NoCapacity : JoinerPlanReason.NoOffer };
    }

    /// <summary>Whether the colony has room for one more colonist; an unread capacity raises <see cref="RequiredFactException"/>.</summary>
    private static bool Room(Fact<bool> capacity) => capacity.TryGetValue(out var room) ? room : throw new RequiredFactException("population capacity");

    /// <summary>
    /// Reports whether an offer is a bestowing-ceremony quest the title claim gate allows (claims,
    /// ClaimQuests): not yet accepted, native-acceptable and needing no accepter. The quest is identified by
    /// the royalty read's quest id, not by script def.
    /// </summary>
    internal static bool ClaimAnswerable(QuestState offer, IReadOnlyList<QuestId> claims) =>
        claims.Contains(offer.Id) && offer.State == QuestStatus.NotYetAccepted && offer.CanAccept && !offer.RequiresAccepter;

    /// <summary>The lowest-id acceptable joiner letter while the colony has room; null when none waits or there is no room.</summary>
    public static JoinerLetter? SelectJoinerLetter(IReadOnlyList<JoinerLetter> letters, Fact<bool> capacity)
    {
        var acceptable = letters.Where(row => row.CanAccept).OrderBy(row => row.Id).ToList();
        return acceptable.Count > 0 && Room(capacity) ? acceptable[0] : null;
    }

    /// <summary>Reports whether an acceptable joiner letter waits while the colony has room.</summary>
    public static bool JoinerLetterDeficit(IReadOnlyList<JoinerLetter> letters, Fact<bool> capacity) =>
        letters.Any(row => row.CanAccept) && Room(capacity);
}

public static class JoinerRoundsFactsExtensions
{
    /// <summary>
    /// The slice of a review's facts JoinerCapacity measures: the population census and the population food
    /// runway (falling back to the stock runway, as the foothold food gate does).
    /// </summary>
    public static JoinerCapacityFacts JoinerCapacity(this RoundsFacts f) =>
        new() { Population = f.Population, FoodDays = f.FoodDays, ColonistGoal = f.ColonistGoal };
}
