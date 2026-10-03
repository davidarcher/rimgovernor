package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// Cremating raiders (#833): MaintainWaste places a crematorium in a free
// workshop slot while stranger corpses lie unburied, then gives it a
// forever CremateCorpse bill that takes stranger corpses only
// (policy.NextCremationStep). Rotting and desiccated animal corpses get a
// second forever bill that excludes fresh ones (#1810). Colonists are never
// cremated.

// cremationBills reads bench bills and previews one; a source without it
// never cremates.
type cremationBills interface {
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
}

// cremationMethod names a cremation step's method, once per goal epoch.
func cremationMethod(step policy.CremationStep) domain.MethodID {
	if step.Kind == policy.CremationBill {
		return cremationBillMethod(step.Bench, domain.CorpseStranger, step.StrangerSpoiledOnly)
	}
	return domain.MethodID(fmt.Sprintf("cremate-place-%d-%d-%s", step.Room.Interior.X, step.Room.Interior.Z, step.Piece.Slot))
}

// stageCremation answers a due cremation step; handled is false when none
// is due or the bill already stands, so the haul flow runs.
func (r *RoutineWastePlanner) stageCremation(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, reading observation.RoutineReading) (RoutineWasteResult, bool, error) {
	bills, ok := r.native.(cremationBills)
	if !ok || r.building == nil {
		return RoutineWasteResult{}, false, nil
	}
	step := cremationStep(reading.Projection)
	switch step.Kind {
	case policy.CremationPlace:
		clockSchedulerLog("%s: crematorium place (strangers %d)", goal.Goal.ID, step.Strangers)
		result, err := r.building.placePiece(call, epoch, state, review, goal, reading, step.Piece, cremationMethod(step))
		return RoutineWasteResult{Verdict: result.Verdict}, true, err
	case policy.CremationBill:
		return r.cremationBill(call, epoch, state, goal, bills, step)
	}
	return RoutineWasteResult{}, false, nil
}

// cremationBillMethod names one corpse class's bill method on a bench.
func cremationBillMethod(bench string, of domain.CorpseOf, spoiledOnly bool) domain.MethodID {
	if of == domain.CorpseAnimal {
		return domain.MethodID("cremate-bill-" + bench + "-animal")
	}
	if spoiledOnly {
		return domain.MethodID("cremate-bill-" + bench + "-spoiled")
	}
	return domain.MethodID("cremate-bill-" + bench)
}

// cremationBill commits each due class's CremateCorpse bill once per goal
// epoch (native applies a bill it already carries again as a no-op).
func (r *RoutineWastePlanner) cremationBill(call, epoch context.Context, state ControlState, goal store.GoalState, bills cremationBills, step policy.CremationStep) (RoutineWasteResult, bool, error) {
	p := r.reviewer.player
	var of domain.CorpseOf
	switch {
	case step.Strangers > 0:
		of = domain.CorpseStranger
	case step.Animals > 0:
		of = domain.CorpseAnimal
	}
	if of == domain.CorpseStranger {
		if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, cremationBillMethod(step.Bench, of, step.StrangerSpoiledOnly)); err == nil {
			of = ""
			if step.Animals > 0 {
				of = domain.CorpseAnimal
			}
		}
	}
	method := cremationBillMethod(step.Bench, of, step.StrangerSpoiledOnly)
	if of == "" {
		return RoutineWasteResult{}, false, nil
	}
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineWasteResult{}, false, nil
	}
	identity := boundary.Identity(state.Snapshot)
	census, _, err := bills.ReadGearBenches(call, identity)
	if err != nil {
		return RoutineWasteResult{}, true, err
	}
	token := ""
	for _, row := range census {
		if row.Bench.ID != step.Bench {
			continue
		}
		if _, known := row.Bench.Bills.Value(); !known {
			return RoutineWasteResult{Verdict: fieldUnavailable("cremation_bills")}, true, nil
		}
		token = row.Token
	}
	if token == "" {
		return RoutineWasteResult{Verdict: fieldUnavailable("cremation_bench")}, true, nil
	}
	minRot := domain.RotStage("")
	if of == domain.CorpseAnimal || of == domain.CorpseStranger && step.StrangerSpoiledOnly {
		minRot = domain.RotRotting
	}
	bill, err := domain.NewCorpseBill(step.Bench, domain.CremateRecipe, of, minRot)
	if err != nil {
		return RoutineWasteResult{}, true, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewProductionBillAction(domain.ActionID(fmt.Sprintf("%s-0", id)), bill)
	if err != nil {
		return RoutineWasteResult{}, true, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineWasteResult{}, true, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineWasteResult{}, true, err
	}
	if p.session.State() != state {
		return RoutineWasteResult{}, true, fmt.Errorf("%w: cremationBill: p.session.State() != state", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineWasteResult{}, true, err
	}
	clockSchedulerLog("%s: cremation bill on %s", goal.Goal.ID, step.Bench)
	return RoutineWasteResult{Verdict: BuildingReasonAdmitted}, true, nil
}
