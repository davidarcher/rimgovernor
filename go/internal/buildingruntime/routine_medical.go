package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// medicineResourceDefinition is the one native resource definition
// MaintainMedicalReserves replenishes: a hardcoded MedicineHerbal target. A
// native policyResources presence check would be the right guard, but this
// boundary has no decode for it yet -- PolicyResources stays deliberately
// unread, see bridge.ValidateColonyFacts. Recipe-product matching in
// policy.SelectMedicineMethod already refuses to guess when no bench
// produces it.
const medicineResourceDefinition = policy.Resource("MedicineHerbal")

// RoutineMedicalSource is the native census RoutineMedicalPlanner reads
// immediately before proposing a medicine-production method: a fresh colony
// facts read for the current medicine stock (the top-level
// Resources/Upkeep sections used during ordinary routine review, not
// gated behind the planning flag) plus the same generic bench/recipe census
// and ingredient stock funding GearProduce already established
// (bridge.ReadGearBenches/ReadSupplyStock read every bench's bills and
// recipes regardless of what they produce, so no separate medical census
// type is needed -- SelectMedicineMethod only matches recipes whose
// Products include MedicineHerbal). PreviewBill re-checks one
// already-selected bench/recipe bill immediately before dispatch, the same
// acceptance-not-authority preview RoutineGearPlanner and RoutineBillPlanner
// use.
type RoutineMedicalSource interface {
	ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*o.ColonyFactsReply, bridge.Result, error)
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
	ReadSupplyStock(context.Context, *c.Identity, []string) ([]policy.Stock, bridge.Result, error)
	PreviewBill(context.Context, *c.Identity, domain.ProductionBill) (*op.PreviewReply, bridge.Result, error)
}
type RoutineMedicalPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineMedicalSource
}
type RoutineMedicalResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineMedicalPlanner(reviewer *RoutineReviewer, native RoutineMedicalSource) (*RoutineMedicalPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineMedicalPlanner{reviewer, native}, nil
}
func (r *RoutineMedicalPlanner) Step(ctx context.Context) (RoutineMedicalResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

// medicalOptionalBool and medicalOptionalTicks mirror observation.optional's
// generic pointer-to-Fact lift for the two medicine-stack fields this
// boundary decodes; medicalIssue mirrors observation.hasIssue. These are
// duplicated here (rather than exported from the observation package)
// because the routine planner reads a fresh census of its own immediately
// before proposing a method, the same way RoutineGearPlanner duplicates
// gearObservationFacts rather than reusing the review's cached facts.
func medicalOptionalBool(v *bool) domain.Fact[bool] {
	if v == nil {
		return domain.Unknown[bool]()
	}
	return domain.Known(*v)
}
func medicalOptionalTicks(v *int64) domain.Fact[int64] {
	if v == nil {
		return domain.Unknown[int64]()
	}
	return domain.Known(*v)
}
func medicalIssue(issues []*o.ReadIssue, field string) bool {
	for _, issue := range issues {
		if issue.GetField() == field {
			return true
		}
	}
	return false
}

// medicalReserveObservationFacts decodes the same medicine-reserve section
// observation.colonyMedicalReserve does, from a freshly read
// ColonyFactsSnapshot rather than the cached routine review snapshot.
func medicalReserveObservationFacts(v *o.ColonyFactsSnapshot) policy.MedicalReserveObservation {
	r := policy.MedicalReserveObservation{}
	if v.ColonistCount != nil {
		r.Colonists = domain.Known(int64(v.GetColonistCount()))
	}
	if !medicalIssue(v.Issues, "resources") {
		rows := []policy.Amount{}
		known := true
		for _, q := range v.Resources {
			if q.Units == nil {
				known = false
				break
			}
			rows = append(rows, policy.Amount{Resource: policy.Resource(q.GetDefName()), Count: q.GetUnits()})
		}
		if known {
			r.Resources = domain.Known(rows)
		}
	}
	u := v.GetUpkeep().GetObserved()
	if u == nil || medicalIssue(u.Issues, "items") {
		return r
	}
	rows := []policy.MedicineStack{}
	for _, item := range u.Items {
		if item.Medicine == nil {
			return r
		}
		if !item.GetMedicine() {
			continue
		}
		if item.Count == nil || item.Forbidden == nil {
			return r
		}
		rows = append(rows, policy.MedicineStack{ID: item.Item.GetId(), Definition: policy.Resource(item.Item.GetDefName()), Count: item.GetCount(), Forbidden: item.GetForbidden(), Perishable: medicalOptionalBool(item.Perishable), RotTicks: medicalOptionalTicks(item.RotTicks)})
	}
	r.Items = domain.Known(rows)
	return r
}

func (r *RoutineMedicalPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineMedicalResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineMedicalResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineMedicalResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineMedicalResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainMedicalReserves {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineMedicalResult{Reason: BuildingMethodNoDeficit}, nil
	}
	stalledSources := map[string]bool{}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineMedicalResult{}, err
		}
		stalled, err := stalledAcquisitionDesignations(call, p.journal, plan.Progress, nil, review.Tick, r.reviewer.policy.AcquisitionStallTicks)
		if err != nil {
			return RoutineMedicalResult{}, err
		}
		for _, v := range stalled {
			if _, err = p.journal.Cancel(call, method.Plan, v.Action); err != nil {
				return RoutineMedicalResult{}, err
			}
			stalledSources[v.Thing] = true
		}
		if len(stalled) > 0 {
			if plan, err = p.journal.LoadPlan(call, method.Plan); err != nil {
				return RoutineMedicalResult{}, err
			}
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineMedicalResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.native.ReadColonyFacts(call, identity, false, nil)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineMedicalResult{}, ErrControl
	}
	if err = bridge.ValidateColonyFacts(observed, identity); err != nil {
		return RoutineMedicalResult{}, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoutineMedicalResult{}, ErrControl
	}
	facts := medicalReserveObservationFacts(observed)
	medicalReview, err := policy.ReviewMedicalReserve(facts, review.Latches.MedicalReserve, r.reviewer.policy.MedicalReserve)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	if !medicalReview.Active {
		return RoutineMedicalResult{Reason: BuildingMethodUsed}, nil
	}
	seen := make([]domain.MethodID, 0, len(goal.Methods))
	for _, method := range goal.Methods {
		seen = append(seen, method.Method)
	}
	census, _, err := r.native.ReadGearBenches(call, identity)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	if len(census) > 256 {
		return RoutineMedicalResult{}, ErrControl
	}
	benches := make([]policy.GearBench, 0, len(census))
	tokens := map[string]string{}
	for _, row := range census {
		benches = append(benches, row.Bench)
		tokens[row.Bench.ID] = row.Token
	}
	names := recipeIngredientNames(census, medicineResourceDefinition)
	var stock []policy.Stock
	if len(names) > 0 {
		stock, _, err = r.native.ReadSupplyStock(call, identity, names)
		if err != nil {
			return RoutineMedicalResult{}, err
		}
	}
	choice, err := policy.SelectMedicineMethod(policy.MedicinePlanningRequest{Review: medicalReview, Resource: medicineResourceDefinition, Seen: seen, Benches: domain.Known(benches), Stock: stock})
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	if choice.Kind == policy.MedicineBlocked {
		return r.harvestMedicine(call, epoch, state, goal, observed, medicalReview, stalledSources, started)
	}
	if choice.Kind != policy.MedicineProduce {
		return RoutineMedicalResult{Reason: BuildingMethodUsed}, nil
	}
	token, ok := tokens[choice.Bench]
	if !ok {
		return RoutineMedicalResult{}, ErrControl
	}
	if !arbiter.tryClaim(nil, "bench:"+choice.Bench) {
		return RoutineMedicalResult{Reason: BuildingMethodUsed}, nil
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, choice.ID)))
	id := domain.PlanID(fmt.Sprintf("routine-medical-%x", digest[:16]))
	target := int32(choice.Target)
	if int64(target) != choice.Target {
		return RoutineMedicalResult{}, ErrControl
	}
	bill, err := domain.NewProductionBill(choice.Bench, choice.Recipe, token, domain.StockTarget, target)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	preview, _, err := r.native.PreviewBill(call, boundary.Identity(state.Snapshot), bill)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil || !evaluated.GetAccepted() {
		return RoutineMedicalResult{Reason: BuildingMethodRefused}, nil
	}
	if _, err = boundary.Context(evaluated.Context, state.Snapshot); err != nil {
		return RoutineMedicalResult{}, ErrControl
	}
	action, err := domain.NewProductionBillAction(domain.ActionID(fmt.Sprintf("%s-0", id)), bill)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineMedicalResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineMedicalResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, choice.ID, plan); err != nil {
		return RoutineMedicalResult{}, err
	}
	return RoutineMedicalResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

