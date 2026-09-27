package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// wallRemovalAction is the Actions/Apply remove_wall arm of one stone-shell
// wall removal (#989): the wall's cell, and for the original demolition the
// wall identity the proposal observed. Native resolves the site live.
func wallRemovalAction(action domain.Action) (*o.Action, error) {
	w, ok := action.WallRemoval()
	if !ok {
		return nil, contract("not a wall removal action")
	}
	cell := w.Cell()
	intent := &o.RemoveWallIntent{Cell: &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)}}
	if w.Original() != "" {
		intent.ExpectedWallId = proto.String(w.Original())
	}
	return &o.Action{Intent: &o.Action_RemoveWall{RemoveWall: intent}}, nil
}
