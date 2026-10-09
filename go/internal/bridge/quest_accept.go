package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// questAcceptAction is the AcceptQuestIntent of one quest acceptance:
// the quest, the accepter when the quest requires one, and the reward
// option; native checks the game's own acceptance rules when it applies.
func questAcceptAction(action domain.Action) (*o.Action, error) {
	accept, ok := action.QuestAccept()
	if !ok {
		return nil, contract("not a quest accept action")
	}
	if validID(string(accept.Quest())) != nil || (accept.AccepterPawn() != "" && validID(string(accept.AccepterPawn())) != nil) || accept.RewardChoice() < -1 {
		return nil, contract("quest accept requires a quest id")
	}
	intent := &o.AcceptQuestIntent{QuestId: proto.String(string(accept.Quest())), RewardChoice: proto.Int32(accept.RewardChoice())}
	if accept.AccepterPawn() != "" {
		intent.AccepterPawnId = proto.String(string(accept.AccepterPawn()))
	}
	return &o.Action{Intent: &o.Action_AcceptQuest{AcceptQuest: intent}}, nil
}