// harvestMedicine is MaintainMedicalReserves' method when no bench produces
// herbal medicine (a tribal start): an ordinary plant-cutting acquisition of
// the nearest native-approved wild plants yielding the medicine definition
// (wild healroot), sized by the review's replenishment and the harvest
// already designated. The same executor and native designation path
// EnsureFoodSupply's berry harvest uses carry it out; recovery is still
// only the observed reserve.
func (r *RoutineMedicalPlanner) harvestMedicine(call, epoch context.Context, state ControlState, goal store.GoalState, observed *o.ColonyFactsSnapshot, medicalReview policy.MedicalReserveReview, stalledSources map[string]bool, started time.Time) (RoutineMedicalResult, error) {
	p := r.reviewer.player
	replenish, known := medicalReview.Replenish.Value()
	if !known {
		return RoutineMedicalResult{Reason: BuildingMethodUnknown}, nil
	}
	if replenish <= 0 {
		return RoutineMedicalResult{Reason: BuildingMethodNoDeficit}, nil
	}
	sources := observation.ColonyAcquisition(observed)
	rows, known := sources.Value()
	if !known {
		return RoutineMedicalResult{Reason: BuildingMethodUnknown}, nil
	}
	// A designation this step cancelled as stalled (#291) is still on the
	// plant; counting its yield as pending would leave nothing to replenish.
	pending := 0.0
	for _, row := range rows {
		if row.Designated && !row.Hunt && !stalledSources[row.ID] && policy.Resource(row.Resource) == medicineResourceDefinition {
			pending += row.Yield
		}
	}
	plans, err := p.journal.LoadPlans(call, 256)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	held := map[string]bool{}
	for _, plan := range plans {
		for _, progress := range plan.Progress {
			if acquisition, ok := progress.Action().Acquisition(); ok && domain.GoalWorkOpen([]domain.Progress{progress}) {
				held[acquisition.Thing()] = true
			}
		}
	}
	selected, err := policy.SelectResourceAcquisition(sources, domain.Known(float64(replenish)), domain.Known(pending), medicineResourceDefinition, held)
	if err != nil {
		return RoutineMedicalResult{Reason: BuildingMethodUnknown}, nil
	}
	if len(selected) == 0 {
		return RoutineMedicalResult{Reason: BuildingMethodUsed}, nil
	}
	hash := sha256.New()
	for _, row := range selected {
		fmt.Fprintf(hash, "%s/%s/%s/%d/%d\n", row.ID, row.Resource, row.Token, row.Cell.X, row.Cell.Z)
	}
	method := domain.MethodID(fmt.Sprintf("acquire-%x", hash.Sum(nil)[:16]))
	if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineMedicalResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineMedicalResult{}, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-medical-acquire-%x", digest[:16]))
	var actions []domain.Action
	for i, row := range selected {
		value, err := domain.NewAcquisition(row.ID, row.Resource, row.Cell)
		if err != nil {
			return RoutineMedicalResult{}, err
		}
		action, err := domain.NewAcquisitionAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), value)
		if err != nil {
			return RoutineMedicalResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineMedicalResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineMedicalResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineMedicalResult{}, err
	}
	return RoutineMedicalResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
