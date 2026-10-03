package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// QuestScope says whether a colony that never builds or launches a
// gravship can finish a quest (epic #1707: ground only). The catalog
// classifies it from the quest's QuestScriptDef row (bridge.QuestClass), never
// from a def-name list.
type QuestScope string

const (
	// QuestScopeOther: the quest is not defined by Odyssey; the Odyssey
	// selector leaves it to the joiner and Empire selectors.
	QuestScopeOther QuestScope = "other"
	// QuestScopeGround: an Odyssey quest that names no space planet layer.
	QuestScopeGround QuestScope = "ground"
	// QuestScopeShipOnly: an Odyssey quest that needs a space planet layer
	// (orbit), which only a ship, shuttle or pod reaches.
	QuestScopeShipOnly QuestScope = "ship_only"
)

// QuestClass is one quest root's classification. SpaceLayer names the space
// planet layer that makes a ship-only quest so.
type QuestClass struct {
	Scope      QuestScope
	SpaceLayer string
}

// OdysseySkipReason names why an Odyssey offer waiting to be accepted is
// not accepted. Every skip is logged by the reviewer, once per quest and
// reason.
type OdysseySkipReason string

const (
	// OdysseySkipShipOnly: the quest needs a space layer (QuestClass.SpaceLayer).
	OdysseySkipShipOnly OdysseySkipReason = "ship_only"
	// OdysseySkipClassUnknown: the catalog could not classify the quest (no
	// row, or a layer reference that names no planet layer def); unknown
	// stays unknown, so the quest is not accepted.
	OdysseySkipClassUnknown OdysseySkipReason = "class_unknown"
	// OdysseySkipRewardChoices: a ground quest with several reward choices;
	// no source says which one a ground colony wants.
	OdysseySkipRewardChoices OdysseySkipReason = "reward_choices"
	// OdysseySkipNeedsAccepter: a ground quest that names an accepter pawn;
	// none is chosen here.
	OdysseySkipNeedsAccepter OdysseySkipReason = "needs_accepter"
)

// OdysseyNoOffer is the reason SelectOdysseyQuestMethod found nothing to accept.
const OdysseyNoOffer JoinerPlanReason = "no_odyssey_offer"

// OdysseySkip is one waiting Odyssey offer that is not accepted and why.
type OdysseySkip struct {
	Quest     domain.QuestID
	ScriptDef string
	Reason    OdysseySkipReason
	// Detail is the space layer of a ship-only quest, or why the catalog
	// could not classify an unknown one.
	Detail string
}

// odysseyDecision decides one offer: accept it, skip it with a reason, or
// (neither) leave it alone. Only a not-yet-accepted quest is decided; the
// game accepts an autoAccept quest itself, so those never wait here.
func odysseyDecision(offer JoinerOffer) (accept bool, skip *OdysseySkip) {
	if offer.State != "NotYetAccepted" {
		return false, nil
	}
	class, known := offer.Class.Value()
	if !known {
		return false, &OdysseySkip{Quest: offer.Quest, ScriptDef: offer.ScriptDef, Reason: OdysseySkipClassUnknown, Detail: offer.ClassError}
	}
	switch class.Scope {
	case QuestScopeOther:
		return false, nil
	case QuestScopeShipOnly:
		return false, &OdysseySkip{Quest: offer.Quest, ScriptDef: offer.ScriptDef, Reason: OdysseySkipShipOnly, Detail: class.SpaceLayer}
	case QuestScopeGround:
	default:
		return false, &OdysseySkip{Quest: offer.Quest, ScriptDef: offer.ScriptDef, Reason: OdysseySkipClassUnknown}
	}
	switch {
	case offer.ChoiceCount > 1:
		return false, &OdysseySkip{Quest: offer.Quest, ScriptDef: offer.ScriptDef, Reason: OdysseySkipRewardChoices}
	case offer.RequiresAccepter:
		return false, &OdysseySkip{Quest: offer.Quest, ScriptDef: offer.ScriptDef, Reason: OdysseySkipNeedsAccepter}
	}
	// CanAccept covers every requirements-to-accept check (research, layer),
	// so a ground quest the game will not take yet simply waits.
	return offer.CanAccept, nil
}

// OdysseySkips lists every waiting Odyssey offer that is not accepted, in
// quest ID order; an unknown census lists none.
func OdysseySkips(offers domain.Fact[[]JoinerOffer]) []OdysseySkip {
	rows, known := offers.Value()
	if !known {
		return nil
	}
	var out []OdysseySkip
	for _, offer := range rows {
		if _, skip := odysseyDecision(offer); skip != nil {
			out = append(out, *skip)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Quest < out[j].Quest })
	return out
}

// OdysseyDeficit reports whether a ground Odyssey quest waits to be
// accepted; an unknown census leaves it unknown. A skipped offer is no
// deficit: there is nothing for the colony to do about it.
func OdysseyDeficit(offers domain.Fact[[]JoinerOffer]) domain.Fact[bool] {
	rows, known := offers.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	for _, offer := range rows {
		if accept, _ := odysseyDecision(offer); accept {
			return domain.Known(true)
		}
	}
	return domain.Known(false)
}

// SelectOdysseyQuestMethod picks the lowest-ID ground Odyssey quest to
// accept through the existing QuestAccept write. The reward index is -1
// without a reward-choice part and 0 for its single option.
func SelectOdysseyQuestMethod(offers domain.Fact[[]JoinerOffer]) JoinerChoice {
	rows, known := offers.Value()
	if !known {
		return JoinerChoice{Reason: JoinerCensusUnknown}
	}
	best := JoinerOffer{}
	for _, offer := range rows {
		if accept, _ := odysseyDecision(offer); accept && (best.Quest == "" || offer.Quest < best.Quest) {
			best = offer
		}
	}
	if best.Quest == "" {
		return JoinerChoice{Reason: OdysseyNoOffer}
	}
	choice := JoinerChoice{Quest: best.Quest, RewardChoice: -1}
	if best.ChoiceCount > 0 {
		choice.RewardChoice = 0
	}
	return choice
}
