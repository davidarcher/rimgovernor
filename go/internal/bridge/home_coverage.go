package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// homeAction is the HomeIntent of one Home extension. Native recomputes the
// facility's batch live when it applies; the planner's shape token is the
// method identity only and is not sent.
func homeAction(action domain.Action) (*o.Action, error) {
	v, ok := action.HomeCoverage()
	if !ok {
		return nil, contract("not a home coverage action")
	}
	if err := validID(v.Target()); err != nil {
		return nil, err
	}
	return &o.Action{Intent: &o.Action_Home{Home: &o.HomeIntent{TargetId: proto.String(v.Target())}}}, nil
}
