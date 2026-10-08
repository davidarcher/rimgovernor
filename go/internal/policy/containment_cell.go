package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The containment cell (#1741, epic #1694): one planned room holding one
// holding platform, owed while an entity the game lets the colony capture
// has no platform that can hold it. It is staged like the worship room
// (worship.go, layout_child_rooms.go): the layout review grows a core room
// sized to the platform, MaintainHousing reconciles it to its template (the
// platform and its lamp, NextChildRoomStep).
// The platform is the catalog's own (a ThingDef with an entity-holder
// platform comp), never a name here. A cell is owed only when its predicted
// containment strength (ContainmentDefs.Predict) reaches what the entity
// needs; how much margin to keep is the capture rule's (#1742), and the
// standing platform's native strength (EntityHolder.ContainmentStrength) is
// the check once it is built. Capture itself is not planned here.
// Bioferrite plate floor (#2435): when the stock pays for every tile of the
// room, the prediction counts the catalog's containment floor in place of the
// plain floor, and the flooring review lays it (WantedFloors).

// ShellWallDefinition and ShellDoorDefinition are the defs a planned room's
// shell is built from (reconcileRoom); the prediction
// reads their hit points.
const (
	ShellWallDefinition = "Wall"
	ShellDoorDefinition = "Door"
)

// ContainmentLampDefinition is the lamp the cell is furnished with (#1743):
// the same standing lamp that stands beside a bed or throne.
const ContainmentLampDefinition = standingLampDef

// PlannedContainmentCell is the containment cell's plan role.
const PlannedContainmentCell PlannedRole = "containment-cell"

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
	// HeldPawn is the pawn on the platform ("" when none) and Doors the
	// doors of its room (#1743).
	HeldPawn string
	Doors    domain.Fact[[]ContainmentDoor]
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
	// Entities are the entity pawn rows the capture rule (#1742) decides;
	// unknown without Anomaly.
	Entities domain.Fact[[]CapturableEntity]
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
func ContainmentCellNeed(p ContainmentPlanning, furniture []FurnitureDefinition, floors FlooringFacts) (ChildRoomNeed, ContainmentVerdict) {
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
	defs, ok := p.Defs.Value()
	if !ok {
		return ChildRoomNeed{}, ContainmentVerdict{Reason: "the containment inputs cannot be read from the definitions: " + p.DefsReason}
	}
	// A cell is owed for what the capture rule would take: the need plus its
	// margin (#1742), so a cell is never built for an entity that would then
	// be killed.
	margin, err := CaptureMargin(defs)
	if err != nil {
		return ChildRoomNeed{}, ContainmentVerdict{Reason: "the capture margin is unknown: " + err.Error()}
	}
	required := demand.Required + margin
	for _, h := range holders {
		if h.Available && h.Strength >= required {
			return ChildRoomNeed{}, ContainmentVerdict{}
		}
	}
	need := ChildRoomNeed{Role: RoomRoleContainmentCell, Module: PlannedContainmentCell, Furniture: []ChildFurniture{
		{Defs: []string{defs.Holder}, Count: 1},
		// A lamp lights the cell (#1743): glow adds ten containment strength
		// per unit of mean glow, and the power planner connects the lamp
		// like any other unpowered consumer. Optional: left out until the
		// lamp is researched, and the predicted strength above counts no
		// glow, so the cell never relies on it.
		{Defs: []string{ContainmentLampDefinition}, Count: 1, Optional: true}}}
	if _, ok := need.resolve(furniture); !ok {
		return ChildRoomNeed{}, ContainmentVerdict{Reason: "the holding platform " + defs.Holder + " is not buildable yet or its size is unknown"}
	}
	// The floor term counts the containment floor (#2435) when the stock pays
	// for every tile of the smallest room that holds the cell; WantedFloors
	// lays it under the same test.
	floored := false
	if shape, ok := need.shape(furniture); ok {
		if sizes := ChildRoomSizes(shape); len(sizes) > 0 {
			floored = ContainmentFloorLaid(defs.Floor, floors, int(sizes[0][0]*sizes[0][1]))
		}
	}
	strength, err := defs.Predict(ContainmentRoom{Floored: floored})
	if err != nil {
		return ChildRoomNeed{}, ContainmentVerdict{Reason: "the cell's strength cannot be predicted: " + err.Error()}
	}
	if strength < required {
		return ChildRoomNeed{}, ContainmentVerdict{Reason: fmt.Sprintf("a cell of %s with the planned walls and door holds at most %.1f containment strength and an entity needs %.1f plus a margin of %.1f; facilities that add strength are not planned", defs.Holder, strength, demand.Required, margin)}
	}
	return need, ContainmentVerdict{Owed: true}
}

// ContainmentFloorLaid reports whether a room of tiles can be laid with the
// containment floor now: a floor def the catalog names, known available
// terrain, and a stock that covers its whole cost for every tile. An unread
// stock or definition is not enough (unknown stays unknown).
func ContainmentFloorLaid(floor ContainmentFloor, facts FlooringFacts, tiles int) bool {
	def, known := facts.Definitions[floor.Def]
	if floor.Def == "" || !known || tiles <= 0 {
		return false
	}
	available, ak := def.Available.Value()
	terrain, tk := def.Terrain.Value()
	return ak && tk && available && terrain && affordableCells(def, facts.Stock, tiles) >= tiles
}
