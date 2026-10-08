package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

func questShuttleAction(action domain.Action) (*o.Action, error) {
	s, ok := action.QuestShuttle()
	if !ok {
		return nil, contract("not a quest shuttle action")
	}
	if _, err := domain.NewQuestShuttleAction(action.ID(), s); err != nil {
		return nil, contract("invalid quest shuttle")
	}
	intent := &o.QuestShuttleIntent{QuestId: proto.String(string(s.Quest())), Launch: proto.Bool(s.Launch())}
	switch s.Loading() {
	case domain.ShuttleAutoload:
		intent.Loading = &o.QuestShuttleIntent_Autoload{Autoload: s.Autoload()}
	case domain.ShuttleExplicitPawns:
		ids := []string{}
		for _, pawn := range s.Pawns() {
			ids = append(ids, string(pawn))
		}
		intent.Loading = &o.QuestShuttleIntent_ExplicitPawns{ExplicitPawns: &o.ShuttlePawnList{PawnIds: ids}}
	}
	return &o.Action{Intent: &o.Action_QuestShuttle{QuestShuttle: intent}}, nil
}
