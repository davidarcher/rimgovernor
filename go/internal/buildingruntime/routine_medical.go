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
)

// RoutineMedicalSource is the native census RoutineMedicalPlanner reads
// immediately before proposing a medicine-production method: a fresh colony
// facts read for the current medicine stock (the top-level
// Resources/Upkeep sections used during ordinary routine review, not
// gated behind the planning flag) plus the same generic bench/recipe census
// and ingredient stock funding GearProduce already established
// (bridge.ReadGearBenches/ReadSupplyStock read every bench's bills and
// recipes regardless of what they produce, so no separate medical census
// type is needed -- SelectMedicineMethod only matches recipes whose
// Products include MedicineHerbal). Native checks the bill against
// live state when the ProductionBillIntent applies.
type RoutineMedicalSource interface {
	ReadColonyFacts(context.Context, *c.Identity, bool) (*o.ColonyFactsReply, bridge.Result, error)
	FrameTables(context.Context, *c.Identity) (bridge.Tables, error)
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
	ReadSupplyStock(context.Context, *c.Identity, []string) ([]policy.Stock, bridge.Result, error)
}
type RoutineMedicalPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineMedicalSource
}
type RoutineMedicalResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoutineMedicalPlanner(reviewer *RoutineReviewer, native RoutineMedicalSource) (*RoutineMedicalPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineMedicalPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoutineMedicalPlanner{reviewer, native}, nil
}

// medicalIssue mirrors observation.hasIssue for the fresh census the
// routine planners read.
func medicalIssue(issues []*o.ReadIssue, field string) bool {
	for _, issue := range issues {
		if issue.GetField() == field {
			return true
		}
	}
	return false
}

