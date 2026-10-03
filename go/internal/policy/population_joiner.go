package policy

import (
	"slices"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// JoinerOffer is one row of the visible quest census (bridge.QuestOffer,
// from rimgovernor/observations_read_world_progression) as MaintainPopulation
// reads it: enough to tell a joiner offer from every other quest and to
// build the QuestAccept write for it. The boundary re-reads the quest's CAS
// token and native CanAcceptQuest verdict immediately before dispatch; this
// only decides which already-observed offer to answer.
type JoinerOffer struct {
	Quest            domain.QuestID
	ScriptDef        string
	State            string
	CanAccept        bool
	RequiresAccepter bool
	ChoiceCount      int32
	// FactionID is the quest's first non-player faction ("" when none);
	// FactionHostile is that faction's FactionState.hostile, unknown when the
	// census carries no row for it. OnMap is true when the quest's map is
	// the colony's identity map. Favor is each reward choice's royal favor.
	FactionID      string
	FactionHostile domain.Fact[bool]
	OnMap          bool
	Favor          []QuestFavor
}

// QuestFavor is the royal favor one QuestAccept reward choice grants.
type QuestFavor struct {
	Choice int32
	Favor  int32
}

// JoinerPlanReason names why RoutinePopulationJoinerPlanner did or did not
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
	return strings.HasPrefix(scriptDef, "ThreatReward_") && strings.HasSuffix(scriptDef, "_Joiner")
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
func JoinerDeficit(offers domain.Fact[[]JoinerOffer], capacity domain.Fact[bool]) domain.Fact[bool] {
	rows, known := offers.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	waiting := false
	for _, offer := range rows {
		waiting = waiting || joinerAnswerable(offer)
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
func SelectJoinerMethod(offers domain.Fact[[]JoinerOffer], capacity domain.Fact[bool]) JoinerChoice {
	rows, known := offers.Value()
	if !known {
		return JoinerChoice{Reason: JoinerCensusUnknown}
	}
	sorted := append([]JoinerOffer{}, rows...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i].Quest < sorted[j].Quest })
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
		choice := JoinerChoice{Quest: offer.Quest, RewardChoice: -1}
		if offer.ChoiceCount > 0 {
			choice.RewardChoice = 0
		}
		return choice
	}
	return JoinerChoice{Reason: JoinerNoOffer}
}

// EmpireNoOffer is the reason SelectEmpireQuestMethod found nothing to accept.
const EmpireNoOffer JoinerPlanReason = "no_empire_offer"

// empireFavor is the most royal favor any one reward choice of the offer
// grants, with that choice's index (the lowest on a tie).
func empireFavor(offer JoinerOffer) (choice, favor int32) {
	for _, row := range offer.Favor {
		if row.Favor > favor || (row.Favor == favor && favor > 0 && row.Choice < choice) {
			choice, favor = row.Choice, row.Favor
		}
	}
	return choice, favor
}

// empireAnswerable reports whether an offer is an Empire quest worth
// accepting for favor: not yet accepted, not a joiner offer, native-
// acceptable (CanAcceptQuest includes every requirements-to-accept check, so
// an unaffordable quest reads can_accept=false), needing no accepter, from a
// faction known not to be hostile, on the colony's map, with a reward choice
// granting favor. Disclosed narrowing: favor granted outside a reward-choice
// part is not in the census, so such a quest is not chosen.
func empireAnswerable(offer JoinerOffer) bool {
	hostile, known := offer.FactionHostile.Value()
	_, favor := empireFavor(offer)
	return offer.State == "NotYetAccepted" && !IsJoinerOffer(offer.ScriptDef) && offer.CanAccept && !offer.RequiresAccepter &&
		offer.FactionID != "" && known && !hostile && offer.OnMap && favor > 0
}

// claimAnswerable reports whether an offer is a bestowing-ceremony quest the
// title claim gate allows (claims, ClaimQuests): not yet accepted,
// native-acceptable and needing no accepter. The quest is identified by the
// royalty read's quest id, not by script def.
func claimAnswerable(offer JoinerOffer, claims []domain.QuestID) bool {
	return slices.Contains(claims, offer.Quest) && offer.State == "NotYetAccepted" && offer.CanAccept && !offer.RequiresAccepter
}

// EmpireDeficit reports whether an answerable Empire quest (or an allowed
// title claim, claims) is waiting; an unknown census leaves it unknown.
func EmpireDeficit(offers domain.Fact[[]JoinerOffer], claims ...domain.QuestID) domain.Fact[bool] {
	rows, known := offers.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	for _, offer := range rows {
		if empireAnswerable(offer) || claimAnswerable(offer, claims) {
			return domain.Known(true)
		}
	}
	return domain.Known(false)
}

// SelectEmpireQuestMethod picks the Empire quest to accept through the
// existing QuestAccept write: first the bestowing-ceremony quest of an
// allowed title claim (claims; the lowest quest ID, reward choice -1 or the
// game's first option), else the answerable quest granting the most favor
// (lowest quest ID on a tie) and the reward choice that grants it.
func SelectEmpireQuestMethod(offers domain.Fact[[]JoinerOffer], claims ...domain.QuestID) JoinerChoice {
	rows, known := offers.Value()
	if !known {
		return JoinerChoice{Reason: JoinerCensusUnknown}
	}
	var claim *JoinerOffer
	for i, offer := range rows {
		if claimAnswerable(offer, claims) && (claim == nil || offer.Quest < claim.Quest) {
			claim = &rows[i]
		}
	}
	if claim != nil {
		choice := JoinerChoice{Quest: claim.Quest, RewardChoice: -1}
		if claim.ChoiceCount > 0 {
			choice.RewardChoice = 0
		}
		return choice
	}
	best, bestChoice, bestFavor := JoinerOffer{}, int32(0), int32(0)
	for _, offer := range rows {
		if !empireAnswerable(offer) {
			continue
		}
		choice, favor := empireFavor(offer)
		if favor > bestFavor || (favor == bestFavor && offer.Quest < best.Quest) {
			best, bestChoice, bestFavor = offer, choice, favor
		}
	}
	if bestFavor == 0 {
		return JoinerChoice{Reason: EmpireNoOffer}
	}
	return JoinerChoice{Quest: best.Quest, RewardChoice: bestChoice}
}

// JoinerCapacity is the slice of a review's facts JoinerCapacity measures:
// the population census, the sleeping census, the population food runway
// (falling back to the stock runway, as the foothold food gate does).
func (f RoutineFacts) JoinerCapacity() JoinerCapacityFacts {
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
