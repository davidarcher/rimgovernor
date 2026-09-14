package domain

import "errors"

// QuestID identifies one native quest by its exact load ID
// (Quest.GetUniqueLoadID()).
type QuestID string

// QuestAccept is explicit intent to accept one already-observed quest offer.
// AccepterPawn only matters when the quest RequiresAccepter (native rejects a
// mismatch); RewardChoice selects the exact option index of a quest's single
// QuestPart_Choice (any of its N options, not just a lone default), or -1
// when the quest carries none. Native eligibility (CanAcceptQuest,
// CanPawnAcceptQuest, at most one native choice part, the index falling
// within the currently observed option count) is established at inspection,
// not here -- the same split PrisonerInteraction and Husbandry use. This
// action never selects which quest to accept, nor which reward is "best"; a
// player command upstream of this boundary makes both choices explicitly.
type QuestAccept struct {
	quest        QuestID
	accepterPawn PawnID
	rewardChoice int32
}

func NewQuestAccept(quest QuestID, accepterPawn PawnID, rewardChoice int32) (QuestAccept, error) {
	if !validID(string(quest)) {
		return QuestAccept{}, errors.New("quest accept requires a valid quest identity")
	}
	if accepterPawn != "" && !validID(string(accepterPawn)) {
		return QuestAccept{}, errors.New("invalid quest accept accepter pawn identity")
	}
	if rewardChoice < -1 {
		return QuestAccept{}, errors.New("invalid quest accept reward choice")
	}
	return QuestAccept{quest: quest, accepterPawn: accepterPawn, rewardChoice: rewardChoice}, nil
}

func (q QuestAccept) Quest() QuestID       { return q.quest }
func (q QuestAccept) AccepterPawn() PawnID { return q.accepterPawn }

// RewardChoice is -1 when the quest carries no reward-choice part.
func (q QuestAccept) RewardChoice() int32 { return q.rewardChoice }

const QuestAcceptAction ActionKind = "quest_accept"

func NewQuestAcceptAction(id ActionID, accept QuestAccept) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewQuestAccept(accept.quest, accept.accepterPawn, accept.rewardChoice); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: QuestAcceptAction, questAccept: accept}, nil
}

func (a Action) QuestAccept() (QuestAccept, bool) {
	return a.questAccept, a.kind == QuestAcceptAction
}
