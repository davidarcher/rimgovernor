package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// QuestScope says whether a colony that never builds or launches a
// gravship can finish a quest (epic #1707: ground only). The catalog
// classifies it from the quest's QuestScriptDef row (bridge.QuestClass), never
// from a def-name list.
type QuestScope string

const (
	QuestSkipNeedsRemoteSiteHold QuestSkipReason = "needs_remote_site_hold"
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

// QuestSkipReason names why an Odyssey offer waiting to be accepted is
// not accepted. Every skip is logged by the reviewer, once per quest and
// reason.
type QuestSkipReason string

const (
	// QuestSkipShipOnly: the quest needs a space layer (QuestClass.SpaceLayer).
	QuestSkipShipOnly QuestSkipReason = "ship_only"
	// QuestSkipClassUnknown: the catalog could not classify the quest (no
	// row, or a layer reference that names no planet layer def); unknown
	// stays unknown, so the quest is not accepted.
	QuestSkipClassUnknown QuestSkipReason = "class_unknown"
	// QuestSkipRewardChoices: a ground quest with several reward choices;
	// no source says which one a ground colony wants.
	QuestSkipRewardChoices QuestSkipReason = "reward_choices"
	// QuestSkipNeedsAccepter: a ground quest that names an accepter pawn;
	// none is chosen here.
	QuestSkipNeedsAccepter QuestSkipReason = "needs_accepter"
)

// QuestSkip is one waiting Odyssey offer that is not accepted and why.
type QuestSkip struct {
	Quest     domain.QuestID
	ScriptDef string
	Reason    QuestSkipReason
	// Detail is the space layer of a ship-only quest, or why the catalog
	// could not classify an unknown one.
	Detail string
}
