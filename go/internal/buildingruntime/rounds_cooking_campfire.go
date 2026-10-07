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
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// campfireRetirement is the cooking campfire to deconstruct (#1179): any once
// a usable stove stands in a kitchen. A campfire the temperature family claimed is room heat and
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
		if !stove {
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
		if claim.Concern == policy.EnsureTemperatureSafety && claim.Building.Definition() == "Campfire" {
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

// temperatureOwed is the review's TemperatureOwed fact.
func temperatureOwed(facts observation.ColonyProjection) domain.Fact[bool] {
	return domain.Known(policy.TemperatureOwed(facts.Rooms, temperatureCooling(facts)))
}

// commitCampfireRefuel binds a one-action auto-refuel plan for the heat
// campfire the temperature proposal switches.
func (r *RoundsBuildingPlanner) commitCampfireRefuel(call context.Context, goal store.WorkOwner, check func() error) (RoundsBuildingResult, error) {
	p := r.reviewer.player
	proposal := r.temperature
	if proposal == nil || proposal.Method != policy.TemperatureRefuelOff && proposal.Method != policy.TemperatureRefuelOn {
		return RoundsBuildingResult{}, fmt.Errorf("%w: commitCampfireRefuel: not a refuel proposal", ErrControl)
	}
	if _, err := p.journal.LoadOwnerMethod(call, goal, proposal.Key); err == nil {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "campfire_refuel_method")}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoundsBuildingResult{}, err
	}
	value, err := domain.NewAutoRefuel(proposal.Thing, proposal.Method == policy.TemperatureRefuelOn)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewAutoRefuelAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if err = check(); err != nil {
		return RoundsBuildingResult{}, err
	}
	if err = p.journal.CommitOwnerMethod(call, goal, proposal.Key, "", plan); err != nil {
		return RoundsBuildingResult{}, err
	}
	return RoundsBuildingResult{Verdict: BuildingReasonAdmitted}, nil
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
// cooking campfire, once per campfire per Episode.
func (r *RoundsBuildingPlanner) retireCampfire(call, epoch context.Context, state ControlState, review store.Rounds, goal store.WorkOwner, reading observation.ColonyReading, campfire policy.CurrentBuilding) (RoundsBuildingResult, error) {
	return r.retireBuilding(call, epoch, state, review, goal, reading, campfire.ID, campfire.Building.Definition(), campfire.Cells[0], "campfire-retire", "building_retire_method")
}

// retireBuilding deconstructs one standing building, once per building per
// Episode under a method named prefix and its ID; reason is the wait reason
// once that method exists.
func (r *RoundsBuildingPlanner) retireBuilding(call, epoch context.Context, state ControlState, review store.Rounds, goal store.WorkOwner, reading observation.ColonyReading, id, def string, cell domain.Cell, prefix, reason string) (RoundsBuildingResult, error) {
	p := r.reviewer.player
	sum := sha256.Sum256([]byte(id))
	method := domain.MethodID(fmt.Sprintf("%s-%x", prefix, sum[:8]))
	if _, err := p.journal.LoadOwnerMethod(call, goal, method); err == nil {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, reason)}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoundsBuildingResult{}, err
	}
	snapshot := state.Snapshot
	snapshot.Plan = domain.MintPlanID()
	snapshot.Revision = 1
	check := func() error {
		if err := p.current(call, epoch); err != nil {
			return err
		}
		if p.session.State() != state {
			return fmt.Errorf("%w: retireBuilding: p.session.State() != state", ErrControl)
		}
		return nil
	}
	if err := check(); err != nil {
		return RoundsBuildingResult{}, err
	}
	value, err := domain.NewDeconstruction(id, def, cell)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	action, err := domain.NewDeconstructionAction(domain.ActionID(fmt.Sprintf("%s-0", snapshot.Plan)), value)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	plan, err := domain.NewPlan(snapshot.Plan, 1, []domain.Action{action})
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	telemetry.Decide(call, telemetry.Decision{Kind: "layout_edit", Component: "building", Verdict: "admitted", Reason: "building_retire", Target: id, Attrs: map[string]any{"family": "building", "owner": goal.OwnerID(), "x": cell.X, "z": cell.Z}})
	facts := reading.Projection
	return r.admitExcavation(call, epoch, excavationStep{state: state, review: review, owner: goal, facts: facts, read: reading}, snapshot, method, plan, nil, policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}, check)
}
