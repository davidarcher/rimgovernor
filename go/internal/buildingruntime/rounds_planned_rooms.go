package buildingruntime

import (
	"context"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/store"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Planned rooms an owning goal shells before it furnishes: the
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
	// The butcher table goes in the planned butchery, ring and table together.
	case r.concern == policy.MaintainButcherSpot && r.definition == "TableButcher":
		return policy.PlannedButchery, true
	case r.concern == policy.MaintainRefrigeration && r.refrigeration != nil && r.refrigeration.Method == policy.RefrigerationBuild:
		return policy.PlannedFreezer, true
	}
	return "", false
}

// plannedLayout is the v2 plan and room census, known whenever both are
// read. The tech tier does not gate it: a colony that holds the materials
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
	return rectCells(room.Interior)
}

// rectCells are the cells of a rectangle.
func rectCells(in policy.Rectangle) []domain.Cell {
	cells := make([]domain.Cell, 0, int(in.Width*in.Height))
	for z := in.Z; z < in.Z+in.Height; z++ {
		for x := in.X; x < in.X+in.Width; x++ {
			cells = append(cells, domain.Cell{X: x, Z: z})
		}
	}
	return cells
}

// plannedInteriorSiteCells are the site cells inside the rooms' interiors,
// roofed or not.
func plannedInteriorSiteCells(facts observation.ColonyProjection, rooms []policy.InteriorRoom) []policy.SiteCell {
	var out []policy.SiteCell
	for _, c := range facts.Cells {
		for _, room := range rooms {
			in := room.Interior
			if c.Cell.X >= in.X && c.Cell.X < in.X+in.Width && c.Cell.Z >= in.Z && c.Cell.Z < in.Z+in.Height {
				out = append(out, c)
				break
			}
		}
	}
	return out
}

// plannedRoomMethod names a planned room's shell: once per room per epoch.
func plannedRoomMethod(room policy.PlannedRoom) domain.MethodID {
	return domain.MethodID(fmt.Sprintf("%s-shell-%d-%d", room.Role, room.Interior.X, room.Interior.Z))
}

// facilityFurnishingRoles are the facility roles whose furniture stands in the
// layout plan's room of that role: dining, rec, hospital, laboratory
// and workshop. No census room hosts them.
var facilityFurnishingRoles = map[policy.RoomRole]bool{
	policy.RoomRoleDiningRoom: true, policy.RoomRoleRecRoom: true, policy.RoomRoleHospital: true,
	policy.RoomRoleLaboratory: true, policy.RoomRoleWorkshop: true,
}

// plannedFacilityFurnishing puts a facility's furniture in the layout plan's
// room of its role: while the room is owed its ring is admitted and the
// furniture goes onto the footprint's interior with it, not after the walls,
// since the room is where they will stand anyway; once the room stands the
// furniture goes into its interior. A laboratory takes the planned shelter's
// bench row instead. done is true when the ring's own step produced
// the result to return.
func (r *RoundsBuildingPlanner) plannedFacilityFurnishing(call, epoch context.Context, state ControlState, review store.Rounds, goal store.WorkOwner, reading observation.ColonyReading, facts observation.ColonyProjection) (planner *RoundsBuildingPlanner, result RoundsBuildingResult, done bool, err error) {
	if r.facility == nil || r.cells != nil || !facilityFurnishingRoles[r.facility.Role] {
		return r, RoundsBuildingResult{}, false, nil
	}
	if r.facility.Role == policy.RoomRoleLaboratory && !r.advancedLab && len(plannedShelterRooms(facts)) > 0 {
		return r, RoundsBuildingResult{}, false, nil
	}
	module, ok := r.roomModule()
	if !ok {
		return r, RoundsBuildingResult{}, false, nil
	}
	room, owed := plannedRoomOwed(facts, module)
	if !owed {
		cells := plannedRoomCells(facts, module)
		if cells == nil {
			return r, RoundsBuildingResult{}, false, nil
		}
		furnish := *r
		furnish.facility, furnish.cells = nil, cells
		return &furnish, RoundsBuildingResult{}, false, nil
	}
	result, err = r.reconcileRoom(call, epoch, state, review, goal, observation.RoundsReading{ColonyReading: reading}, nil, roomReconcile{ringOnly: true, room: room, name: string(plannedRoomMethod(room))})
	if err != nil || !result.Verdict.skipsToPlacement() {
		return nil, result, true, err
	}
	furnish := *r
	furnish.facility, furnish.cells, furnish.environment = nil, plannedRoomInterior(room), policy.PlacementAnywhere
	return &furnish, RoundsBuildingResult{}, false, nil
}

