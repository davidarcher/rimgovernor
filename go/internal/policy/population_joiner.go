package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// JoinerOffer is one row of the visible quest census (bridge.QuestOffer,
// from rimgovernor/observations_read_world_progression) as MaintainPopulation
// reads it: enough to tell a joiner offer from every other quest and to
// build the QuestAccept write for it. The boundary re-reads the quest's CAS
// token and native CanAcceptQuest verdict immediately before dispatch; this
// only decides which already-observed offer to answer.
type JoinerOffer struct {
	ThreatPoints     domain.Fact[float64]
	Quest            domain.QuestID
	ScriptDef        string
	State            string
	CanAccept        bool
	RequiresAccepter bool
	ChoiceCount      int32
	ExpiresInTicks   domain.Fact[int64]
	EligiblePawnIDs  []domain.PawnID
	DeparturePawnIDs []domain.PawnID
	Objectives       []QuestObjective
	Shuttles         []QuestShuttleState
	Profile          domain.Fact[QuestProfile]
	// FactionID is the quest's first non-player faction ("" when none);
	// FactionHostile is that faction's FactionState.hostile, unknown when the
	// census carries no row for it. OnMap is true when the quest's map is
	// the colony's identity map. Favor is each reward choice's royal favor.
	FactionID            string
	FactionHostile       domain.Fact[bool]
	OnMap                bool
	Favor                []QuestFavor
	Rewards              []QuestReward
	RewardChoiceParts    domain.Fact[int32]
	Asker                domain.PawnID
	AskerFactionPlayer   domain.Fact[bool]
	ViolentQuestsAllowed domain.Fact[bool]
	// Class is the quest root's ground or ship-only classification from the
	// catalog's QuestScriptDef rows (bridge.QuestClass); unknown when the
	// catalog could not classify it. SelectOdysseyQuestMethod reads it.
	Class domain.Fact[QuestClass]
	// ClassError is why the catalog could not classify the root.
	ClassError string
}

// QuestObjective carries the typed native requirement and its observed progress.
type QuestObjective struct {
	Monument         domain.Fact[QuestMonument]
	Kind             o.QuestObjectiveKind
	Def              string
	Stuff            string
	Count            domain.Fact[int64]
	Produced         domain.Fact[int64]
	DeadlineTicks    domain.Fact[int64]
	DurationTicks    domain.Fact[int64]
	Workload         domain.Fact[QuestWorkload]
	UnmetRequirement string
	PawnIDs          []domain.PawnID
	MinimumMood      domain.Fact[float64]
	LodgerMoods      []QuestLodgerMood
	Active           domain.Fact[bool]
}
type QuestLodgerMood struct {
	Pawn domain.PawnID
	Mood domain.Fact[float64]
}
type QuestShuttleState struct {
	ID                                                                             string
	AutoloadAvailable, Autoload, Loading, AllRequiredLoaded, ManualLaunchAvailable domain.Fact[bool]
	PawnIDs, LoadedPawnIDs                                                         []domain.PawnID
	PendingPawnIDs                                                                 []domain.PawnID
	RequiredColonistCount                                                          domain.Fact[int32]
}

// QuestFavor is the royal favor one QuestAccept reward choice grants.
type QuestFavor struct {
	Choice int32
	Favor  int32
}

// JoinerPlanReason names why RoundsPopulationJoinerPlanner did or did not
// propose a quest acceptance.
type JoinerPlanReason string

const (
	JoinerNoOffer       JoinerPlanReason = "no_joiner_offer"
	JoinerNoCapacity    JoinerPlanReason = "no_population_capacity"
	JoinerCensusUnknown JoinerPlanReason = "quest_census_unknown"
)

// JoinerChoice is the one offer chosen for a QuestAccept write, mirroring
// PrisonerChoice's single-write-per-cycle rule. Quest is set whenever Reason
// is empty; RewardChoice is the QuestAccept reward index (-1 without a
// reward-choice part, otherwise the game's own first option).
type JoinerChoice struct {
	Accepter     domain.PawnID
	Reason       JoinerPlanReason
	Quest        domain.QuestID
	RewardChoice int32
}

