package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// sleepingRoomCells are the cells of every census room holding a bed,
// bedroll or sleeping spot (#1179): the cooking campfire never stands there.
func sleepingRoomCells(rooms domain.Fact[policy.RoomObservation]) []domain.Cell {
	census, known := rooms.Value()
	if !known {
		return nil
	}
	var cells []domain.Cell
	for _, room := range census.Rooms {
		if len(room.Beds) > 0 {
			cells = append(cells, room.Cells...)
		}
	}
	return cells
}

// campfireRetirement is the cooking campfire to deconstruct (#1179): one
// standing in a sleeping room, or any once a usable stove stands in a
// kitchen. A campfire the temperature family claimed is room heat and
// stays. Unknown rooms, benches or census retire nothing.
func campfireRetirement(facts observation.ColonyProjection, claims []policy.ConstructionClaim) (policy.CurrentBuilding, bool) {
	benches, bk := facts.CookingBenches.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	if !bk || !rk || !ck {
		return policy.CurrentBuilding{}, false
	}
	heat := heatCampfireCells(claims)
	stove := false
	for _, bench := range benches {
		if bench.Definition != "FueledStove" && bench.Definition != "ElectricStove" {
			continue
		}
		usable, uk := bench.Usable.Value()
		id, ik := bench.Room.Value()
		room, found := rooms.Room(id)
		role, ok := room.Role.Value()
		stove = stove || uk && usable && ik && found && ok && role == policy.RoomRoleKitchen
	}
	for _, bench := range benches {
		if bench.Definition != "Campfire" {
			continue
		}
		sleeping := false
		if id, ik := bench.Room.Value(); ik {
			room, found := rooms.Room(id)
			sleeping = found && len(room.Beds) > 0
		}
		if !stove && !sleeping {
			continue
		}
		for _, b := range census.Buildings {
			if b.ID == bench.ID && len(b.Cells) > 0 && !heat[b.Cells[0]] {
				return b, true
			}
		}
	}
	return policy.CurrentBuilding{}, false
}

// heatCampfireCells are the cells of every campfire the temperature family
// claimed: the marker of a campfire standing as room heat (#1179, #1180).
func heatCampfireCells(claims []policy.ConstructionClaim) map[domain.Cell]bool {
	heat := map[domain.Cell]bool{}
	for _, claim := range claims {
		if claim.Goal == policy.EnsureTemperatureSafety && claim.Building.Definition() == "Campfire" {
			for _, c := range claim.Cells {
				heat[c] = true
			}
		}
	}
	return heat
}

// heatCampfires are the standing campfires the temperature family claimed,
// with the room and auto-refuel toggle the cooking census reads (#1180).
func heatCampfires(facts observation.ColonyProjection) []policy.HeatCampfire {
	claims, _ := facts.Facts.ConstructionClaims.Value()
	benches, bk := facts.CookingBenches.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	heat := heatCampfireCells(claims)
	if !bk || !ck || len(heat) == 0 {
		return nil
	}
	var out []policy.HeatCampfire
	for _, bench := range benches {
		if bench.Definition != "Campfire" {
			continue
		}
		for _, b := range census.Buildings {
			if b.ID == bench.ID && len(b.Cells) > 0 && heat[b.Cells[0]] {
				out = append(out, policy.HeatCampfire{ID: bench.ID, Room: bench.Room, AutoRefuel: bench.AutoRefuel})
			}
		}
	}
	return out
}

// campfireRefuelOwed is the review's CampfireRefuelOwed fact.
func campfireRefuelOwed(facts observation.ColonyProjection) domain.Fact[bool] {
	_, owed := policy.CampfireRefuel(facts.Rooms, heatCampfires(facts))
	return domain.Known(owed)
}

// commitCampfireRefuel binds a one-action auto-refuel plan for the heat
// campfire the temperature proposal switches.
func (r *RoutineBuildingPlanner) commitCampfireRefuel(call context.Context, goal store.GoalState, check func() error) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	proposal := r.temperature
	if proposal == nil || proposal.Method != policy.TemperatureRefuelOff && proposal.Method != policy.TemperatureRefuelOn {
		return RoutineBuildingResult{}, fmt.Errorf("%w: commitCampfireRefuel: not a refuel proposal", ErrControl)
	}
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, proposal.Key); err == nil {
		return RoutineBuildingResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineBuildingResult{}, err
	}
	value, err := domain.NewAutoRefuel(proposal.Thing, proposal.Method == policy.TemperatureRefuelOn)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewAutoRefuelAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if err = check(); err != nil {
		return RoutineBuildingResult{}, err
	}
	clockSchedulerLog("%s: campfire %s auto-refuel %v", goal.Goal.ID, proposal.Thing, value.Allow())
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, proposal.Key, plan); err != nil {
		return RoutineBuildingResult{}, err
	}
	return RoutineBuildingResult{Reason: BuildingMethodAdmitted}, nil
}

// campfireRetireOwed is the review's CampfireRetireOwed fact.
func campfireRetireOwed(facts observation.ColonyProjection) domain.Fact[bool] {
	claims, _ := facts.Facts.ConstructionClaims.Value()
	_, bk := facts.CookingBenches.Value()
	_, rk := facts.Rooms.Value()
	_, ck := facts.Facts.CurrentConstruction.Value()
	if !bk || !rk || !ck {
		return domain.Unknown[bool]()
	}
	_, owed := campfireRetirement(facts, claims)
	return domain.Known(owed)
}

// retireCampfire admits one deconstruction of a misplaced or superseded
// cooking campfire, once per campfire per goal epoch.
func (r *RoutineBuildingPlanner) retireCampfire(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, reading observation.ColonyReading, campfire policy.CurrentBuilding) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	sum := sha256.Sum256([]byte(campfire.ID))
	method := domain.MethodID(fmt.Sprintf("campfire-retire-%x", sum[:8]))
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineBuildingResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineBuildingResult{}, err
	}
	snapshot := state.Snapshot
	snapshot.Plan = domain.MintPlanID()
	snapshot.Revision = 1
	check := func() error {
		if err := p.current(call, epoch); err != nil {
			return err
		}
		if p.session.State() != state {
			return fmt.Errorf("%w: retireCampfire: p.session.State() != state", ErrControl)
		}
		return nil
	}
	if err := check(); err != nil {
		return RoutineBuildingResult{}, err
	}
	value, err := domain.NewDeconstruction(campfire.ID, campfire.Building.Definition(), campfire.Cells[0])
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	action, err := domain.NewDeconstructionAction(domain.ActionID(fmt.Sprintf("%s-0", snapshot.Plan)), value)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	plan, err := domain.NewPlan(snapshot.Plan, 1, []domain.Action{action})
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	clockSchedulerLog("%s: retire cooking campfire %s at %d,%d", goal.Goal.ID, campfire.ID, campfire.Cells[0].X, campfire.Cells[0].Z)
	facts := reading.Projection
	return r.admitExcavation(call, epoch, excavationStep{state: state, review: review, goal: goal, facts: facts, read: reading}, snapshot, method, plan, nil, policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}, check)
}
