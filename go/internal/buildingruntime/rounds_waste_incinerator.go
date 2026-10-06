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

// The incinerator (#1814): the layout plan holds it from the start inside the
// waste yard (#2187), and MaintainWaste shells the yard's fence and gate, then
// the incinerator like the tomb with the least flammable wall and door the game's
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

// incineratorRooms are the plan's incinerators; unknown without the plan and
// the construction census.
func incineratorRooms(facts observation.ColonyProjection) (plan policy.LayoutPlan, rooms []policy.PlannedRoom, known bool) {
	plan, pk := facts.LayoutPlan.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	if !pk || !ck || !census.Colony {
		return plan, nil, false
	}
	return plan, plan.IncineratorRooms(), true
}

// incineratorStep is the first planned incinerator whose ring is not yet
// raised.
func incineratorStep(facts observation.ColonyProjection) (policy.PlannedRoom, bool) {
	plan, rooms, known := incineratorRooms(facts)
	if !known {
		return policy.PlannedRoom{}, false
	}
	for _, room := range rooms {
		if roomRingOwed(facts, plan, room) {
			return room, true
		}
	}
	return policy.PlannedRoom{}, false
}

// standingIncinerator is the first planned incinerator with its ring raised,
// for the storage planner's zone.
func standingIncinerator(facts observation.ColonyProjection) *policy.PlannedRoom {
	plan, rooms, known := incineratorRooms(facts)
	if !known {
		return nil
	}
	for _, room := range rooms {
		if !roomRingOwed(facts, plan, room) {
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

func incineratorMethod(room policy.PlannedRoom) domain.MethodID {
	return domain.MethodID(fmt.Sprintf("incinerator-shell-%d-%d", room.Interior.X, room.Interior.Z))
}

// stageDisposal answers a due burn, else the incinerator's shell, else the
// waste yard's fence; handled is false when none is due.
func (r *RoundsWastePlanner) stageDisposal(call, epoch context.Context, state ControlState, review store.Rounds, goal store.StandardState, arbiter *stepArbiter, reading observation.RoundsReading) (RoundsWasteResult, bool, error) {
	if result, handled, err := r.stageBurn(call, epoch, state, review, goal, arbiter, reading); err != nil || handled {
		return result, handled, err
	}
	if r.building == nil {
		return RoundsWasteResult{}, false, nil
	}
	room, owed := incineratorStep(reading.Projection)
	rc := roomReconcile{ringOnly: true, room: room, name: string(incineratorMethod(room)), reason: "burn rotten and worn items", stuff: fireproofShellStuff}
	if !owed {
		if room, owed = plannedRoomOwed(reading.Projection, policy.PlannedWasteYard); !owed {
			return RoundsWasteResult{}, false, nil
		}
		rc = roomReconcile{ringOnly: true, room: room, name: string(plannedRoomMethod(room)), reason: "hold the dump and the incinerator"}
	}
	result, err := r.building.reconcileRoom(call, epoch, state, review, goal, observation.RoundsReading{ColonyReading: reading.ColonyReading}, nil, rc)
	return RoundsWasteResult{Verdict: result.Verdict}, true, err
}
