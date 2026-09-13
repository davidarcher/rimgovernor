package domain

import (
	"errors"
	"strings"
)

// QuestFulfill is explicit intent to fulfill one already-observed quest's
// single native settlement trade-request objective (QuestPart_
// InitiateTradeRequest), using an already-observed, already-visiting
// caravan. Unlike QuestAccept this is not a direct settings write: native
// drives an actual TradeRequestComp caravan gizmo callback and its
// confirmation dialog under the hood (QuestFulfillmentTool.cs's legacy
// mechanism, ported behind the typed boundary with no UI/camera
// dependency). This action never selects which quest or caravan to use, nor
// does it compute resource sufficiency -- native re-derives and re-checks
// the exact requested resource/count against the live TradeRequestComp at
// admission time, exactly as the legacy tool did; a planner upstream of
// this boundary makes the selection conservatively and native alone decides
// eligibility from there.
type QuestFulfill struct {
	quest   QuestID
	caravan CaravanID
	crew    string
}

func NewQuestFulfill(quest QuestID, caravan CaravanID, crewIDs []PawnID) (QuestFulfill, error) {
	if !validID(string(quest)) {
		return QuestFulfill{}, errors.New("quest fulfill requires a valid quest identity")
	}
	if !validID(string(caravan)) {
		return QuestFulfill{}, errors.New("quest fulfill requires a valid caravan identity")
	}
	if len(crewIDs) == 0 || len(crewIDs) > 64 {
		return QuestFulfill{}, errors.New("invalid quest fulfill crew")
	}
	seen := make(map[PawnID]bool, len(crewIDs))
	joined := make([]string, len(crewIDs))
	for i, pawn := range crewIDs {
		if !validID(string(pawn)) || strings.Contains(string(pawn), crewSeparator) || seen[pawn] {
			return QuestFulfill{}, errors.New("invalid or duplicate quest fulfill crew")
		}
		seen[pawn] = true
		joined[i] = string(pawn)
	}
	crew := strings.Join(joined, crewSeparator)
	return QuestFulfill{quest: quest, caravan: caravan, crew: crew}, nil
}

func (f QuestFulfill) Quest() QuestID     { return f.quest }
func (f QuestFulfill) Caravan() CaravanID { return f.caravan }

func (f QuestFulfill) CrewIDs() []PawnID {
	parts := strings.Split(f.crew, crewSeparator)
	crew := make([]PawnID, len(parts))
	for i, part := range parts {
		crew[i] = PawnID(part)
	}
	return crew
}

const QuestFulfillAction ActionKind = "quest_fulfill"

func NewQuestFulfillAction(id ActionID, fulfill QuestFulfill) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewQuestFulfill(fulfill.quest, fulfill.caravan, fulfill.CrewIDs()); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: QuestFulfillAction, questFulfill: fulfill}, nil
}

func (a Action) QuestFulfill() (QuestFulfill, bool) {
	return a.questFulfill, a.kind == QuestFulfillAction
}
