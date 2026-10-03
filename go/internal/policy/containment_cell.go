package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The containment cell (#1741, epic #1694): one planned room holding one
// holding platform, owed while an entity the game lets the colony capture
// has no platform that can hold it. It is staged like the worship room
// (worship.go, layout_child_rooms.go): the layout review grows a core room
// sized to the platform, MaintainHousing shells it and places the platform.
// The platform is the catalog's own (a ThingDef with an entity-holder
// platform comp), never a name here. A cell is owed only when its predicted
// containment strength (ContainmentDefs.Predict) reaches what the entity
// needs; how much margin to keep is the capture rule's (#1742), and the
// standing platform's native strength (EntityHolder.ContainmentStrength) is
// the check once it is built. Capture itself is not planned here.

// ShellWallDefinition and ShellDoorDefinition are the defs a planned room's
// shell is built from (the sleeping planner's shellRoom); the prediction
// reads their hit points.
const (
	ShellWallDefinition = "Wall"
	ShellDoorDefinition = "Door"
)

// ModuleContainmentCell is the containment cell's plan role.
const ModuleContainmentCell ModuleRole = "containment-cell"

// ContainmentDemand is what the colony's entities ask of a cell: how many
// living entities the game lets the colony capture that no platform holds
// (HeldState.can_be_captured, not held) and the highest containment strength
// any of them needs (its MinimumContainmentStrength).
type ContainmentDemand struct {
	Entities int
	Required float64
}

// BuiltHolder is a standing holding platform's native facts.
type BuiltHolder struct {
	Strength float64
	// Available is whether the platform can take a pawn now.
	Available bool
}

// ContainmentPlanning is the projection's containment inputs. Each unknown
// fact stops the cell with its own reason.
type ContainmentPlanning struct {
	Demand domain.Fact[ContainmentDemand]
	// Holders are the standing platforms; unknown without a building census.
	Holders domain.Fact[[]BuiltHolder]
	// Defs are the prediction's inputs; DefsReason says in plain English
	// why they are unknown.
	Defs       domain.Fact[ContainmentDefs]
	DefsReason string
}

// ContainmentVerdict is whether a cell is owed and, when demand exists but a
// cell is not owed, why in plain English.
type ContainmentVerdict struct {
	Owed   bool
	Reason string
}

// ContainmentCellNeed is the cell the entities owe: one platform in its own
// room. No demand, or a standing available platform whose native strength
// reaches the demand, owes nothing; an unknown fact, an unavailable
// platform, or a design whose predicted strength falls short owes nothing
// and says why.
func ContainmentCellNeed(p ContainmentPlanning, furniture []FurnitureDefinition) (ChildRoomNeed, ContainmentVerdict) {
	demand, ok := p.Demand.Value()
	if !ok {
		return ChildRoomNeed{}, ContainmentVerdict{Reason: "the entities' capture and containment facts are unread"}
	}
	if demand.Entities == 0 {
		return ChildRoomNeed{}, ContainmentVerdict{}
	}
	holders, ok := p.Holders.Value()
	if !ok {
		return ChildRoomNeed{}, ContainmentVerdict{Reason: "the standing holding platforms are unread"}
	}
	for _, h := range holders {
		if h.Available && h.Strength >= demand.Required {
			return ChildRoomNeed{}, ContainmentVerdict{}
		}
	}
	defs, ok := p.Defs.Value()
	if !ok {
		return ChildRoomNeed{}, ContainmentVerdict{Reason: "the containment inputs cannot be read from the definitions: " + p.DefsReason}
	}
	strength, err := defs.Predict(ContainmentRoom{})
	if err != nil {
		return ChildRoomNeed{}, ContainmentVerdict{Reason: "the cell's strength cannot be predicted: " + err.Error()}
	}
	if strength < demand.Required {
		return ChildRoomNeed{}, ContainmentVerdict{Reason: fmt.Sprintf("a cell of %s with the planned walls and door holds at most %.1f containment strength and an entity needs %.1f; facilities that add strength are not planned", defs.Holder, strength, demand.Required)}
	}
	need := ChildRoomNeed{Role: RoomRoleContainmentCell, Module: ModuleContainmentCell, Furniture: []ChildFurniture{{Defs: []string{defs.Holder}, Count: 1}}}
	if _, ok := need.resolve(furniture); !ok {
		return ChildRoomNeed{}, ContainmentVerdict{Reason: "the holding platform " + defs.Holder + " is not buildable yet or its size is unknown"}
	}
	return need, ContainmentVerdict{Owed: true}
}
