package buildingruntime

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/store"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Planned rooms an owning goal shells before it furnishes (#835): the
// kitchen before EnsureCooking's stove, the freezer before
// MaintainRefrigeration's cooler, the jail while MaintainPopulation holds a
// prisoner. Each raises the plan's exact rectangle, with doors at the plan's
// Door and at the freezer's Link in both rings.

// plannedRoomModule is the planned room this planner's next method needs
// standing first; false when it needs none.
func (r *RoundsBuildingPlanner) plannedRoomModule() (policy.PlannedRole, bool) {
	switch {
	case r.concern == policy.EnsureCooking && len(r.paste) == 0:
		return policy.PlannedKitchen, true
	// The stand-in ButcherSpot is free and instant and gates hunting (#260): it
	// stands outdoors now and never waits on the room being dug. Only the real
	// table goes in the planned butchery.
	case r.concern == policy.MaintainButcherSpot && r.definition == "TableButcher":
		return policy.PlannedButchery, true
	case r.concern == policy.MaintainRefrigeration && r.refrigeration != nil && r.refrigeration.Method == policy.RefrigerationBuild:
		return policy.PlannedFreezer, true
	}
	return "", false
}

// plannedLayout is the v2 plan and room census, known whenever both are
// read. The build tier does not gate it: a colony that holds the materials
// raises a planned room at any tier, and the stuff ladder (WallStuff) says
// what it is built from.
func plannedLayout(facts observation.ColonyProjection) (policy.LayoutPlan, policy.RoomObservation, bool) {
	plan, pk := facts.LayoutPlan.Value()
	rooms, rk := facts.Rooms.Value()
	return plan, rooms, pk && rk
}

// plannedRoomOwed is the first planned room of module with nothing
// standing in it yet.
func plannedRoomOwed(facts observation.ColonyProjection, module policy.PlannedRole) (policy.PlannedRoom, bool) {
	plan, known := facts.LayoutPlan.Value()
	ground, groundKnown := colonyGround(facts)
	if !known || !groundKnown {
		return policy.PlannedRoom{}, false
	}
	return plan.NextPlannedRoom(module, ground)
}

// colonyGround is the walls and doors the colony's construction census holds;
// unknown while the census is, so no room reads as unbuilt on a guess.
func colonyGround(facts observation.ColonyProjection) (policy.GroundCensus, bool) {
	construction, known := facts.Facts.CurrentConstruction.Value()
	if !known || !construction.Colony {
		return policy.GroundCensus{}, false
	}
	return policy.GroundOfWithFlap(construction.Buildings, facts.Shapes.Furniture.AnimalFlap), true
}

// plannedRoomCells is the interior of the first standing planned room of
// module; nil when none stands.
func plannedRoomCells(facts observation.ColonyProjection, module policy.PlannedRole) []domain.Cell {
	plan, rooms, known := plannedLayout(facts)
	if !known {
		return nil
	}
	for _, r := range plan.AllRooms() {
		if r.Role != module {
			continue
		}
		// Census: a spot goes only in a roofed room.
		if _, ok := policy.CensusRoomIn(r, rooms); !ok {
			continue
		}
		in := r.Interior
		cells := make([]domain.Cell, 0, int(in.Width*in.Height))
		for z := in.Z; z < in.Z+in.Height; z++ {
			for x := in.X; x < in.X+in.Width; x++ {
				cells = append(cells, domain.Cell{X: x, Z: z})
			}
		}
		return cells
	}
	return nil
}

// plannedRoomInterior is the room's interior cells.
func plannedRoomInterior(room policy.PlannedRoom) []domain.Cell {
	in := room.Interior
	cells := make([]domain.Cell, 0, int(in.Width*in.Height))
	for z := in.Z; z < in.Z+in.Height; z++ {
		for x := in.X; x < in.X+in.Width; x++ {
			cells = append(cells, domain.Cell{X: x, Z: z})
		}
	}
	return cells
}

// butcherSpotCoreMargin is how far past the planned core's bounding box the
// stand-in butcher spot may stand (#2040).
const butcherSpotCoreMargin = 3

// roomInteriorCells are the cells inside any observed room or planned room
// interior, built or not.
func roomInteriorCells(facts observation.ColonyProjection) []domain.Cell {
	var cells []domain.Cell
	if census, known := facts.Rooms.Value(); known {
		for _, room := range census.Rooms {
			cells = append(cells, room.Cells...)
		}
	}
	if plan, ok := facts.LayoutPlan.Value(); ok {
		for _, room := range plan.AllRooms() {
			cells = append(cells, plannedRoomInterior(room)...)
		}
	}
	return cells
}

// plannedRoomMethod names a planned room's shell: once per room per epoch.
func plannedRoomMethod(room policy.PlannedRoom) domain.MethodID {
	return domain.MethodID(fmt.Sprintf("%s-shell-%d-%d", room.Role, room.Interior.X, room.Interior.Z))
}

