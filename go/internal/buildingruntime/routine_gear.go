package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"sort"

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

// RoutineGearSource reads the same generic colony census (with planning
// facts requested) that carries the already-validated gear census
// (bridge.ReadColonyFacts runs validateColonyGear on it internally); no
// dedicated gear read is needed to plan a method, only to refresh CAS tokens
// immediately before dispatch (see bridge.ReadGearReplacement, used by
// GearReplaceBoundary). ReadGearBenches and ReadSupplyStock feed the
// workshop-bill half (GearProduce): a fresh bench/recipe census and the
// ingredient stock funding it, respectively, gathered only when no
// replace-candidate is already pending (SelectGearMethod always prefers
// wearing an existing item over crafting a new one). PreviewBill re-checks
// one already-selected bench/recipe bill immediately before dispatch, the
// same acceptance-not-authority preview RoutineBillPlanner uses for food.
type RoutineGearSource interface {
	ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*o.ColonyFactsReply, bridge.Result, error)
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
	ReadSupplyStock(context.Context, *c.Identity, []string) ([]policy.Stock, bridge.Result, error)
	PreviewBill(context.Context, *c.Identity, domain.ProductionBill) (*op.PreviewReply, bridge.Result, error)
}
type RoutineGearPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineGearSource
}
type RoutineGearResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineGearPlanner(reviewer *RoutineReviewer, native RoutineGearSource) (*RoutineGearPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineGearPlanner{reviewer, native}, nil
}
func (r *RoutineGearPlanner) Step(ctx context.Context) (RoutineGearResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineGearResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

// gearObservationFacts decodes an already-validated gear census the same way
// observation.colonyGear does for routine review. It is duplicated here
// (rather than exported from the observation package) because the routine
// planner reads a fresh census of its own immediately before proposing a
// method, the same way RoutineEquipPlanner rereads combat pawns and loose
// weapons rather than reusing the review's cached facts.
func gearObservationFacts(gear *o.GearSnapshot) policy.GearObservation {
	result := policy.GearObservation{Pawns: []policy.GearPawn{}, Stored: observation.GearStorageFacts(gear)}
	for _, p := range gear.GetPawns() {
		row := policy.GearPawn{Pawn: policy.PawnID(p.GetPawn().GetId()), Loadout: p.GetSnapshot().GetToken(), Blocked: p.Blocker != nil, Deficit: optionalBool(p.Deficit)}
		needs := []policy.GearReplacement{}
		for _, need := range p.GetReplacementNeeds() {
			needs = append(needs, policy.GearReplacement{Definition: policy.Resource(need.GetDefName()), Stuff: policy.Resource(need.GetStuff()), Reason: need.GetReason()})
		}
		row.Candidates = domain.Known(observation.GearCandidateFacts(p))
		row.Replacements = domain.Known(needs)
		row.Apparel = observation.GearApparelFacts(p.GetEquipment())
		row.Policy = observation.ApparelPolicyFacts(p)
		row.Climate = observation.GearClimateFacts(gear)
		result.Pawns = append(result.Pawns, row)
	}
	return result
}
func optionalBool(v *bool) domain.Fact[bool] {
	if v == nil {
		return domain.Unknown[bool]()
	}
	return domain.Known(*v)
}

func gearCandidateDefinition(observation policy.GearObservation, pawn policy.PawnID, target string) (string, bool) {
	for _, p := range observation.Pawns {
		if p.Pawn != pawn {
			continue
		}
		candidates, known := p.Candidates.Value()
		if !known {
			return "", false
		}
		for _, c := range candidates {
			if c.Target == target {
				return string(c.Definition), true
			}
		}
	}
	return "", false
}

func (r *RoutineGearPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineGearResult, error) {
	review, err := r.reviewer.player.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineGearResult{}, err
	}
	limit := review.Development.Capacity - len(review.Development.Committed)
	open := 0
	for _, binding := range review.Goals {
		if binding.Need != policy.MaintainEquipment {
			continue
		}
		goal, err := r.reviewer.player.journal.LoadGoal(call, binding.Goal)
		if err != nil {
			return RoutineGearResult{}, err
		}
		for _, method := range goal.Methods {
			plan, err := r.reviewer.player.journal.LoadPlan(call, method.Plan)
			if err != nil {
				return RoutineGearResult{}, err
			}
			// An apparel policy write holds no development slot (#660).
			if domain.GoalWorkOpen(plan.Progress) && !apparelPolicyPlan(plan.Spec) {
				open++
			}
		}
	}
	for _, goal := range review.Development.Committed {
		if goal == policy.MaintainEquipment {
			limit++
		}
	}
	limit -= open
	if limit <= 0 {
		if open > 0 {
			return RoutineGearResult{Reason: BuildingMethodExistingWork}, nil
		}
		return RoutineGearResult{Reason: BuildingMethodRefused}, nil
	}
	var admitted RoutineGearResult
	for i := 0; i < limit; i++ {
		result, err := r.stepOne(call, epoch, arbiter)
		if err != nil {
			return result, err
		}
		if result.Reason != BuildingMethodAdmitted {
			if admitted.Plan != "" {
				return admitted, nil
			}
			return result, nil
		}
		admitted = result
	}
	return admitted, nil
}

