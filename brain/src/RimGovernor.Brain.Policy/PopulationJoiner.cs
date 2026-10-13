using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using Pb = RimGovernor.Protocol.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One row of the visible quest census (bridge.QuestOffer, from rimgovernor/observations_read_world_progression)
/// as MaintainPopulation reads it: enough to tell a joiner offer from every other quest and to build the
/// QuestAccept write for it. The boundary re-reads the quest's CAS token and native CanAcceptQuest verdict
/// immediately before dispatch; this only decides which already-observed offer to answer.
/// </summary>
public sealed record JoinerOffer
{
    public Fact<double> ThreatPoints { get; init; }

    public QuestId Quest { get; init; }

    public string ScriptDef { get; init; } = "";

    public string State { get; init; } = "";

    public bool CanAccept { get; init; }

    public bool RequiresAccepter { get; init; }

    public int ChoiceCount { get; init; }

    public Fact<long> ExpiresInTicks { get; init; }

    public IReadOnlyList<PawnId> EligiblePawnIds { get; init; } = [];

    public IReadOnlyList<PawnId> DeparturePawnIds { get; init; } = [];

    public IReadOnlyList<QuestObjective> Objectives { get; init; } = [];

    public IReadOnlyList<QuestShuttleState> Shuttles { get; init; } = [];

    public Fact<QuestProfile> Profile { get; init; }

    // FactionId is the quest's first non-player faction ("" when none); FactionHostile is that faction's
    // FactionState.hostile, unknown when the census carries no row for it. OnMap is true when the quest's map
    // is the colony's identity map. Favor is each reward choice's royal favor.
    public string FactionId { get; init; } = "";

    public Fact<bool> FactionHostile { get; init; }

    public bool OnMap { get; init; }

    public IReadOnlyList<QuestFavor> Favor { get; init; } = [];

    public IReadOnlyList<QuestReward> Rewards { get; init; } = [];

    public Fact<int> RewardChoiceParts { get; init; }

    public PawnId Asker { get; init; }

    public Fact<bool> AskerFactionPlayer { get; init; }

    public Fact<bool> ViolentQuestsAllowed { get; init; }

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
    public IReadOnlyList<QuestHackTarget> HackTargets { get; init; } = [];

    public Fact<QuestHackRisk> HackRisk { get; init; }

    public Fact<QuestGiftRequest> Gift { get; init; }

    public Fact<QuestMonument> Monument { get; init; }

    public Fact<QuestGravEngine> GravEngine { get; init; }

    public Fact<QuestSurveyScanner> SurveyScanner { get; init; }

    public Pb.QuestObjectiveKind Kind { get; init; }

    public string Def { get; init; } = "";

    public string Stuff { get; init; } = "";

    public Fact<long> Count { get; init; }

    public Fact<long> Produced { get; init; }

    public Fact<long> DeadlineTicks { get; init; }

    public Fact<long> DurationTicks { get; init; }

    public Fact<QuestWorkload> Workload { get; init; }

    public string UnmetRequirement { get; init; } = "";

    public IReadOnlyList<PawnId> PawnIds { get; init; } = [];

    public Fact<double> MinimumMood { get; init; }

    public IReadOnlyList<QuestLodgerMood> LodgerMoods { get; init; } = [];

    public Fact<bool> Active { get; init; }
}

public sealed record QuestLodgerMood(PawnId Pawn, Fact<double> Mood);

public sealed record QuestShuttleState
{
    public string Id { get; init; } = "";

    public Fact<bool> AutoloadAvailable { get; init; }

    public Fact<bool> Autoload { get; init; }

    public Fact<bool> Loading { get; init; }

    public Fact<bool> AllRequiredLoaded { get; init; }

    public Fact<bool> ManualLaunchAvailable { get; init; }

    public IReadOnlyList<PawnId> PawnIds { get; init; } = [];

    public IReadOnlyList<PawnId> LoadedPawnIds { get; init; } = [];

    public IReadOnlyList<PawnId> PendingPawnIds { get; init; } = [];

    public Fact<int> RequiredColonistCount { get; init; }
}