// JoinerCapacityFacts are the population facts one more colonist is
// admitted against: every living person the colony already hosts (admitted
// colonists, guests and prisoners, from the same population census
// CustodyFacts reads), the sleeping census for a spare non-medical bed and
// the food runway in days. The target is the bot's own
// domain.PopulationTarget; there is no player knob (#1032).
type JoinerCapacityFacts struct {
	Custody  domain.Fact[[]CustodyFacts]
	Sleeping domain.Fact[SleepingObservation]
	FoodDays domain.Fact[float64]
}

// JoinerFoodFloorDays is the food runway one more colonist is admitted
// against: 3 days for a colony of one, rising linearly to 15 days at 20 or
// more hosted people (#1032).
func JoinerFoodFloorDays(hosted int64) float64 {
	const lowPop, highPop, lowDays, highDays = 1, 20, 3.0, 15.0
	n := min(max(hosted, lowPop), highPop)
	return lowDays + float64(n-lowPop)*(highDays-lowDays)/float64(highPop-lowPop)
}

// IsJoinerOffer reports whether a quest root is one of the refugee-chased-
// by-a-threat family (Core's ThreatReward_Raid_Joiner and the expansions'
// ThreatReward_*_Joiner roots): accepting it walks one joiner into the
// colony and brings the threat after them. Disclosed narrowing: the
// wanderer-joins offer is a hidden auto-accepted quest answered from a
// ChoiceLetter_AcceptJoiner letter, not a census row, and the opportunity-
// site joiners need a caravan, so neither is answered here.
func IsJoinerOffer(scriptDef string) bool {
	profile := QuestFamilyForRoot(scriptDef)
	return profile.Family == QuestFamilyJoiner && !profile.NeverAct
}

// joinerAnswerable reports whether an offer is a joiner quest the colony
// could accept right now: not yet accepted, native-acceptable, and needing
// no accepter (a joiner offer never names one).
func joinerAnswerable(offer JoinerOffer) bool {
	return IsJoinerOffer(offer.ScriptDef) && offer.State == "NotYetAccepted" && offer.CanAccept && !offer.RequiresAccepter
}

// hostedPopulation counts the living people the colony hosts: admitted
// colonists plus every guest (a prisoner is a guest of the player faction
// too). A row missing any of those facts leaves the count unknown, since an
// undercount would admit a joiner past the population target.
func hostedPopulation(custody domain.Fact[[]CustodyFacts]) domain.Fact[int64] {
	rows, known := custody.Value()
	if !known {
		return domain.Unknown[int64]()
	}
	var count int64
	for _, row := range rows {
		dead, dk := row.Dead.Value()
		admitted, ak := row.Admitted.Value()
		guest, gk := row.Guest.Value()
		if !dk || !ak || !gk {
			return domain.Unknown[int64]()
		}
		if !dead && (admitted || guest) {
			count++
		}
	}
	return domain.Known(count)
}

// spareBed reports whether an unowned humanlike, non-medical, non-prisoner
// bed reads back: the bed the joiner would sleep in.
func spareBed(sleeping domain.Fact[SleepingObservation]) domain.Fact[bool] {
	census, known := sleeping.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	for _, bed := range census.Beds {
		humanlike, hk := bed.Humanlike.Value()
		medical, mk := bed.Medical.Value()
		prisoners, pk := bed.Prisoners.Value()
		if hk && mk && pk && humanlike && !medical && !prisoners && len(bed.Owners) == 0 {
			return domain.Known(true)
		}
	}
	return domain.Known(false)
}

// JoinerCapacity reports whether the colony can host one more colonist:
// hosted population below domain.PopulationTarget, the food runway at or
// above JoinerFoodFloorDays and a spare bed. Any unknown ingredient leaves
// it unknown.
func JoinerCapacity(f JoinerCapacityFacts) domain.Fact[bool] {
	hosted, hk := hostedPopulation(f.Custody).Value()
	food, fk := f.FoodDays.Value()
	bed, bk := spareBed(f.Sleeping).Value()
	if !hk || !fk || !bk {
		return domain.Unknown[bool]()
	}
	return domain.Known(hosted < domain.PopulationTarget && food >= JoinerFoodFloorDays(hosted) && bed)
}