func (r *RoutineMedicalPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineMedicalResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineMedicalResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineMedicalResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineMedicalResult{Verdict: BuildingReasonNoReview}, nil
	}
	if result, err := r.planAmputation(call, epoch, state, review); err != nil || result.Plan != "" {
		return result, err
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainMedicalReserves)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	if !workable || !review.Latches.Medical.Restocks() {
		return RoutineMedicalResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.reviewer.colonyFacts(call, r.native, identity, false)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineMedicalResult{}, fmt.Errorf("%w: step: observed == nil", ErrControl)
	}
	if err = bridge.ValidateColonyFacts(observed, identity); err != nil {
		return RoutineMedicalResult{}, fmt.Errorf("%w: step: err != nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoutineMedicalResult{}, fmt.Errorf("%w: step: err != nil || observed.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	tables, err := r.native.FrameTables(call, identity)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	// The one definition MaintainMedicalReserves replenishes is the lowest
	// potency medicine of the catalog (herbal in Core); recipe-product
	// matching in policy.SelectMedicineMethod refuses to guess when no bench
	// produces it.
	items, err := r.reviewer.itemFacts(call, state.Snapshot)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	medicineResource, err := items.MedicineAt(0)
	if err != nil {
		return RoutineMedicalResult{Verdict: fieldUnavailable("medicine_catalog")}, nil
	}
	// Stalls are read from the acquisition census (#1044): a designated,
	// untaken plant past AcquisitionStallTicks since native first saw it.
	sources := observation.ColonyAcquisition(observed, tables)
	// Each is withdrawn by its own one-action method (#1046), admitted
	// before anything else and using up this step's admission.
	stalledSources := map[string]bool{}
	medicine := func(row policy.AcquisitionSource) bool {
		return policy.Resource(row.Resource) == medicineResource
	}
	for _, row := range stalledDesignations(sources, false, domain.Tick(observed.Context.GetTick()), r.reviewer.policy.AcquisitionProgress(), medicine) {
		stalledSources[row.ID] = true
		method, plan, err := stallWithdraw(row)
		if err != nil {
			return RoutineMedicalResult{}, err
		}
		if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
			continue
		} else if !errors.Is(err, store.ErrNotFound) {
			return RoutineMedicalResult{}, err
		}
		if err = p.current(call, epoch); err != nil {
			return RoutineMedicalResult{}, err
		}
		if p.session.State() != state {
			return RoutineMedicalResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
		}
		if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
			return RoutineMedicalResult{}, err
		}
		return RoutineMedicalResult{Verdict: BuildingReasonAdmitted, Plan: plan.ID()}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineMedicalResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineMedicalResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	facts := observation.ColonyMedicalReserve(observed, tables)
	facts.Catalog = items
	medicalReview, err := policy.ReviewMedicalReserve(facts, review.Latches.MedicalReserve, r.reviewer.policy.MedicalReserve)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	if !medicalReview.Active {
		return RoutineMedicalResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	seen := make([]domain.MethodID, 0, len(goal.Methods))
	for _, method := range goal.Methods {
		seen = append(seen, method.Method)
	}
	census, _, err := r.native.ReadGearBenches(call, identity)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	benches := make([]policy.GearBench, 0, len(census))
	tokens := map[string]string{}
	for _, row := range census {
		benches = append(benches, row.Bench)
		tokens[row.Bench.ID] = row.Token
	}
	names := recipeIngredientNames(census, medicineResource)
	var stock []policy.Stock
	if len(names) > 0 {
		stock, _, err = r.native.ReadSupplyStock(call, identity, names)
		if err != nil {
			return RoutineMedicalResult{}, err
		}
	}
	choice, err := policy.SelectMedicineMethod(policy.MedicinePlanningRequest{Review: medicalReview, Resource: medicineResource, Seen: seen, Benches: domain.Known(benches), Stock: stock})
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	if choice.Kind == policy.MedicineBlocked {
		return r.harvestMedicine(call, epoch, state, goal, observed, tables, medicalReview, facts, medicineResource, stalledSources, started)
	}
	if choice.Kind != policy.MedicineProduce {
		return RoutineMedicalResult{Verdict: medicineChoiceVerdict(choice.Kind, medicineResource)}, nil
	}
	_, ok := tokens[choice.Bench]
	if !ok {
		return RoutineMedicalResult{}, fmt.Errorf("%w: step: !ok", ErrControl)
	}
	if !arbiter.tryClaim(nil, "bench:"+choice.Bench) {
		return RoutineMedicalResult{Verdict: claimHeld("bench")}, nil
	}
	id := domain.MintPlanID()
	target := int32(choice.Target)
	if int64(target) != choice.Target {
		return RoutineMedicalResult{}, fmt.Errorf("%w: step: int64(target) != choice.Target", ErrControl)
	}
	bill, err := domain.NewProductionBill(choice.Bench, choice.Recipe, domain.StockTarget, target)
	if err != nil {
		return RoutineMedicalResult{}, err
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
		return RoutineMedicalResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, choice.ID, plan); err != nil {
		return RoutineMedicalResult{}, err
	}
	return RoutineMedicalResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

// harvestMedicine is MaintainMedicalReserves' method when no bench produces
// herbal medicine (a tribal start): an ordinary plant-cutting acquisition of
// the nearest native-approved wild plants yielding the medicine definition
// (wild healroot), sized by the review's replenishment and the harvest
// already designated. The same executor and native designation path
// EnsureFoodSupply's berry harvest uses carry it out; recovery is still
// only the observed reserve.
func (r *RoutineMedicalPlanner) harvestMedicine(call, epoch context.Context, state ControlState, goal store.GoalState, observed *o.ColonyFactsSnapshot, tables bridge.Tables, medicalReview policy.MedicalReserveReview, facts policy.MedicalReserveObservation, medicineResource policy.Resource, stalledSources map[string]bool, started time.Time) (RoutineMedicalResult, error) {
	p := r.reviewer.player
	replenish, known := medicalReview.Replenish.Value()
	if !known {
		return RoutineMedicalResult{Verdict: fieldUnavailable(unreadMedicalFact(facts))}, nil
	}
	if replenish <= 0 {
		return RoutineMedicalResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	sources := observation.ColonyAcquisition(observed, tables)
	rows, known := sources.Value()
	if !known {
		return RoutineMedicalResult{Verdict: fieldUnavailable("acquisition_sources")}, nil
	}
	// A designation this step cancelled as stalled (#291) is still on the
	// plant; counting its yield as pending would leave nothing to replenish.
	pending := 0.0
	for _, row := range rows {
		if row.Designated && !row.Hunt && !stalledSources[row.ID] && policy.Resource(row.Resource) == medicineResource {
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
	selected, err := policy.SelectResourceAcquisition(sources, domain.Known(float64(replenish)), domain.Known(pending), medicineResource, held)
	if err != nil {
		// The census was read; a row in it is unusable.
		return RoutineMedicalResult{Verdict: fieldUnavailable("acquisition_sources")}, nil
	}
	if len(selected) == 0 {
		if pending >= float64(replenish) {
			return RoutineMedicalResult{Verdict: BuildingReasonExistingWork}, nil
		}
		return RoutineMedicalResult{Verdict: awaitingPlan("medicine_source", "")}, nil
	}
	hash := sha256.New()
	for _, row := range selected {
		fmt.Fprintf(hash, "%s/%s/%s/%d/%d\n", row.ID, row.Resource, row.Token, row.Cell.X, row.Cell.Z)
	}
	method := domain.MethodID(fmt.Sprintf("acquire-%x", hash.Sum(nil)[:16]))
	if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineMedicalResult{Verdict: BuildingReasonUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineMedicalResult{}, err
	}
	id := domain.MintPlanID()
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
		return RoutineMedicalResult{}, fmt.Errorf("%w: harvestMedicine: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineMedicalResult{}, err
	}
	return RoutineMedicalResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

// unreadMedicalFact names the census the reserve review lacks: the review
// reads no replenishment until the colonist count, the medicine stacks and the
// usable stock are all read.
func unreadMedicalFact(facts policy.MedicalReserveObservation) string {
	if _, known := facts.Colonists.Value(); !known {
		return "colonists"
	}
	if _, known := facts.Items.Value(); !known {
		return "medicine_items"
	}
	return "medicine_stock"
}

// medicineChoiceVerdict is the verdict of a bench method choice that is
// neither a produce nor the plant-cutting fallback: the reserve recovered, a
// bench or recipe fact is unread, a bill already stands, or no bench can fund
// the product.
func medicineChoiceVerdict(kind policy.MedicineMethodKind, resource policy.Resource) Verdict {
	switch kind {
	case policy.MedicineRecovered:
		return BuildingReasonNoDeficit
	case policy.MedicineUnknown:
		return fieldUnavailable("bench_recipes")
	case policy.MedicineBlocked:
		return awaitingPlan("production_bench", string(resource))
	}
	return BuildingReasonUsed
}

// planAmputation is CriticalMedical's life-saving amputation (#1166): while
// the deficit stands, the first colonist whose limb infection is losing its
// immunity race gets one surgery bill on the infected part. Native doctor
// jobs choose the surgeon. Queueing is idempotent natively, and a failed
// operation consumes the bill, so the bill is re-sent once per
// amputationRequeueTicks window while the infection is still projected to
// win; there is no attempt cap, only vanilla's success chance against the
// projected death (policy.LifeSavingAmputations).
const amputationRequeueTicks = domain.TicksPerHour

func (r *RoutineMedicalPlanner) planAmputation(call, epoch context.Context, state ControlState, review store.RoutineReview) (RoutineMedicalResult, error) {
	p := r.reviewer.player
	incident, found, err := incidentDeficit(call, p.journal, review, policy.CriticalMedicine)
	if err != nil || !found {
		return RoutineMedicalResult{}, err
	}
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil || !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineMedicalResult{}, fmt.Errorf("%w: planAmputation: err != nil || !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	pawns, _ := read.Projection.Facts.MedicalPawns.Value()
	window := int64(expected.Tick) / amputationRequeueTicks
	for _, pawn := range pawns {
		for _, surgery := range policy.LifeSavingAmputations(pawn) {
			method := domain.MethodID(fmt.Sprintf("amputate-%s-%d-%d", surgery.Pawn(), surgery.Part(), window))
			queued := false
			for _, m := range incident.Methods {
				queued = queued || m.Method == method
			}
			if queued {
				continue
			}
			id := domain.MintPlanID()
			action, err := domain.NewSurgeryAction(domain.ActionID(fmt.Sprintf("%s-0", id)), surgery)
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
			if p.session.State() != state {
				return RoutineMedicalResult{}, fmt.Errorf("%w: planAmputation: p.session.State() != state", ErrControl)
			}
			if _, err = p.journal.CommitIncidentMethod(call, incident.Incident.ID, method, "", plan); err != nil {
				return RoutineMedicalResult{}, err
			}
			return RoutineMedicalResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
		}
	}
	return RoutineMedicalResult{}, nil
}
