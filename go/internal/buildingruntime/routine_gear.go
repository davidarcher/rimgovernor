package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoutineGearSource reads the same generic colony census (with planning
// facts requested) that carries the already-validated gear census
// (bridge.ReadColonyFacts runs validateColonyGear on it internally); no
// dedicated gear read is needed to plan a method, only to refresh CAS tokens
// immediately before dispatch (see bridge.ReadGearReplacement, used by
// GearReplaceBoundary).
type RoutineGearSource interface {
	ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*o.ColonyFactsReply, bridge.Result, error)
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
	// Benches deliberately unknown: SelectGearMethod checks existing-gear
	// replace candidates first and never reaches bench/recipe selection while
	// any are pending, so this proposes only the wear-existing-item half of
	// MaintainEquipment (GearReplace); the workshop-bill half (GearProduce)
	// safely stays inert (GearUnknown) until resource-stock funding and the
	// IngredientRequirement per-alternative wire shape are wired (open item).
	choice, err := policy.SelectGearMethod(policy.GearPlanningRequest{Observation: domain.Known(observation), Seen: seen, Benches: domain.Unknown[[]policy.GearBench]()})
	if err != nil {
		return RoutineGearResult{}, err
	}
	if choice.Kind != policy.GearReplace {
		return RoutineGearResult{Reason: BuildingMethodUsed}, nil
	}
	definition, ok := gearCandidateDefinition(observation, choice.Pawn, choice.Target)
	if !ok {
		return RoutineGearResult{}, ErrControl
	}
	replace, err := domain.NewGearReplace(domain.PawnID(choice.Pawn), choice.Target, definition)
	if err != nil {
		return RoutineGearResult{}, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, choice.ID)))
	id := domain.PlanID(fmt.Sprintf("routine-gear-%x", digest[:16]))
	action, err := domain.NewGearReplaceAction(domain.ActionID(fmt.Sprintf("%s-0", id)), replace)
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
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, choice.ID, plan); err != nil {
		return RoutineGearResult{}, err
	}
	return RoutineGearResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
