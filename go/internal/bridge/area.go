package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

var areaOperations = map[domain.AreaOperation]op.AreaOperation{
	domain.AreaCreate:     op.AreaOperation_AREA_OPERATION_CREATE,
	domain.AreaSetCells:   op.AreaOperation_AREA_OPERATION_SET_CELLS,
	domain.AreaClearCells: op.AreaOperation_AREA_OPERATION_CLEAR_CELLS,
	domain.AreaDelete:     op.AreaOperation_AREA_OPERATION_DELETE,
}

// areaAction is the AreaIntent of one bot-owned allowed area or the home
// area (#1321). Native resolves the bot area by its key
// label and never touches a player's area (NativeAreaIntent.cs).
func areaAction(action domain.Action) (*op.Action, error) {
	v, ok := action.Area()
	if !ok {
		return nil, contract("not an area action")
	}
	if _, err := domain.NewAreaAction(action.ID(), v); err != nil {
		return nil, contract("%v", err)
	}
	intent := &op.AreaIntent{Operation: areaOperations[v.Operation()].Enum()}
	if v.PollutionClear() {
		intent.PollutionClear = proto.Bool(true)
	} else if v.Home() {
		intent.Home = proto.Bool(true)
	} else {
		intent.Key = proto.String(v.Key())
	}
	for _, r := range v.Rects() {
		intent.Rects = append(intent.Rects, &op.AreaRect{MinX: proto.Int32(r.MinX), MinZ: proto.Int32(r.MinZ), MaxX: proto.Int32(r.MaxX), MaxZ: proto.Int32(r.MaxZ)})
	}
	return &op.Action{Intent: &op.Action_Area{Area: intent}}, nil
}
