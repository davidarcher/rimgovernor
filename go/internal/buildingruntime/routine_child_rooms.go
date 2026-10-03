package buildingruntime

import (
	"context"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The Biotech child rooms (#1680, epic #1667): MaintainHousing's sleeping
// planner shells the planned nursery, playroom or classroom a baby, toddler
// or child owes and places the furniture the game scores the role from
// (policy.NextChildRoomStep). The layout review grows the rooms
// (policy.ChildRoomsOwed). Nothing is owed without Biotech: the pawn rows
// carry no developmental stage and the furniture definitions are absent.

// reviewChildRooms adds the child room furniture definitions to the
// projection so the planners size and place them from the catalog.
func reviewChildRooms(reading *observation.RoutineReading) {
	reading.Projection.AddDefinitions(reading.Frame, policy.ChildRoomDefinitions())
}

// childRoomNeeds are the child rooms the projection's pawns owe; unknown
// pawns owe none.
func childRoomNeeds(facts observation.ColonyProjection) []policy.ChildRoomNeed {
	pawns, known := facts.WorkPawns.Value()
	if !known {
		return nil
	}
	return policy.ChildRoomNeeds(pawns)
}

// furnitureDefinitions are the child room furniture the projection's
// catalog describes.
func furnitureDefinitions(facts observation.ColonyProjection) []policy.FurnitureDefinition {
	names := policy.ChildRoomDefinitions()
	var defs []policy.FurnitureDefinition
	for _, d := range facts.Definitions {
		if slices.Contains(names, d.Name) {
			defs = append(defs, policy.FurnitureDefinition{Name: d.Name, Available: d.Available, Size: d.Size})
		}
	}
	return defs
}

// childRoomStep is the projection's next child room step; none while the
// plan, the room census or the construction census is unknown.
func childRoomStep(facts observation.ColonyProjection) policy.ChildRoomStep {
	plan, pk := facts.LayoutPlan.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	if !pk || !rk || !ck || !census.Colony {
		return policy.ChildRoomStep{}
	}
	return policy.NextChildRoomStep(plan, rooms, census.Buildings, childRoomNeeds(facts), furnitureDefinitions(facts))
}

// stageChildRoom answers a due child room step: the shell through
// shellRoom, a piece through placePiece.
func (r *RoutineSleepingUpkeepPlanner) stageChildRoom(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, reading observation.RoutineReading, step policy.ChildRoomStep) (RoutineBuildingResult, error) {
	clockSchedulerLog("%s: %s %s", goal.Goal.ID, step.Need.Role, step.Kind)
	method := domain.MethodID(step.Method())
	if step.Kind == policy.ChildRoomPlace {
		return r.building.placePiece(call, epoch, state, review, goal, reading, step.Piece, method)
	}
	return r.building.shellRoom(call, epoch, state, review, goal, reading.ColonyReading, step.Room, method, string(step.Need.Role))
}