// takesShelterSlot reports a planner placing furniture on the planned
// shelter's template slots: the cold campfires, the hot passive cooler, the
// crafting spot and the research bench.
func (r *RoundsBuildingPlanner) takesShelterSlot(facts observation.ColonyProjection) bool {
	plan, known := facts.LayoutPlan.Value()
	switch {
	case r.facility != nil:
		return r.facility.Role == policy.RoomRoleLaboratory && !r.advancedLab
	case r.concern == policy.EnsureBasicDefense:
		return r.definition == craftingSpotDefinition
	case r.definition == "Campfire":
		cooking := r.concern == policy.EnsureCooking && len(r.paste) == 0
		return known && plan.Cold && (cooking || r.temperature != nil && r.temperature.Method == policy.TemperatureHeat)
	case r.definition == "PassiveCooler":
		return known && plan.Hot && r.temperature != nil && r.temperature.Method == policy.TemperatureCool
	}
	return false
}

// hostsInPlannedRoom reports a planner whose building stands on the planned
// room's interior: the kitchen's and the butchery's furniture, unless the
// planner takes the shelter's template slot instead (a cold map's cooking
// campfire).
func (r *RoundsBuildingPlanner) hostsInPlannedRoom(module policy.PlannedRole, facts observation.ColonyProjection) bool {
	return (module == policy.PlannedKitchen || module == policy.PlannedButchery) && !r.takesShelterSlot(facts)
}

// shelterCampfires is how many campfires the shelter holds indoors: the plan's
// latched climate decides.
func shelterCampfires(facts observation.ColonyProjection) int {
	plan, known := facts.LayoutPlan.Value()
	return policy.ShelterCampfires(known && plan.Cold)
}

// shelterCoolers is how many passive coolers the shelter holds on its floor:
// the plan's latched climate decides.
func shelterCoolers(facts observation.ColonyProjection) int {
	plan, known := facts.LayoutPlan.Value()
	return policy.ShelterCoolers(known && plan.Hot)
}

// plannedShelterRooms are the layout plan's shelter rooms read as plan inputs
// from the planned interior alone, whether or not a wall stands: the
// template's slots (campfires, cooler, crafting spot, research bench) are
// keyed on the footprint, like the dining furniture. Empty while no plan holds
// a shelter.
func plannedShelterRooms(facts observation.ColonyProjection) []policy.InteriorRoom {
	plan, known := facts.LayoutPlan.Value()
	rooms := plannedInteriorRooms(facts, func(planned policy.PlannedRoom) bool { return planned.Role == policy.PlannedShelter })
	for i := range rooms {
		rooms[i].Campfires, rooms[i].Coolers = policy.ShelterCampfires(known && plan.Cold), policy.ShelterCoolers(known && plan.Hot)
	}
	return rooms
}

// plannedInteriorRooms are the layout plan's rooms that keep accepts, read as
// plan inputs from the planned interior alone, with the buildings already
// standing in each; a room with no usable door is left out.
func plannedInteriorRooms(facts observation.ColonyProjection, keep func(policy.PlannedRoom) bool) []policy.InteriorRoom {
	plan, known := facts.LayoutPlan.Value()
	if !known {
		return nil
	}
	edifice := map[domain.Cell]string{}
	for _, c := range facts.Cells {
		if d := c.PlayerEdifice(); d != "" {
			edifice[c.Cell] = d
		}
	}
	var out []policy.InteriorRoom
	for _, planned := range plan.AllRooms() {
		if !keep(planned) {
			continue
		}
		room, ok := policy.InteriorRoomFromLayout(planned, facts.Shapes)
		if !ok {
			continue
		}
		seen := map[string]bool{}
		for _, c := range plannedRoomInterior(planned) {
			if d, ok := edifice[c]; ok && !seen[d] {
				seen[d] = true
				room.Standing = append(room.Standing, d)
			}
		}
		sort.Strings(room.Standing)
		out = append(out, room)
	}
	return out
}

// craftingSpotDefinition is the free, unpowered bench every tech level can
// place at once; it hosts the club and short bow recipes a tribal start
// arms itself with. It lives here, not in rounds_armory.go, so the armory's
// scope does not widen through the shelter slot predicate.
const craftingSpotDefinition = "CraftingSpot"
