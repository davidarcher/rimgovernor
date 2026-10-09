package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	"google.golang.org/protobuf/proto"
)

// buildingAction is the Actions/Apply intent for a building action: place
// one blueprint of the building's definition, stuff and rotation at its
// cell. Native validates the placement at apply time and stamps what it
// places with the intent key (BuildingState.intent_key).
func buildingAction(action domain.Action) (*o.Action, error) {
	b, ok := action.Building()
	if !ok {
		return nil, contract("not a building action")
	}
	rotation := map[domain.Rotation]p.Rotation{domain.North: p.Rotation_ROTATION_NORTH, domain.East: p.Rotation_ROTATION_EAST, domain.South: p.Rotation_ROTATION_SOUTH, domain.West: p.Rotation_ROTATION_WEST}[b.Rotation()]
	if rotation == 0 || validID(b.Definition()) != nil {
		return nil, contract("building intent requires a definition and a cardinal rotation")
	}
	candidate := &p.PlacementCandidate{DefName: proto.String(b.Definition()), Stuff: proto.String(b.Stuff()), X: proto.Int32(b.Cell().X), Z: proto.Int32(b.Cell().Z), Rotation: rotation.Enum()}
	intent := &o.BuildingIntent{Placement: candidate}
	if minimum, known := action.FinishingSkill().Value(); known {
		intent.MinimumFinishingSkill = proto.Int32(int32(minimum))
	}
	if tier, known := action.Tier().Value(); known {
		intent.Tier = proto.Int32(int32(tier))
	}
	if action.ConstructionTarget() != "" {
		intent.ExistingTargetId = proto.String(action.ConstructionTarget())
	}
	if action.ReplacesWall() {
		intent.ReplaceWall = proto.Bool(true)
	}
	return &o.Action{Intent: &o.Action_Building{Building: intent}}, nil
}
