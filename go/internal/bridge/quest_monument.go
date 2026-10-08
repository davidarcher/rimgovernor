package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// Partial marker identities cannot become executable construction targets.
func validatedQuestMonument(m *o.QuestMonument) *o.QuestMonument {
	if m == nil || m.GetMarkerId() == "" || m.GetDefName() == "" || m.MapId == nil || m.GetMapId() < 0 || m.Packed == nil || m.Installed == nil || m.GetPacked() == m.GetInstalled() {
		return nil
	}
	if m.GetInstalled() && !monumentCellKnown(m.Cell) {
		return nil
	}
	for _, p := range m.Pieces {
		if p == nil || p.GetDefName() == "" || !monumentCellKnown(p.Offset) || p.Rotation == nil || p.GetRotation() < 0 || p.GetRotation() > 3 || len(p.Footprint) == 0 {
			return nil
		}
		for _, cell := range p.Footprint {
			if !monumentCellKnown(cell) {
				return nil
			}
		}
	}
	for _, r := range m.Resources {
		if r == nil || r.GetId() == "" || r.GetDefName() == "" || !monumentCellKnown(r.Cell) {
			return nil
		}
	}
	for _, c := range m.InstallCells {
		if !monumentCellKnown(c) || c.GetX() < 0 || c.GetZ() < 0 {
			return nil
		}
	}
	return proto.Clone(m).(*o.QuestMonument)
}

func monumentCellKnown(cell *c.Cell) bool { return cell != nil && cell.X != nil && cell.Z != nil }
