package buildingruntime

import (
	"context"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The incinerator (#1814): once the layout plan reserves it beside the dumps
// (policy.RoomGrowth.Incinerator, sited by the storage planner), MaintainWaste
// shells it like the tomb with the least flammable wall and door the game's
// stuff data offers, refusing a wall or door that would burn. The storage
// planner then zones its interior for rotten and worn dump items. It is
// permanent: nothing here ever drops or tears it down.

// fireproofShellStuff builds the walls and the door from the least flammable
// stuff each allows, and refuses when that still burns or the rows do not say.
func fireproofShellStuff(wall, door observation.PlanningDefinition) (string, string, Verdict, bool) {
	wallStuff, err := wall.FireproofStuff()
	if err != nil {
		return "", "", fireproofRefusal(err), false
	}
	doorStuff, err := door.FireproofStuff()
	if err != nil {
		return "", "", fireproofRefusal(err), false
	}
	return wallStuff, doorStuff, Verdict{}, true
}

func fireproofRefusal(err error) Verdict {
	if errors.Is(err, observation.ErrFlammableStuff) {
		return siteBlocked("incinerator", "flammable_wall_or_door")
	}
	return fieldUnavailable("incinerator_stuff")
}

// incineratorRing is the planned incinerator's wall and door cells still to
// raise.
func incineratorRing(plan policy.LayoutPlan, room policy.LayoutRoom, facts observation.ColonyProjection) []domain.Cell {
	order := plan.ShellDoors(room)
	doors := make(map[domain.Cell]bool, len(order))
	for _, d := range order {
		doors[d] = true
	}
	return bedroomRing(room, doors, order, facts)
}

// incineratorRooms are the plan's incinerators; unknown without the plan and
// the construction census.
func incineratorRooms(facts observation.ColonyProjection) (plan policy.LayoutPlan, rooms []policy.LayoutRoom, known bool) {
	plan, pk := facts.LayoutPlan.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	if !pk || !ck || !census.Colony {
		return plan, nil, false
	}
	return plan, plan.IncineratorRooms(), true
}

// incineratorStep is the first planned incinerator whose ring is not yet
// raised.
func incineratorStep(facts observation.ColonyProjection) (policy.LayoutRoom, bool) {
	plan, rooms, known := incineratorRooms(facts)
	if !known {
		return policy.LayoutRoom{}, false
	}
	for _, room := range rooms {
		if len(incineratorRing(plan, room, facts)) > 0 {
			return room, true
		}
	}
	return policy.LayoutRoom{}, false
}

// standingIncinerator is the first planned incinerator with its ring raised,
// for the storage planner's zone.
func standingIncinerator(facts observation.ColonyProjection) *policy.LayoutRoom {
	plan, rooms, known := incineratorRooms(facts)
	if !known {
		return nil
	}
	for _, room := range rooms {
		if len(incineratorRing(plan, room, facts)) == 0 {
			return &room
		}
	}
	return nil
}

// incineratorOwed is true while a planned incinerator awaits its shell.
func incineratorOwed(facts observation.ColonyProjection) domain.Fact[bool] {
	_, _, known := incineratorRooms(facts)
	if !known {
		return domain.Unknown[bool]()
	}
	_, owed := incineratorStep(facts)
	return domain.Known(owed)
}

func incineratorMethod(room policy.LayoutRoom) domain.MethodID {
	return domain.MethodID(fmt.Sprintf("incinerator-shell-%d-%d", room.Interior.X, room.Interior.Z))
}

// stageDisposal answers a due burn, else the incinerator's shell; handled is
// false when neither is due.
func (r *RoutineWastePlanner) stageDisposal(call, epoch context.Context, state ControlState, review store.Rounds, goal store.GoalState, arbiter *stepArbiter, reading observation.RoutineReading) (RoutineWasteResult, bool, error) {
	if result, handled, err := r.stageBurn(call, epoch, state, review, goal, arbiter, reading); err != nil || handled {
		return result, handled, err
	}
	if r.building == nil {
		return RoutineWasteResult{}, false, nil
	}
	room, owed := incineratorStep(reading.Projection)
	if !owed {
		return RoutineWasteResult{}, false, nil
	}
	clockSchedulerLog("%s: incinerator shell at %d,%d", goal.Goal.ID, room.Interior.X, room.Interior.Z)
	result, err := r.building.shellRoomOf(call, epoch, state, review, goal, reading.ColonyReading, room, incineratorMethod(room), "burn rotten and worn items", fireproofShellStuff)
	return RoutineWasteResult{Verdict: result.Verdict}, true, err
}
