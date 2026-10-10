package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// rulesMethodPrefix names EnsureFoodSupply's native rule attachments: one
// method per Round, its id the observed tick so the goal's history sorts by time.
const rulesMethodPrefix = "rules-"

// RoundsRulesPlanner attaches the native declarative rules the food plan needs:
// each Round it derives the rule set from the hunt plan
// (policy.HuntChainRules) and commits one rules_attach action under
// EnsureFoodSupply, so the attachment has a receipt and sits in the session
// journal before native is written. The lease (policy.RuleLeaseTicks) is
// renewed every Round; an empty set clears rules an earlier Round attached,
// and a goal that is no longer workable lets the lease lapse.
type RoundsRulesPlanner struct {
	reviewer *Rounder
}

type RoundsRulesResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsRulesPlanner(reviewer *Rounder) (*RoundsRulesPlanner, error) {
	if reviewer == nil {
		return nil, fmt.Errorf("%w: NewRoundsRulesPlanner: reviewer == nil", ErrControl)
	}
	return &RoundsRulesPlanner{reviewer}, nil
}

func (r *RoundsRulesPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsRulesResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsRulesResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsRulesResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsRulesResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsRulesResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.EnsureFoodSupply)
	if err != nil {
		return RoundsRulesResult{}, err
	}
	if !workable {
		return RoundsRulesResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsRulesResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsRulesResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	// Hunters come from the pawn frame, not colony facts alone. After a
	// census invalidation the fresh read must populate both inputs too.
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoundsRulesResult{}, err
	}
	hunters := domain.Unknown[[]policy.PawnProfile]()
	if pawns, known := read.Projection.WorkPawns.Value(); known {
		hunters = domain.Known(policy.Profiles(pawns))
	}
	set, known := policy.HuntChainRules(read.Projection.Acquisition, hunters, read.Projection.HuntHolds)
	if !known {
		return RoundsRulesResult{Verdict: fieldUnavailable("hunt_census")}, nil
	}
	return r.attach(call, epoch, state, goal, expected.Tick, set)
}

// attach commits one rules_attach method for set at tick: a renewal when the
// set stands, a clear when it is empty and an earlier Round attached rules.
func (r *RoundsRulesPlanner) attach(call, epoch context.Context, state ControlState, goal store.StandardState, tick domain.Tick, set policy.RuleSet) (RoundsRulesResult, error) {
	p := r.reviewer.player
	if len(set.Rules) == 0 {
		attached, err := r.attached(call, goal)
		if err != nil {
			return RoundsRulesResult{}, err
		}
		if !attached {
			return RoundsRulesResult{Verdict: BuildingReasonNoDeficit}, nil
		}
	}
	method := domain.MethodID(fmt.Sprintf("%s%012d", rulesMethodPrefix, int64(tick)))
	if _, err := p.journal.LoadMethod(call, goal.Standard.ID, goal.Standard.Episode, method); err == nil {
		return RoundsRulesResult{Verdict: waitFor(policy.CauseMethodUsed, "rules_method")}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoundsRulesResult{}, err
	}
	attach, err := domain.NewRulesAttach(set.Rules, policy.RuleLeaseTicks)
	if err != nil {
		return RoundsRulesResult{}, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewRulesAttachAction(domain.ActionID(fmt.Sprintf("%s-0", id)), attach)
	if err != nil {
		return RoundsRulesResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsRulesResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsRulesResult{}, err
	}
	if p.session.State() != state {
		return RoundsRulesResult{}, fmt.Errorf("%w: attach: p.session.State() != state", ErrControl)
	}
	reason := "attach native rules"
	if len(set.Rules) == 0 {
		reason = "clear native rules"
	}
	if _, err = p.journal.CommitMethodReason(call, goal.Standard.ID, goal.Revision, method, reason, plan); err != nil {
		return RoundsRulesResult{}, err
	}
	return RoundsRulesResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

// attached reports whether the goal's latest rules method attached any rule,
// so an empty set is committed only to clear what an earlier Round left.
func (r *RoundsRulesPlanner) attached(call context.Context, goal store.StandardState) (bool, error) {
	var latest domain.Method
	for _, method := range goal.History {
		if strings.HasPrefix(string(method.Method), rulesMethodPrefix) && method.Method > latest.Method {
			latest = method
		}
	}
	if latest.Method == "" {
		return false, nil
	}
	plan, err := r.reviewer.player.journal.LoadPlan(call, latest.Plan)
	if err != nil {
		return false, err
	}
	for _, progress := range plan.Progress {
		if attach, ok := progress.Action().RulesAttach(); ok && len(attach.Rules()) > 0 {
			return true, nil
		}
	}
	return false, nil
}
