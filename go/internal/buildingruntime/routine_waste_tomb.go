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

// Staging the tomb (#832): MaintainWaste raises the planned tomb and
// places one sarcophagus at a time while a dead colonist has none waiting
// (policy.NextTombStep). Vanilla haulers inter the body.

// tombMethod names a tomb step's method: the shell once per room, each
// sarcophagus slot once, per goal epoch.
func tombMethod(step policy.TombStep) domain.MethodID {
	if step.Kind == policy.TombPlace {
		return domain.MethodID(fmt.Sprintf("tomb-place-%d-%d-%s", step.Room.Interior.X, step.Room.Interior.Z, step.Piece.Slot))
	}
	return domain.MethodID(fmt.Sprintf("tomb-shell-%d-%d", step.Room.Interior.X, step.Room.Interior.Z))
}

// stageTomb answers a due tomb step; handled is false when none is due.
func (r *RoutineWastePlanner) stageTomb(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, expected observation.Identity) (RoutineWasteResult, bool, error) {
	source, ok := r.native.(observation.RoutineSource)
	if !ok || r.building == nil {
		return RoutineWasteResult{}, false, nil
	}
	reading, err := r.reviewer.observeRooms(call, source, expected, domain.Unknown[[]policy.ConstructionClaim](), wasteDefinitions...)
	if err != nil {
		return RoutineWasteResult{}, false, err
	}
	step := tombStep(reading.Projection)
	if step.Kind == policy.TombNone {
		return RoutineWasteResult{}, false, nil
	}
	clockSchedulerLog("%s: tomb %s (dead %d, empty %d)", goal.Goal.ID, step.Kind, step.Dead, step.Empty)
	var result RoutineBuildingResult
	if step.Kind == policy.TombShell {
		result, err = r.building.shellRoom(call, epoch, state, review, goal, reading, step.Room, tombMethod(step), "routine-waste-tomb")
	} else {
		result, err = r.placeSarcophagus(call, epoch, state, review, goal, reading, step)
	}
	return RoutineWasteResult{Reason: result.Reason}, true, err
}

// placeSarcophagus previews and admits the step's sarcophagus.
func (r *RoutineWastePlanner) placeSarcophagus(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, reading observation.RoutineReading, step policy.TombStep) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	facts := reading.Projection
	method := tombMethod(step)
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineBuildingResult{Reason: BuildingMethodUsed}, nil
	}
	stuff := ""
	for _, d := range facts.Definitions {
		if d.Name == step.Piece.Def {
			stuff, _ = d.Stuff.Value()
		}
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	snapshot := state.Snapshot
	snapshot.Plan = domain.PlanID(fmt.Sprintf("routine-waste-tomb-%x", digest[:16]))
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
	building, err := domain.NewBuilding(step.Piece.Def, step.Piece.Anchor(), step.Piece.Rot, stuff)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", snapshot.Plan)), building)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	preview, _, err := r.building.native.PreviewBuilding(call, action, snapshot)
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
		clockSchedulerLog("%s: sarcophagus %s refused at %d,%d", goal.Goal.ID, step.Piece.Slot, step.Piece.Anchor().X, step.Piece.Anchor().Z)
		return RoutineBuildingResult{Reason: BuildingMethodNoSpace}, nil
	}
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	if err := mergeRoutineStock(&stock, preview.Stock, true); err != nil {
		return RoutineBuildingResult{}, err
	}
	return r.building.admitPreviews(call, epoch, routineAdmission{state: state, review: review, goal: goal, facts: facts, read: reading.ColonyReading, method: method, snapshot: snapshot, selected: []policy.Preview{v}, stock: stock, purpose: policy.Shelter, check: check})
}