func apparelPolicyPlan(spec domain.PlanSpec) bool {
	for _, action := range spec.Actions() {
		if _, ok := action.ApparelPolicy(); !ok {
			return false
		}
	}
	return len(spec.Actions()) > 0
}

func (r *RoutineGearPlanner) stepOne(call, epoch context.Context, arbiter *stepArbiter) (RoutineGearResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineGearResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineGearResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineGearResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineGearResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainEquipment {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineGearResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineGearResult{Reason: BuildingMethodNoDeficit}, nil
	}
	busy := map[domain.PawnID]bool{}
	claimed := map[string]bool{}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineGearResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			for _, action := range plan.Spec.Actions() {
				if wear, ok := action.GearReplace(); ok {
					busy[wear.Pawn()] = true
					claimed[wear.Thing()] = true
				} else if settings, ok := action.ApparelPolicy(); ok {
					busy[settings.Pawn()] = true
				} else {
					return RoutineGearResult{Reason: BuildingMethodExistingWork}, nil
				}
			}
		}
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.native.ReadColonyFacts(call, identity, true, nil)
	if err != nil {
		return RoutineGearResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineGearResult{}, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoutineGearResult{}, ErrControl
	}
	gear := observed.GetPlanning().GetObserved().GetGear()
	if gear == nil || observed.ColonistCount == nil || uint32(len(gear.GetPawns())) != observed.GetColonistCount() {
		return RoutineGearResult{Reason: BuildingMethodUsed}, nil
	}
	observation := gearObservationFacts(gear)
	for i := range observation.Pawns {
		pawn := &observation.Pawns[i]
		pawn.Blocked = pawn.Blocked || busy[domain.PawnID(pawn.Pawn)]
		candidates, _ := pawn.Candidates.Value()
		available := []policy.GearCandidate{}
		for _, c := range candidates {
			if !claimed[c.Target] {
				available = append(available, c)
			}
		}
		pawn.Candidates = domain.Known(available)
	}
	// Configure vanilla dressing before choosing individual replacements. The
	// shared goal and Hands executor own this settings operation like wear work.
	// Every pawn that needs a policy is admitted in the same step: one write per
	// development slot per round spent a whole 60k-tick window assigning eight
	// colonists before any wear order or bill (#660). A write whose CAS token an
	// earlier write in the batch staled is cancelled and re-admitted next round,
	// so the batch converges in about one round per distinct role policy.
	var policies RoutineGearResult
	for _, pawn := range observation.Pawns {
		if pawn.Blocked {
			continue
		}
		value, needed := policy.DesiredApparelPolicy(pawn)
		if !needed {
			continue
		}
		digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, value.Encoded())))
		method := domain.MethodID(fmt.Sprintf("apparel-policy-%x", digest[:16]))
		seen := false
		for _, m := range goal.Methods {
			seen = seen || m.Method == method
		}
		if seen {
			continue
		}
		if !arbiter.tryClaim([]domain.PawnID{value.Pawn()}) {
			continue
		}
		id := domain.PlanID(method)
		action, err := domain.NewApparelPolicyAction(domain.ActionID(string(id)+"-0"), value)
		if err != nil {
			return RoutineGearResult{}, err
		}
		plan, err := domain.NewPlan(id, 1, []domain.Action{action})
		if err != nil {
			return RoutineGearResult{}, err
		}
		if err = p.current(call, epoch); err != nil {
			return RoutineGearResult{}, err
		}
		elapsed := r.reviewer.clock.Now().Sub(started)
		if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
			return RoutineGearResult{}, ErrControl
		}
		if goal, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
			return RoutineGearResult{}, err
		}
		policies = RoutineGearResult{Reason: BuildingMethodAdmitted, Plan: id}
	}
	if policies.Plan != "" {
		return policies, nil
	}
	seen := make([]domain.MethodID, 0, len(goal.Methods))
	for _, method := range goal.Methods {
		seen = append(seen, method.Method)
	}
	// SelectGearMethod always prefers wearing an already-observed replacement
	// candidate over crafting a new one and never reaches bench/recipe
	// selection while any candidate is pending, so the bench census (extra
	// native round trips) is only worth gathering once none exist. A wear
	// order still needs the candidate's own definition in a funded stock
	// (gearIngredients), so its stock is read either way (#233).
	hasCandidates := false
	candidateNames := map[string]bool{}
	for _, pawn := range observation.Pawns {
		if candidates, known := pawn.Candidates.Value(); known && len(candidates) > 0 {
			hasCandidates = true
			for _, c := range candidates {
				candidateNames[string(c.Definition)] = true
			}
		}
	}
	benchesFact := domain.Unknown[[]policy.GearBench]()
	tokens := map[string]string{}
	var stock []policy.Stock
	if hasCandidates {
		names := make([]string, 0, len(candidateNames))
		for name := range candidateNames {
			names = append(names, name)
		}
		sort.Strings(names)
		if stock, _, err = r.native.ReadSupplyStock(call, identity, names); err != nil {
			return RoutineGearResult{}, err
		}
	} else {
		census, _, err := r.native.ReadGearBenches(call, identity)
		if err != nil {
			return RoutineGearResult{}, err
		}
		if len(census) > 256 {
			return RoutineGearResult{}, ErrControl
		}
		benches := make([]policy.GearBench, 0, len(census))
		for _, row := range census {
			benches = append(benches, row.Bench)
			tokens[row.Bench.ID] = row.Token
		}
		names := recipeIngredientNames(census, "")
		if len(names) > 0 {
			stock, _, err = r.native.ReadSupplyStock(call, identity, names)
			if err != nil {
				return RoutineGearResult{}, err
			}
		}
		benchesFact = domain.Known(benches)
	}
	var weaponDemand []policy.Amount
	if benches, known := benchesFact.Value(); known {
		weaponDemand, err = r.weaponDemand(call, state, gear, benches)
		if err != nil {
			return RoutineGearResult{}, err
		}
	}
	choice, err := policy.SelectGearMethod(policy.GearPlanningRequest{Observation: domain.Known(observation), Seen: seen, Benches: benchesFact, Stock: stock, Rules: r.reviewer.rules, WeaponDemand: weaponDemand})
	if err != nil {
		return RoutineGearResult{}, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, choice.ID)))
	id := domain.PlanID(fmt.Sprintf("routine-gear-%x", digest[:16]))
	var action domain.Action
	switch choice.Kind {
	case policy.GearReplace:
		definition, ok := gearCandidateDefinition(observation, choice.Pawn, choice.Target)
		if !ok {
			return RoutineGearResult{}, ErrControl
		}
		if !arbiter.tryClaim([]domain.PawnID{domain.PawnID(choice.Pawn)}) {
			return RoutineGearResult{Reason: BuildingMethodUsed}, nil
		}
		replace, err := domain.NewGearReplace(domain.PawnID(choice.Pawn), choice.Target, definition)
		if err != nil {
			return RoutineGearResult{}, err
		}
		if action, err = domain.NewGearReplaceAction(domain.ActionID(fmt.Sprintf("%s-0", id)), replace); err != nil {
			return RoutineGearResult{}, err
		}
	case policy.GearProduce:
		token, ok := tokens[choice.Bench]
		if !ok {
			return RoutineGearResult{}, ErrControl
		}
		// A finite batch covers the colony gap, using only funded ingredients.
		ingredients := make([]string, len(choice.Filter))
		for i, resource := range choice.Filter {
			ingredients[i] = string(resource)
		}
		bill, err := domain.NewProductionBill(choice.Bench, choice.Recipe, token, domain.GearBatch, choice.Count, ingredients...)
		if err != nil {
			return RoutineGearResult{}, err
		}
		preview, _, err := r.native.PreviewBill(call, boundary.Identity(state.Snapshot), bill)
		if err != nil {
			return RoutineGearResult{}, err
		}
		evaluated := preview.GetEvaluated()
		if evaluated == nil || !evaluated.GetAccepted() {
			return RoutineGearResult{Reason: BuildingMethodRefused}, nil
		}
		if _, err = boundary.Context(evaluated.Context, state.Snapshot); err != nil {
			return RoutineGearResult{}, ErrControl
		}
		if action, err = domain.NewProductionBillAction(domain.ActionID(fmt.Sprintf("%s-0", id)), bill); err != nil {
			return RoutineGearResult{}, err
		}
	default:
		if len(busy) > 0 {
			return RoutineGearResult{Reason: BuildingMethodExistingWork}, nil
		}
		return RoutineGearResult{Reason: BuildingMethodUsed}, nil
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineGearResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineGearResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineGearResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, choice.ID, plan); err != nil {
		return RoutineGearResult{}, err
	}
	return RoutineGearResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
