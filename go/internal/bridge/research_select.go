package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// researchAction is the ResearchIntent of one research selection. Native
// judges the project against live research state when it applies.
func researchAction(action domain.Action) (*o.Action, error) {
	v, ok := action.ResearchSelect()
	if !ok {
		return nil, contract("not a research select action")
	}
	if err := validID(v.Project()); err != nil {
		return nil, err
	}
	return &o.Action{Intent: &o.Action_Research{Research: &o.ResearchIntent{ProjectDef: proto.String(v.Project())}}}, nil
}
