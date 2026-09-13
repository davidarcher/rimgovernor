package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
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
	return r.step(call, epoch)
}

// gearObservationFacts decodes an already-validated gear census the same way
// observation.colonyGear does for routine review. It is duplicated here
// (rather than exported from the observation package) because the routine
// planner reads a fresh census of its own immediately before proposing a
// method, the same way RoutineEquipPlanner rereads combat pawns and loose
// weapons rather than reusing the review's cached facts.
func gearObservationFacts(gear *o.GearSnapshot) policy.GearObservation {
	result := policy.GearObservation{Pawns: []policy.GearPawn{}}
	for _, p := range gear.GetPawns() {
		row := policy.GearPawn{Pawn: policy.PawnID(p.GetPawn().GetId()), Loadout: p.GetSnapshot().GetToken(), Blocked: p.Blocker != nil, Deficit: optionalBool(p.Deficit)}
		candidates := []policy.GearCandidate{}
		for _, candidate := range p.GetCandidates() {
			candidates = append(candidates, policy.GearCandidate{Target: candidate.GetItem().GetThing().GetId(), Gain: candidate.GetGain(), Definition: policy.Resource(candidate.GetItem().GetThing().GetDefName())})
		}
		needs := []policy.GearReplacement{}
		for _, need := range p.GetReplacementNeeds() {
			needs = append(needs, policy.GearReplacement{Definition: policy.Resource(need.GetDefName()), Stuff: policy.Resource(need.GetStuff()), Reason: need.GetReason()})
		}
		row.Candidates = domain.Known(candidates)
		row.Replacements = domain.Known(needs)
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

func (r *RoutineGearPlanner) step(call, epoch context.Context) (RoutineGearResult, error) {
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
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineGearResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineGearResult{Reason: BuildingMethodExistingWork}, nil
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
	seen := make([]domain.MethodID, 0, len(goal.Methods))
	for _, method := range goal.Methods {
		seen = append(seen, method.Method)
	}
	// SelectGearMethod always prefers wearing an already-observed replacement
	// candidate over crafting a new one and never reaches bench/recipe
	// selection while any candidate is pending, so the bench/stock census
	// (extra native round trips) is only worth gathering once none exist.
	hasCandidates := false
	for _, pawn := range observation.Pawns {
		if candidates, known := pawn.Candidates.Value(); known && len(candidates) > 0 {
			hasCandidates = true
		}
	}
	benchesFact := domain.Unknown[[]policy.GearBench]()
	tokens := map[string]string{}
	var stock []policy.Stock
	if !hasCandidates {
		census, _, err := r.native.ReadGearBenches(call, identity)
		if err != nil {
			return RoutineGearResult{}, err
		}
		if len(census) > 256 {
			return RoutineGearResult{}, ErrControl
		}
		benches := make([]policy.GearBench, 0, len(census))
		ingredients := map[string]bool{}
		for _, row := range census {
			benches = append(benches, row.Bench)
			tokens[row.Bench.ID] = row.Token
			if recipes, known := row.Bench.Recipes.Value(); known {
				for _, recipe := range recipes {
					if slots, known := recipe.Ingredients.Value(); known {
						for _, slot := range slots {
							for _, alt := range slot {
								ingredients[string(alt.Resource)] = true
							}
						}
					}
				}
			}
		}
		names := make([]string, 0, len(ingredients))
		for name := range ingredients {
			names = append(names, name)
		}
		sort.Strings(names)
		if len(names) > 0 {
			stock, _, err = r.native.ReadSupplyStock(call, identity, names)
			if err != nil {
				return RoutineGearResult{}, err
			}
		}
		benchesFact = domain.Known(benches)
	}
	choice, err := policy.SelectGearMethod(policy.GearPlanningRequest{Observation: domain.Known(observation), Seen: seen, Benches: benchesFact, Stock: stock})
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
		// Target 1: pause-when-satisfied maintains a standing buffer of the
		// needed replacement rather than crafting a single unit once. Native
		// ingredient-filter/material-preference selection is left at its
		// default (no FilterPatch override) — SelectGearMethod's Filter
		// output goes unused here, an open, disclosed narrowing.
		bill, err := domain.NewProductionBill(choice.Bench, choice.Recipe, token, domain.StockTarget, 1)
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
