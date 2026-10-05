package buildingruntime

import (
	"context"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The Biotech child rooms (#1680, epic #1667): MaintainHousing's sleeping
// planner reconciles the planned nursery, playroom or classroom a baby,
// toddler or child owes with the furniture the game scores the role from
// (policy.NextChildRoomStep, reconcileRoom). The layout review grows the rooms
// (policy.ChildRoomsOwed). Nothing is owed without Biotech: the pawn rows
// carry no developmental stage and the furniture definitions are absent.

// worshipDefinitions are the buildings the ideoligion requires; none while
// the ideoligion is unknown.
func worshipDefinitions(facts observation.ColonyProjection) []string {
	ideology, known := facts.Facts.Ideology.Value()
	if !known {
		return nil
	}
	return ideology.RequiredBuildings()
}

// reviewChildRooms adds the worship room's required buildings to the
// projection's definitions so the planners size and place them from the
// catalog, and remembers the buildings for the planners' own reads (#1658).
// Room-role furniture rides every routine reading (the catalog names it).
func (r *Rounder) reviewChildRooms(reading *observation.RoundsReading) error {
	worship := worshipDefinitions(reading.Projection)
	if err := reading.Projection.AddDefinitions(reading.Frame, slices.Concat(worship, containmentDefinitions(reading.Projection))); err != nil {
		return err
	}
	r.census.rememberWorship(worship)
	return nil
}

// containmentDefinitions are the holding platform the containment cell
// places; none while the catalog's containment inputs are unknown.
func containmentDefinitions(facts observation.ColonyProjection) []string {
	if defs, known := facts.Facts.Containment.Defs.Value(); known {
		return []string{defs.Holder, policy.ContainmentLampDefinition}
	}
	return nil
}

// containmentCellNeed is the cell the capturable entities owe.
func containmentCellNeed(facts observation.ColonyProjection) (policy.ChildRoomNeed, policy.ContainmentVerdict) {
	return policy.ContainmentCellNeed(facts.Facts.Containment, furnitureDefinitions(facts))
}

// isolationRoomNeed is the room the isolated creepjoiners owe (#1740).
func isolationRoomNeed(facts observation.ColonyProjection) (policy.ChildRoomNeed, bool) {
	return policy.IsolationRoomNeed(facts.Isolation, furnitureDefinitions(facts))
}

// childRoomNeeds are the rooms the projection owes: the child rooms its
// pawns owe (unknown pawns owe none) and the ideoligion's worship room
// (#1658; an unknown ideoligion owes none).
func childRoomNeeds(facts observation.ColonyProjection) []policy.ChildRoomNeed {
	var needs []policy.ChildRoomNeed
	if pawns, known := facts.WorkPawns.Value(); known {
		needs = policy.ChildRoomNeeds(pawns)
	}
	if ideology, known := facts.Facts.Ideology.Value(); known {
		if need, owed := policy.WorshipRoomNeed(ideology); owed {
			needs = append(needs, need)
		}
	}
	if need, verdict := containmentCellNeed(facts); verdict.Owed {
		needs = append(needs, need)
	}
	if need, owed := isolationRoomNeed(facts); owed {
		needs = append(needs, need)
	}
	return needs
}

// furnitureDefinitions are the room furniture the projection's catalog
// describes: the room-role furniture and the worship room's buildings.
func furnitureDefinitions(facts observation.ColonyProjection) []policy.FurnitureDefinition {
	names := slices.Concat(worshipDefinitions(facts), containmentDefinitions(facts), facts.Isolation.Beds)
	var defs []policy.FurnitureDefinition
	for _, d := range facts.Definitions {
		if len(d.RoomRoles) > 0 || slices.Contains(names, d.Name) {
			defs = append(defs, policy.FurnitureDefinition{Name: d.Name, Available: d.Available, Size: d.Size, Roles: d.RoomRoles})
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
	ground, gk := colonyGround(facts)
	if !pk || !rk || !ck || !census.Colony || !gk {
		return policy.ChildRoomStep{}
	}
	return policy.NextChildRoomStep(plan, rooms, plan.GroundWithRock(ground, naturalRock(facts)), census.Buildings, childRoomNeeds(facts), furnitureDefinitions(facts))
}

// stageChildRoom answers a due child room step through the shared build side
// (reconcileRoom): the ring, floor and furniture the plan and the template
// still owe, installed from packed stock first.
func (r *RoundsSleepingUpkeepPlanner) stageChildRoom(call, epoch context.Context, stock *packedStock, state ControlState, review store.Rounds, goal store.WorkOwner, reading observation.RoundsReading, step policy.ChildRoomStep) (RoundsBuildingResult, error) {
	in := step.Room.Interior
	return r.building.reconcileRoom(call, epoch, state, review, goal, reading, stock, roomReconcile{
		room: step.Room, template: step.Template,
		name: fmt.Sprintf("child-room-%d-%d", in.X, in.Z), reason: string(step.Need.Role),
	})
}