/// <summary>The royal favor one QuestAccept reward choice grants.</summary>
public sealed record QuestFavor(int Choice, int Favor);

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
/// The one offer chosen for a QuestAccept write, mirroring PrisonerChoice's single-write-per-cycle rule.
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
/// (admitted colonists, guests and prisoners, from the same population census CustodyFacts reads), the
/// sleeping census for a spare non-medical bed and the food runway in days. The target is the bot's own
/// DomainConstants.PopulationTarget; there is no player knob.
/// </summary>
public sealed record JoinerCapacityFacts
{
    public Fact<EquatableArray<CustodyFacts>> Custody { get; init; }

    public Fact<SleepingObservation> Sleeping { get; init; }

    public Fact<double> FoodDays { get; init; }
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

    public PawnId Pawn { get; init; }

    public Tick Expires { get; init; }

    public string Label { get; init; } = "";

    public bool CanAccept { get; init; }

    public bool CreepJoiner { get; init; }
}

public static partial class PolicyFunctions
{
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
        var profile = QuestFamilyForRoot(scriptDef);
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
    /// of the player faction too). A row missing any of those facts leaves the count unknown, since an
    /// undercount would admit a joiner past the population target.
    /// </summary>
    internal static Fact<long> HostedPopulation(Fact<EquatableArray<CustodyFacts>> custody)
    {
        if (!custody.TryGetValue(out var rows))
        {
            return Unknown<long>();
        }
        long count = 0;
        foreach (var row in rows)
        {
            var dk = row.Dead.TryGetValue(out var dead);
            var ak = row.Admitted.TryGetValue(out var admitted);
            var gk = row.Guest.TryGetValue(out var guest);
            if (!dk || !ak || !gk)
            {
                return Unknown<long>();
            }
            if (!dead && (admitted || guest))
            {
                count++;
            }
        }
        return Known(count);
    }

    /// <summary>Reports whether an unowned humanlike, non-medical, non-prisoner bed reads back: the bed the joiner would sleep in.</summary>
    internal static Fact<bool> SpareBed(Fact<SleepingObservation> sleeping)
    {
        if (!sleeping.TryGetValue(out var census))
        {
            return Unknown<bool>();
        }
        foreach (var bed in census.Beds)
        {
            var hk = bed.Humanlike.TryGetValue(out var humanlike);
            var mk = bed.Medical.TryGetValue(out var medical);
            var pk = bed.Prisoners.TryGetValue(out var prisoners);
            if (hk && mk && pk && humanlike && !medical && !prisoners && bed.Owners.Count == 0)
            {
                return Known(true);
            }
        }
        return Known(false);
    }

    /// <summary>
    /// Reports whether the colony can host one more colonist: hosted population below
    /// DomainConstants.PopulationTarget, the food runway at or above JoinerFoodFloorDays and a spare bed. Any
    /// unknown ingredient leaves it unknown.
    /// </summary>
    public static Fact<bool> JoinerCapacity(JoinerCapacityFacts f)
    {
        var hk = HostedPopulation(f.Custody).TryGetValue(out var hosted);
        var fk = f.FoodDays.TryGetValue(out var food);
        var bk = SpareBed(f.Sleeping).TryGetValue(out var bed);
        if (!hk || !fk || !bk)
        {
            return Unknown<bool>();
        }
        return Known(hosted < DomainConstants.PopulationTarget && food >= JoinerFoodFloorDays(hosted) && bed);
    }

    /// <summary>
    /// Reports whether an answerable joiner offer is waiting while the colony has capacity for it. An offer
    /// the colony cannot host is not a deficit: it is left to expire. An unknown census leaves the need
    /// unknown rather than silently settled, the same rule PrisonerRecruitDeficit uses.
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
            waiting = waiting || (JoinerAnswerable(offer) && JoinerThreatReason(offer, facts).IsNone);
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
    /// Picks the lowest-ID answerable joiner offer to accept next, mirroring SelectPrisonerInteractionMethod's
    /// determinism, once the colony's capacity for one more colonist is known.
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
            if (!JoinerThreatReason(offer, facts).IsNone)
            {
                refused = true;
                continue;
            }
            return new JoinerChoice { Quest = offer.Quest, RewardChoice = offer.ChoiceCount > 0 ? 0 : -1 };
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
    /// The slice of a review's facts JoinerCapacity measures: the population census, the sleeping census, the
    /// population food runway (falling back to the stock runway, as the foothold food gate does).
    /// </summary>
    public static JoinerCapacityFacts JoinerCapacity(this RoundsFacts f) =>
        new() { Custody = f.Custody, Sleeping = f.Sleeping, FoodDays = f.FoodDays };
}
