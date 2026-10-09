package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// gatheringAction is the Actions/Apply gathering arm: the organizer starts one
// vanilla GatheringDef. The intent names no spot; the game chooses it.
func gatheringAction(action domain.Action) (*o.Action, error) {
	gathering, ok := action.Gathering()
	if !ok {
		return nil, contract("not a gathering action")
	}
	if _, err := domain.NewGathering(gathering.Def(), gathering.Organizer()); err != nil {
		return nil, contract("gathering: %v", err)
	}
	return &o.Action{Intent: &o.Action_Gathering{Gathering: &o.GatheringIntent{GatheringDef: proto.String(gathering.Def()), Organizer: NewRef(string(gathering.Organizer()))}}}, nil
}
