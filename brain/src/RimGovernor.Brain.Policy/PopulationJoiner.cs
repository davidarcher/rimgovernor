using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One row of the visible quest census (bridge.QuestOffer, from rimgovernor/observations_read_world_progression)
/// as the joiners family reads it: enough to tell a joiner offer from every other quest and to build the
/// QuestAccept write for it. The boundary re-reads the quest's CAS token and native CanAcceptQuest verdict
/// immediately before dispatch; this only decides which already-observed offer to answer.
/// </summary>
public sealed record JoinerOffer
{
    /// <summary>The raid points the quest threatens, or none for a quest without threat parts.</summary>
    public Pb.QuestThreat Threat { get; init; } = new Pb.QuestThreat.NoThreat();

    public QuestId Quest { get; init; }

    public string ScriptDef { get; init; } = "";

    public string State { get; init; } = "";

    public bool CanAccept { get; init; }

    public bool RequiresAccepter { get; init; }

    public Pb.QuestExpiry Expiry { get; init; } = new Pb.QuestExpiry.NoExpiry();

    public IReadOnlyList<PawnId> EligiblePawnIds { get; init; } = [];

    public IReadOnlyList<PawnId> DeparturePawnIds { get; init; } = [];

    public IReadOnlyList<QuestObjective> Objectives { get; init; } = [];

    public IReadOnlyList<Pb.QuestShuttleState> Shuttles { get; init; } = [];

    public Fact<QuestProfile> Profile { get; init; }

    // FactionId is the quest's first non-player faction ("" when none); FactionHostile is that faction's
    // FactionState.hostile, unknown when the census carries no row for it. OnMap is true when the quest's map
    // is the colony's identity map. Favor is the royal favor the quest grants whatever option is taken; Rewards
    // holds one row per reward option, its position the QuestAccept reward index.
    public string FactionId { get; init; } = "";

    public Fact<bool> FactionHostile { get; init; }

    public bool OnMap { get; init; }

    public int Favor { get; init; }

    public IReadOnlyList<Pb.QuestReward> Rewards { get; init; } = [];

    public int RewardChoiceParts { get; init; }

    public Pb.DecreeAsker Asker { get; init; }

    public bool ViolentQuestsAllowed { get; init; }

    /// <summary>
    /// The quest root's ground or ship-only classification from the catalog's QuestScriptDef rows
    /// (bridge.QuestClass); unknown when the catalog could not classify it. SelectOdysseyQuestMethod reads it.
    /// </summary>
    public Fact<QuestClass> Class { get; init; }

    /// <summary>Why the catalog could not classify the root.</summary>
    public string ClassError { get; init; } = "";
}

/// <summary>Carries the typed native requirement and its observed progress.</summary>
public sealed record QuestObjective
{
    public IReadOnlyList<Pb.QuestHackTarget> HackTargets { get; init; } = [];

    /// <summary>The worshipped terminal's site faction; null when the objective names none.</summary>
    public Pb.QuestHackRisk? HackRisk { get; init; }

    /// <summary>The beg-for-items request; null for an objective that is not one.</summary>
    public Pb.QuestGiftRequest? Gift { get; init; }

    /// <summary>The monument marker; null for an objective that is not a monument or whose quest holds no live marker.</summary>
    public Pb.QuestMonument? Monument { get; init; }

    public Pb.QuestGravEngine? GravEngine { get; init; }

    public Pb.QuestSurveyScanner? SurveyScanner { get; init; }

    public Pb.QuestObjectiveKind Kind { get; init; }

    public string Def { get; init; } = "";

    public string Stuff { get; init; } = "";

    public Pb.QuestObjectiveGoal Goal { get; init; } = new Pb.QuestObjectiveGoal.Uncounted();

    public Pb.QuestObjectiveTiming Timing { get; init; } = new Pb.QuestObjectiveTiming.Untimed();

    /// <summary>The work an unfinished produce or harvest objective still needs; null when it needs none or no usable bench hosts its recipe.</summary>
    public QuestWorkload? Workload { get; init; }

    public IReadOnlyList<PawnId> PawnIds { get; init; } = [];

    public Pb.QuestMoodFloor MinimumMood { get; init; } = new Pb.QuestMoodFloor.NoFloor();

    public IReadOnlyList<Pb.QuestLodgerMood> LodgerMoods { get; init; } = [];

    public bool Active { get; init; }
}


/// <summary>Names why the joiner planner did or did not propose a quest acceptance.</summary>
public enum JoinerPlanReason
{
    [WireName("")]
    None = 0,
    [WireName("no_joiner_offer")]
    NoOffer,
    [WireName("no_population_capacity")]
    NoCapacity,
    [WireName("quest_census_unknown")]
    CensusUnknown,
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
    public Fact<PrisonerCensus> Population { get; init; }

    public Fact<double> FoodDays { get; init; }

    /// <summary>The player's colonist goal (#3494), 0 for none.</summary>
    public int ColonistGoal { get; init; }
}

