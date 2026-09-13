package domain

import (
	"errors"
	"strings"
)

// crewSeparator joins a SettlementGift's crew roster into one comparable
// string field (Action, and every domain action it embeds, must stay a
// comparable struct: the executor package compares Progress.Action() with
// == throughout). It uses the ASCII unit separator, a control character
// validID already forbids nowhere but which no native load ID ever contains,
// rather than a printable delimiter an adversarial or unusual ID could
// collide with.
const crewSeparator = "\x1f"

// CaravanID identifies one native world-object caravan by its exact load ID
// (Caravan.GetUniqueLoadID()).
type CaravanID string

// SettlementID identifies one native world-object settlement by its exact
// load ID (Settlement.GetUniqueLoadID()).
type SettlementID string

// FactionID identifies one native faction by its exact load ID
// (Faction.GetUniqueLoadID()).
type FactionID string

// SettlementGift is explicit intent to gift an exact silver amount from one
// already-observed, already-visiting caravan to the exact faction of the
// settlement it currently sits at. Settlement and Faction are the intended
// target, established as the caravan's one currently-visited settlement at
// inspection, not chosen here -- the same split QuestAccept uses for its
// quest. CrewIDs is the exact expected crew roster (native rejects a
// membership mismatch, the same way as a stale-identity write); Silver is
// the exact amount, never a proportion or "as much as possible" -- native
// itself refuses when it cannot adjust the trade row to the exact amount.
// This action never decides whether a gift is worthwhile (goodwill math,
// reserve sizing); a planner upstream of this boundary makes that choice
// conservatively.
type SettlementGift struct {
	caravan    CaravanID
	settlement SettlementID
	faction    FactionID
	crew       string
	silver     int32
}

func NewSettlementGift(caravan CaravanID, settlement SettlementID, faction FactionID, crewIDs []PawnID, silver int32) (SettlementGift, error) {
	if !validID(string(caravan)) {
		return SettlementGift{}, errors.New("settlement gift requires a valid caravan identity")
	}
	if !validID(string(settlement)) {
		return SettlementGift{}, errors.New("settlement gift requires a valid settlement identity")
	}
	if !validID(string(faction)) {
		return SettlementGift{}, errors.New("settlement gift requires a valid faction identity")
	}
	if len(crewIDs) == 0 || len(crewIDs) > 64 {
		return SettlementGift{}, errors.New("invalid settlement gift crew")
	}
	seen := make(map[PawnID]bool, len(crewIDs))
	joined := make([]string, len(crewIDs))
	for i, pawn := range crewIDs {
		if !validID(string(pawn)) || strings.Contains(string(pawn), crewSeparator) || seen[pawn] {
			return SettlementGift{}, errors.New("invalid or duplicate settlement gift crew")
		}
		seen[pawn] = true
		joined[i] = string(pawn)
	}
	if silver <= 0 {
		return SettlementGift{}, errors.New("invalid settlement gift silver")
	}
	crew := strings.Join(joined, crewSeparator)
	return SettlementGift{caravan: caravan, settlement: settlement, faction: faction, crew: crew, silver: silver}, nil
}

func (g SettlementGift) Caravan() CaravanID       { return g.caravan }
func (g SettlementGift) Settlement() SettlementID { return g.settlement }
func (g SettlementGift) Faction() FactionID       { return g.faction }

func (g SettlementGift) CrewIDs() []PawnID {
	parts := strings.Split(g.crew, crewSeparator)
	crew := make([]PawnID, len(parts))
	for i, part := range parts {
		crew[i] = PawnID(part)
	}
	return crew
}

func (g SettlementGift) Silver() int32 { return g.silver }

const SettlementGiftAction ActionKind = "settlement_gift"

func NewSettlementGiftAction(id ActionID, gift SettlementGift) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewSettlementGift(gift.caravan, gift.settlement, gift.faction, gift.CrewIDs(), gift.silver); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: SettlementGiftAction, settlementGift: gift}, nil
}

func (a Action) SettlementGift() (SettlementGift, bool) {
	return a.settlementGift, a.kind == SettlementGiftAction
}