// JoinerDeficit reports whether an answerable joiner offer is waiting while
// the colony has capacity for it. An offer the colony cannot host is not a
// deficit: it is left to expire. An unknown census leaves the need unknown
// rather than silently settled, the same rule PrisonerRecruitDeficit uses.
func JoinerDeficit(offers domain.Fact[[]JoinerOffer], capacity domain.Fact[bool], facts RoundsFacts) domain.Fact[bool] {
	rows, known := offers.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	waiting := false
	for _, offer := range rows {
		waiting = waiting || joinerAnswerable(offer) && JoinerThreatReason(offer, facts) == ""
	}
	if !waiting {
		return domain.Known(false)
	}
	room, known := capacity.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(room)
}

// SelectJoinerMethod picks the lowest-ID answerable joiner offer to accept
// next, mirroring SelectPrisonerInteractionMethod's determinism, once the
// colony's capacity for one more colonist is known.
func SelectJoinerMethod(offers domain.Fact[[]JoinerOffer], capacity domain.Fact[bool], facts RoundsFacts) JoinerChoice {
	rows, known := offers.Value()
	if !known {
		return JoinerChoice{Reason: JoinerCensusUnknown}
	}
	sorted := append([]JoinerOffer{}, rows...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i].Quest < sorted[j].Quest })
	refused := false
	for _, offer := range sorted {
		if !joinerAnswerable(offer) {
			continue
		}
		room, known := capacity.Value()
		if !known {
			return JoinerChoice{Reason: JoinerCensusUnknown}
		}
		if !room {
			return JoinerChoice{Reason: JoinerNoCapacity}
		}
		if JoinerThreatReason(offer, facts) != "" {
			refused = true
			continue
		}
		choice := JoinerChoice{Quest: offer.Quest, RewardChoice: -1}
		if offer.ChoiceCount > 0 {
			choice.RewardChoice = 0
		}
		return choice
	}
	if refused {
		return JoinerChoice{Reason: JoinerNoCapacity}
	}
	return JoinerChoice{Reason: JoinerNoOffer}
}

// claimAnswerable reports whether an offer is a bestowing-ceremony quest the
// title claim gate allows (claims, ClaimQuests): not yet accepted,
// native-acceptable and needing no accepter. The quest is identified by the
// royalty read's quest id, not by script def.
func claimAnswerable(offer JoinerOffer, claims []domain.QuestID) bool {
	return slices.Contains(claims, offer.Quest) && offer.State == "NotYetAccepted" && offer.CanAccept && !offer.RequiresAccepter
}

// JoinerCapacity is the slice of a review's facts JoinerCapacity measures:
// the population census, the sleeping census, the population food runway
// (falling back to the stock runway, as the foothold food gate does).
func (f RoundsFacts) JoinerCapacity() JoinerCapacityFacts {
	return JoinerCapacityFacts{Custody: f.Custody, Sleeping: f.Sleeping, FoodDays: f.FoodDays}
}

// JoinerLetterOffer is a pending current-map WandererJoins or creepjoiner
// letter (#1740). Its opaque token binds the native pawn, quest, map,
// expiry and choices. Expires is the letter's disappearAtTick verbatim,
// negative for a letter with no timeout (the game's own sentinel).
type JoinerLetterOffer struct {
	ID          int32
	Token       string
	Pawn        domain.PawnID
	Expires     domain.Tick
	Label       string
	CanAccept   bool
	CreepJoiner bool
}

func SelectJoinerLetter(offers domain.Fact[[]JoinerLetterOffer], capacity domain.Fact[bool]) (JoinerLetterOffer, bool) {
	rows, known := offers.Value()
	room, capacityKnown := capacity.Value()
	if !known || !capacityKnown || !room {
		return JoinerLetterOffer{}, false
	}
	var chosen JoinerLetterOffer
	found := false
	for _, row := range rows {
		if row.CanAccept && (!found || row.ID < chosen.ID) {
			chosen, found = row, true
		}
	}
	return chosen, found
}
func JoinerLetterDeficit(offers domain.Fact[[]JoinerLetterOffer], capacity domain.Fact[bool]) domain.Fact[bool] {
	rows, known := offers.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	for _, row := range rows {
		if row.CanAccept {
			return capacity
		}
	}
	return domain.Known(false)
}
