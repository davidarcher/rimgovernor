package buildingruntime

import (
	"fmt"

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
func (r *RoutineBuildingPlanner) plannedRoomModule() (policy.ModuleRole, bool) {
	switch {
	case r.goal == policy.EnsureCooking && len(r.paste) == 0:
		return policy.ModuleKitchen, true
	case r.goal == policy.MaintainRefrigeration && r.refrigeration != nil && r.refrigeration.Method == policy.RefrigerationBuild:
		return policy.ModuleFreezer, true
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
func plannedRoomOwed(facts observation.ColonyProjection, module policy.ModuleRole) (policy.LayoutRoom, bool) {
	plan, rooms, known := plannedLayout(facts)
	if !known {
		return policy.LayoutRoom{}, false
	}
	return plan.NextPlannedRoom(module, rooms)
}

// plannedRoomCells is the interior of the first standing planned room of
// module; nil when none stands.
func plannedRoomCells(facts observation.ColonyProjection, module policy.ModuleRole) []domain.Cell {
	plan, rooms, known := plannedLayout(facts)
	if !known {
		return nil
	}
	for _, r := range plan.AllRooms() {
		if r.Role != module {
			continue
		}
		if _, ok := policy.PlannedRoomStanding(r, rooms); !ok {
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
func plannedRoomInterior(room policy.LayoutRoom) []domain.Cell {
	in := room.Interior
	cells := make([]domain.Cell, 0, int(in.Width*in.Height))
	for z := in.Z; z < in.Z+in.Height; z++ {
		for x := in.X; x < in.X+in.Width; x++ {
			cells = append(cells, domain.Cell{X: x, Z: z})
		}
	}
	return cells
}

// plannedRoomMethod names a planned room's shell: once per room per epoch.
func plannedRoomMethod(room policy.LayoutRoom) domain.MethodID {
	return domain.MethodID(fmt.Sprintf("%s-shell-%d-%d", room.Role, room.Interior.X, room.Interior.Z))
}