// plannedDiningFurnishing puts the dining table and chairs in the layout
// plan's dining room while no standing room hosts dining: the ring is
// admitted and the furniture goes onto the footprint's interior with it, not
// after the walls, since the room is where they will stand anyway. done is
// true when the ring's own step produced the result to return.
func (r *RoundsBuildingPlanner) plannedDiningFurnishing(call, epoch context.Context, state ControlState, review store.Rounds, goal store.WorkOwner, reading observation.ColonyReading, facts observation.ColonyProjection) (planner *RoundsBuildingPlanner, result RoundsBuildingResult, done bool, err error) {
	if r.concern != policy.EnsureComfort || r.facility == nil || r.facility.Role != policy.RoomRoleDiningRoom || !comfortFurniture(facts).IsTable(r.definition) && !comfortFurniture(facts).IsChair(r.definition) {
		return r, RoundsBuildingResult{}, false, nil
	}
	rooms, known := facts.Rooms.Value()
	if !known || len(policy.HostingCells(*r.facility, rooms)) > 0 {
		return r, RoundsBuildingResult{}, false, nil
	}
	room, owed := plannedRoomOwed(facts, policy.PlannedDining)
	if !owed {
		return r, RoundsBuildingResult{}, false, nil
	}
	result, err = r.reconcileRoom(call, epoch, state, review, goal, observation.RoundsReading{ColonyReading: reading}, nil, roomReconcile{ringOnly: true, room: room, name: string(plannedRoomMethod(room))})
	if err != nil || !result.Verdict.skipsToPlacement() {
		return nil, result, true, err
	}
	furnish := *r
	furnish.facility, furnish.cells, furnish.environment = nil, plannedRoomInterior(room), policy.PlacementAnywhere
	return &furnish, RoundsBuildingResult{}, false, nil
}

// shelterShellSuffix is "-X-Z" of the layout plan's first shelter room, so
// the shell reads shelter-shell-X-Z (#2043); empty while no plan holds one.
func shelterShellSuffix(facts observation.ColonyProjection) string {
	plan, known := facts.LayoutPlan.Value()
	if !known {
		return ""
	}
	for _, room := range plan.AllRooms() {
		if room.Role == policy.PlannedShelter {
			return fmt.Sprintf("-%d-%d", room.Interior.X, room.Interior.Z)
		}
	}
	return ""
}

// shelterCampfires is how many campfires the shelter holds indoors: the plan's
// latched climate decides (#2044).
func shelterCampfires(facts observation.ColonyProjection) int {
	plan, known := facts.LayoutPlan.Value()
	return policy.ShelterCampfires(known && plan.Cold)
}

// shelterCoolers is how many passive coolers the shelter holds on its floor:
// the plan's latched climate decides (#2044).
func shelterCoolers(facts observation.ColonyProjection) int {
	plan, known := facts.LayoutPlan.Value()
	return policy.ShelterCoolers(known && plan.Hot)
}

// shelterInteriorRooms plans the standing room on the layout plan's shelter
// interior as the shelter, whatever role the game scores it (a Barracks once
// the bunks stand), so the research bench takes the template's research slot
// (#2043).
func shelterInteriorRooms(rooms []policy.InteriorRoom, facts observation.ColonyProjection) []policy.InteriorRoom {
	plan, known := facts.LayoutPlan.Value()
	if !known {
		return rooms
	}
	for _, planned := range plan.AllRooms() {
		if planned.Role != policy.PlannedShelter {
			continue
		}
		for i := range rooms {
			if rooms[i].Interior == planned.Interior {
				rooms[i].Role = policy.RoomRoleShelter
				rooms[i].Campfires, rooms[i].Coolers = policy.ShelterCampfires(plan.Cold), policy.ShelterCoolers(plan.Hot)
			}
		}
	}
	return rooms
}

// standingShelterRooms are the enclosed census rooms standing on the layout
// plan's shelter interior, planned as the shelter whatever role the game
// scores them (#2074). Empty while no shelter stands.
func standingShelterRooms(facts observation.ColonyProjection) []policy.InteriorRoom {
	plan, known := facts.LayoutPlan.Value()
	census, roomsKnown := facts.Rooms.Value()
	if !known || !roomsKnown {
		return nil
	}
	var doorways []domain.Cell
	for _, c := range facts.Cells {
		if doorway, _ := c.Doorway.Value(); doorway {
			doorways = append(doorways, c.Cell)
		}
	}
	var out []policy.InteriorRoom
	for _, planned := range plan.AllRooms() {
		if planned.Role != policy.PlannedShelter {
			continue
		}
		for _, room := range census.Rooms {
			if enclosed, _ := room.Enclosed.Value(); !enclosed {
				continue
			}
			interior, ok := policy.InteriorRoomFromCensus(room, policy.RoomRoleShelter, doorways, census.Shapes)
			if ok && interior.Interior == planned.Interior {
				interior.Campfires, interior.Coolers = policy.ShelterCampfires(plan.Cold), policy.ShelterCoolers(plan.Hot)
				out = append(out, interior)
			}
		}
	}
	return out
}
