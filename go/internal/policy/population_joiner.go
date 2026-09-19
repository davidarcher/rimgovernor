package policy

import (
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
// CustodyFacts reads), the sleeping census for a spare non-medical bed, the
// food runway in days and the player's PopulationPolicy. Without a policy
// the colony has no declared capacity and no offer is ever answered.
type JoinerCapacityFacts struct {
	Custody      domain.Fact[[]CustodyFacts]
	Sleeping     domain.Fact[SleepingObservation]
	FoodDays     domain.Fact[float64]
	Policy       domain.Fact[domain.PopulationPolicy]
	RaidPoints   domain.Fact[float64]
	DefenseTiers domain.Fact[int]
}

// joinerRaidPointIncrement uses the conservative upper end of the vanilla
// 15–200 pawn-point range across wealth bands. It is a policy allowance,
// not a prediction of the storyteller's final raid strength.
const joinerRaidPointIncrement = 200.0

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
// undercount would admit a joiner past the policy maximum.
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

// JoinerCapacity reports whether the colony can host one more colonist
// under its population policy: hosted population below the maximum, the
// food runway at or above the policy's reserve days and a spare bed. Any
// unknown capacity ingredient leaves it unknown; an unset policy is a known no.
// The optional raid threshold refuses an undefended join only when both raid
// points and built defense tiers are known; unknown threat facts add no veto.
func JoinerCapacity(f JoinerCapacityFacts) domain.Fact[bool] {
	p, known := f.Policy.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	if !p.Set() {
		return domain.Known(false)
	}
	hosted, hk := hostedPopulation(f.Custody).Value()
	food, fk := f.FoodDays.Value()
	bed, bk := spareBed(f.Sleeping).Value()
	if !hk || !fk || !bk {
		return domain.Unknown[bool]()
	}
	points, pk := f.RaidPoints.Value()
	tiers, tk := f.DefenseTiers.Value()
	if p.RaidThreshold() > 0 && pk && tk && tiers == 0 && points+joinerRaidPointIncrement > p.RaidThreshold() {
		return domain.Known(false)
	}
	return domain.Known(hosted < int64(p.Maximum()) && food >= p.FoodDays() && bed)
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

// JoinerCapacity is the slice of a review's facts JoinerCapacity measures:
// the population census, the sleeping census, the population food runway
// (falling back to the stock runway, as the foothold food gate does) and the
// player's policy.
func (f RoutineFacts) JoinerCapacity() JoinerCapacityFacts {
	return JoinerCapacityFacts{Custody: f.Custody, Sleeping: f.Sleeping, FoodDays: fallback(f.PopulationFoodDays, f.FoodDays), Policy: f.PopulationCapacity, RaidPoints: f.RaidPoints, DefenseTiers: f.DefenseTiers}
}

// JoinerLetterOffer is a pending current-map WandererJoins letter. Its opaque
// token binds the native pawn, quest, map, expiry and choices.
type JoinerLetterOffer struct {
	ID        int32
	Token     string
	Pawn      domain.PawnID
	Expires   domain.Tick
	Label     string
	CanAccept bool
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
