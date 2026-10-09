package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	"google.golang.org/protobuf/proto"
)

var moveRotations = map[domain.Rotation]p.Rotation{domain.North: p.Rotation_ROTATION_NORTH, domain.East: p.Rotation_ROTATION_EAST, domain.South: p.Rotation_ROTATION_SOUTH, domain.West: p.Rotation_ROTATION_WEST}

// relocateAction is the RelocateIntent of a move (the game's
// reinstall blueprint, or its install blueprint for a packed item's inner
// id) or an uninstall (the Uninstall designation where the
// building stands). Native checks every rule live; applied means ordered
// and the next building read decides progress (NativeMoveBuilding.cs).
func relocateAction(action domain.Action) (*o.Action, error) {
	m, uninstall, ok := action.Relocation()
	if !ok {
		return nil, contract("not a relocation action")
	}
	if _, err := domain.NewMoveBuilding(m.Thing(), m.Definition(), m.Cell(), m.Rotation()); err != nil {
		return nil, contract("relocate intent requires a building, a destination and a rotation")
	}
	return &o.Action{Intent: &o.Action_Relocate{Relocate: &o.RelocateIntent{
		ThingId:     proto.String(m.Thing()),
		Destination: &c.Cell{X: proto.Int32(m.Cell().X), Z: proto.Int32(m.Cell().Z)},
		Rotation:    moveRotations[m.Rotation()].Enum(),
		Uninstall:   proto.Bool(uninstall)}}}, nil
}
