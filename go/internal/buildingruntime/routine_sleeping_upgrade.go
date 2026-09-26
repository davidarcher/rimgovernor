package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// roomUpgrade is the next room quality upgrade (#814): one template piece
// for an owned bedroom below its target.
func roomUpgrade(facts observation.ColonyProjection) (policy.RoomUpgrade, bool) {
	obs, sk := facts.Facts.Sleeping.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	traits := sleepingTraits(facts)
	if !sk || !rk || !ck || !census.Colony || traits == nil {
		return policy.RoomUpgrade{}, false
	}
	tier, _ := facts.BuildTier.Value()
	targets := policy.RoomQualityTargets(obs, traits, tier)
	for id, t := range policy.CommonRoomTargets(obs, tier) {
		if _, owned := targets[id]; !owned {
			targets[id] = t
		}
	}
	available := func(def string) bool {
		v, known := facts.DefinitionAvailable(def).Value()
		return known && v
	}
	return policy.NextRoomUpgrade(obs, targets, policy.TidyFurnitureRooms(rooms, census, facts.Cells), available)
}

// upgradeBedroom previews and admits one upgrade piece, once per room and
// slot per goal epoch.
func (r *RoutineSleepingUpkeepPlanner) upgradeBedroom(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, reading observation.RoutineReading, u policy.RoomUpgrade) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	facts := reading.Projection
	room := sha256.Sum256([]byte(u.Room))
	method := domain.MethodID(fmt.Sprintf("bedroom-upgrade-%s-%x", u.Slot, room[:8]))
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineBuildingResult{Reason: BuildingMethodUsed}, nil
	}
	stuff := ""
	for _, d := range facts.Definitions {
		if d.Name == u.Def {
			stuff, _ = d.Stuff.Value()
		}
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	snapshot := state.Snapshot
	snapshot.Plan = domain.PlanID(fmt.Sprintf("routine-sleeping-upgrade-%x", digest[:16]))
	snapshot.Revision = 1
	check := func() error {
		if err := p.current(call, epoch); err != nil {
			return err
		}
		if p.session.State() != state {
			return ErrControl
		}
		return nil
	}
	if err := check(); err != nil {
		return RoutineBuildingResult{}, err
	}
	building, err := domain.NewBuilding(u.Def, u.Anchor, u.Rot, stuff)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", snapshot.Plan)), building)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	preview, _, err := r.native.PreviewBuilding(call, action, snapshot)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	v := preview.Preview
	if v.Action != action || !v.Snapshot.Matches(snapshot) || !preview.Stock.Snapshot.Matches(snapshot) {
		return RoutineBuildingResult{}, ErrControl
	}
	can, ck := v.CanPlace.Value()
	safe, sk := v.SafeToPlace.Value()
	if !ck || !can || !sk || !safe {
		clockSchedulerLog("%s: bedroom upgrade %s %s refused at %d,%d", goal.Goal.ID, u.Room, u.Def, u.Anchor.X, u.Anchor.Z)
		return RoutineBuildingResult{Reason: BuildingMethodNoSpace}, nil
	}
	clockSchedulerLog("%s: bedroom upgrade %s %s (weakest %s)", goal.Goal.ID, u.Room, u.Def, u.Weakest)
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	if err := mergeRoutineStock(&stock, preview.Stock, true); err != nil {
		return RoutineBuildingResult{}, err
	}
	return r.building.admitPreviews(call, epoch, routineAdmission{state: state, review: review, goal: goal, facts: facts, read: reading.ColonyReading, method: method, snapshot: snapshot, selected: []policy.Preview{v}, stock: stock, purpose: policy.Shelter, check: check})
}
