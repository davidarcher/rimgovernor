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
// (policy.NextTombStep). With every tomb full the layout review grows
// another; a plain grave stands in when no sarcophagus can be had (#857).
// Vanilla haulers inter the body.

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
		return r.stageCremation(call, epoch, state, review, goal, reading)
	}
	clockSchedulerLog("%s: tomb %s (dead %d, empty %d)", goal.Goal.ID, step.Kind, step.Dead, step.Empty)
	var result RoutineBuildingResult
	switch step.Kind {
	case policy.TombShell:
		result, err = r.building.shellRoom(call, epoch, state, review, goal, reading, step.Room, tombMethod(step), "routine-waste-tomb", "")
	case policy.TombPlace:
		result, err = r.placePiece(call, epoch, state, review, goal, reading, step.Piece, tombMethod(step), "routine-waste-tomb")
	case policy.TombFull:
		// The layout review grows another tomb; a grave only once it
		// found no room for one; cremation goes on meanwhile.
		plan, _ := reading.Projection.LayoutPlan.Value()
		if refused := r.reviewer.tombsRefused; refused == 0 || refused != plan.TombRooms()+1 {
			return r.stageCremation(call, epoch, state, review, goal, reading)
		}
		result, err = r.placeGrave(call, epoch, state, review, goal, reading, step)
	case policy.TombGrave:
		result, err = r.placeGrave(call, epoch, state, review, goal, reading, step)
	}
	return RoutineWasteResult{Reason: result.Reason}, true, err
}

// graveSiteTries bounds the free sites a grave previews.
const graveSiteTries = 4

// placeGrave places a plain grave (#857) on the free 1x2 site nearest the
// fields anchor, trying the next site while native refuses one.
func (r *RoutineWastePlanner) placeGrave(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, reading observation.RoutineReading, step policy.TombStep) (RoutineBuildingResult, error) {
	facts := reading.Projection
	sites, err := policy.FreeSites(policy.PenEnclosureRequest{Bounds: facts.Bounds, Anchor: layoutAnchor(facts, policy.DistrictFields), Cells: facts.Cells, Protected: layoutProtected(facts, nil)}, 1, 2)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	method := domain.MethodID(fmt.Sprintf("tomb-grave-%d", step.Graves))
	result := RoutineBuildingResult{Reason: BuildingMethodNoSpace}
	for i, site := range sites {
		if i == graveSiteTries {
			break
		}
		piece := policy.NewInteriorPiece("grave", policy.GraveDefinition, domain.Cell{X: 1, Z: 2}, domain.North, domain.Cell{X: site.X, Z: site.Z})
		if result, err = r.placePiece(call, epoch, state, review, goal, reading, piece, method, "routine-waste-grave"); err != nil || result.Reason != BuildingMethodNoSpace {
			return result, err
		}
	}
	return result, nil
}

// placePiece previews and admits one interior piece: a sarcophagus (#832)
// or the crematorium (#833).
func (r *RoutineWastePlanner) placePiece(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, reading observation.RoutineReading, piece policy.InteriorPiece, method domain.MethodID, prefix string) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	facts := reading.Projection
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineBuildingResult{Reason: BuildingMethodUsed}, nil
	}
	stuff := ""
	for _, d := range facts.Definitions {
		if d.Name == piece.Def {
			stuff, _ = d.Stuff.Value()
		}
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	snapshot := state.Snapshot
	snapshot.Plan = domain.PlanID(fmt.Sprintf("%s-%x", prefix, digest[:16]))
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
	building, err := domain.NewBuilding(piece.Def, piece.Anchor(), piece.Rot, stuff)
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
		clockSchedulerLog("%s: %s %s refused at %d,%d", goal.Goal.ID, piece.Def, piece.Slot, piece.Anchor().X, piece.Anchor().Z)
		return RoutineBuildingResult{Reason: BuildingMethodNoSpace}, nil
	}
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	if err := mergeRoutineStock(&stock, preview.Stock, true); err != nil {
		return RoutineBuildingResult{}, err
	}
	return r.building.admitPreviews(call, epoch, routineAdmission{state: state, review: review, goal: goal, facts: facts, read: reading.ColonyReading, method: method, snapshot: snapshot, selected: []policy.Preview{v}, stock: stock, purpose: policy.Shelter, check: check})
}