/// <summary>
/// A pending current-map WandererJoins or creepjoiner letter. Its opaque token binds the native pawn, quest,
/// map, expiry and choices. Expires is the letter's disappearAtTick verbatim, negative for a letter with no
/// timeout (the game's own sentinel).
/// </summary>
public sealed record JoinerLetterOffer
{
    public int Id { get; init; }

    public string Token { get; init; } = "";

    public string Label { get; init; } = "";

    public bool CanAccept { get; init; }
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
    internal static bool JoinerAnswerable(JoinerOffer offer) =>
        IsJoinerOffer(offer.ScriptDef) && offer.State == "NotYetAccepted" && offer.CanAccept && !offer.RequiresAccepter;

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
        if (!f.Population.TryGetValue(out var census) || !f.FoodDays.TryGetValue(out var food))
        {
            return Unknown<bool>();
        }
        var hosted = HostedPopulation(census.Custody);
        return Known(hosted < PopulationTargetFor(f.ColonistGoal) && food >= JoinerFoodFloorDays(hosted));
    }

    /// <summary>
    /// Reports whether an answerable joiner offer is waiting while the colony has capacity for it. An offer
    /// the colony cannot host is not a deficit: it is left to expire. An unknown census leaves the need
    /// unknown rather than silently settled, the same rule the prisoner census uses.
    /// </summary>
    public static Fact<bool> JoinerDeficit(Fact<EquatableArray<JoinerOffer>> offers, Fact<bool> capacity, RoundsFacts facts)
    {
        if (!offers.TryGetValue(out var rows))
        {
            return Unknown<bool>();
        }
        var waiting = false;
        foreach (var offer in rows)
        {
            waiting = waiting || (JoinerAnswerable(offer) && QuestSelection.JoinerThreatReason(offer, facts).IsNone);
        }
        if (!waiting)
        {
            return Known(false);
        }
        if (!capacity.TryGetValue(out var room))
        {
            return Unknown<bool>();
        }
        return Known(room);
    }

    /// <summary>
    /// Picks the lowest-ID answerable joiner offer to accept next, for a
    /// deterministic order, once the colony's capacity for one more colonist is known.
    /// </summary>
    public static JoinerChoice SelectJoinerMethod(Fact<EquatableArray<JoinerOffer>> offers, Fact<bool> capacity, RoundsFacts facts)
    {
        if (!offers.TryGetValue(out var rows))
        {
            return new JoinerChoice { Reason = JoinerPlanReason.CensusUnknown };
        }
        var sorted = rows.OrderBy(o => o.Quest).ToList();
        var refused = false;
        foreach (var offer in sorted)
        {
            if (!JoinerAnswerable(offer))
            {
                continue;
            }
            if (!capacity.TryGetValue(out var room))
            {
                return new JoinerChoice { Reason = JoinerPlanReason.CensusUnknown };
            }
            if (!room)
            {
                return new JoinerChoice { Reason = JoinerPlanReason.NoCapacity };
            }
            if (!QuestSelection.JoinerThreatReason(offer, facts).IsNone)
            {
                refused = true;
                continue;
            }
            return new JoinerChoice { Quest = offer.Quest, RewardChoice = offer.Rewards.Count > 0 ? 0 : -1 };
        }
        return new JoinerChoice { Reason = refused ? JoinerPlanReason.NoCapacity : JoinerPlanReason.NoOffer };
    }

    /// <summary>
    /// Reports whether an offer is a bestowing-ceremony quest the title claim gate allows (claims,
    /// ClaimQuests): not yet accepted, native-acceptable and needing no accepter. The quest is identified by
    /// the royalty read's quest id, not by script def.
    /// </summary>
    internal static bool ClaimAnswerable(JoinerOffer offer, IReadOnlyList<QuestId> claims) =>
        claims.Contains(offer.Quest) && offer.State == "NotYetAccepted" && offer.CanAccept && !offer.RequiresAccepter;

    public static (JoinerLetterOffer Offer, bool Ok) SelectJoinerLetter(Fact<EquatableArray<JoinerLetterOffer>> offers, Fact<bool> capacity)
    {
        var known = offers.TryGetValue(out var rows);
        var capacityKnown = capacity.TryGetValue(out var room);
        if (!known || !capacityKnown || !room)
        {
            return (new JoinerLetterOffer(), false);
        }
        var chosen = new JoinerLetterOffer();
        var found = false;
        foreach (var row in rows!)
        {
            if (row.CanAccept && (!found || row.Id < chosen.Id))
            {
                (chosen, found) = (row, true);
            }
        }
        return (chosen, found);
    }

    public static Fact<bool> JoinerLetterDeficit(Fact<EquatableArray<JoinerLetterOffer>> offers, Fact<bool> capacity)
    {
        if (!offers.TryGetValue(out var rows))
        {
            return Unknown<bool>();
        }
        return rows.Any(row => row.CanAccept) ? capacity : Known(false);
    }
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
