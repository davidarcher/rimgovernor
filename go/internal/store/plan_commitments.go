package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"sort"
	"strconv"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// CommitmentRelease names why a plan's quantity is not held against the
// step's claims: the admitted segment has not reached it yet, or it has
// waited past its horizon.
type CommitmentRelease string

const (
	// CommitmentHeld: the action is in the plan's next admitted work
	// segment (every prerequisite completed in the current world, or the
	// order already dispatched) and within its horizon.
	CommitmentHeld CommitmentRelease = "held"
	// CommitmentBlocked: the action waits on a prerequisite of its own
	// plan; its cost is the plan's remainder, demand rather than a hold.
	CommitmentBlocked CommitmentRelease = "dependency"
	// CommitmentExpired: the action sat in the segment past the horizon
	// without dispatching; its cost is released to the step's claims and
	// the admission path beneath the next dispatch checks stock again. A
	// dispatched order never expires: it settles natively.
	CommitmentExpired CommitmentRelease = "expired"
)

// PlanCommitment is one quantity an admitted plan's action accounts for in
// the current world (#628): the admission's cost, its owner plan and goal,
// the tick the evidence dates from, when the hold lapses and whether it is
// held now. It is derived from the plan's admission and progress rows each
// step, never stored beside them.
type PlanCommitment struct {
	Plan     domain.PlanID
	Goal     domain.GoalID
	Revision uint64
	// Urgency is the owner goal's priority class (domain.Goal.Priority):
	// lower is more urgent.
	Urgency int
	Action  domain.ActionID
	Amount  policy.Amount
	Since   domain.Tick
	Expires domain.Tick
	Release CommitmentRelease
	// Preemptible: no attempt of the owner plan has been dispatched, so the
	// plan retires through the ordinary undispatched-method cancellation
	// (PreemptGoalMethod) and its goal is re-evaluated at the next review.
	Preemptible bool
}

// PlanCommitments is the ActivePlanCommitments view at Tick: what admitted
// plans hold against the step's claims (Committed) and what they will need
// beyond their next segment or past their horizon (Demand). Software
// commitments never imply native exclusivity: dispatch revalidates every
// order against the live game.
type PlanCommitments struct {
	Tick      domain.Tick
	Committed []PlanCommitment
	Demand    []PlanCommitment
}

// CommittedTotals sums the held quantities by resource.
func (c PlanCommitments) CommittedTotals() map[policy.Resource]int64 {
	totals := map[policy.Resource]int64{}
	for _, v := range c.Committed {
		totals[v.Amount.Resource] += v.Amount.Count
	}
	return totals
}

// DemandTotals sums the unheld remainder by resource.
func (c PlanCommitments) DemandTotals() map[policy.Resource]int64 {
	totals := map[policy.Resource]int64{}
	for _, v := range c.Demand {
		totals[v.Amount.Resource] += v.Amount.Count
	}
	return totals
}

// LoadPlanCommitments derives the commitments view from the active catalog:
// every unretired plan bound to a goal, its admissions and progress, read in
// one transaction. horizon is how long a held quantity outlives the tick of
// its latest evidence (admission or dispatch) before it expires. Plans
// admitted under another world (load, map or colony) hold nothing here.
func (s *Store) LoadPlanCommitments(ctx context.Context, current domain.GenerationSnapshot, tick, horizon domain.Tick) (PlanCommitments, error) {
	if err := current.Validate(); err != nil {
		return PlanCommitments{}, err
	}
	if tick < 0 || horizon <= 0 {
		return PlanCommitments{}, errors.New("invalid commitment scope")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return PlanCommitments{}, err
	}
	defer tx.Rollback()
	plans, err := loadPlans(ctx, tx, 256)
	if err != nil {
		return PlanCommitments{}, err
	}
	out := PlanCommitments{Tick: tick}
	for _, p := range plans {
		var goalID domain.GoalID
		err = tx.QueryRowContext(ctx, "SELECT goal_id FROM goal_methods WHERE plan_id=? LIMIT 1", p.Spec.ID()).Scan(&goalID)
		if errors.Is(err, sql.ErrNoRows) {
			continue
		}
		if err != nil {
			return PlanCommitments{}, err
		}
		goal, err := loadGoal(ctx, tx, goalID)
		if err != nil {
			return PlanCommitments{}, err
		}
		committed, demand := derivePlanCommitments(p, goal, current, tick, horizon)
		out.Committed = append(out.Committed, committed...)
		out.Demand = append(out.Demand, demand...)
	}
	if err = tx.Commit(); err != nil {
		return PlanCommitments{}, err
	}
	return out, nil
}

// derivePlanCommitments splits one plan's open admitted costs into what it
// holds now and what it will need later. Settled work (completed,
// unsuccessful, resolved cancellation) accounts for nothing: consumption
// and cancellation release a commitment by the same progress rows that
// record them. A cost admitted in another world is dropped: a generation
// change releases it. An open action whose prerequisites have completed
// (or that is already dispatched) is the next segment and holds its cost
// until Expires; one still waiting on a prerequisite is demand.
func derivePlanCommitments(p PlanState, goal GoalState, current domain.GenerationSnapshot, tick, horizon domain.Tick) (committed, demand []PlanCommitment) {
	if p.Retired || goal.Retired || goal.Goal.Status != domain.GoalActive {
		return nil, nil
	}
	admissions := map[domain.ActionID]Admission{}
	for _, a := range p.Admissions {
		admissions[a.Action] = a.Admission
	}
	scope := current
	scope.Plan, scope.Revision = p.Spec.ID(), p.Spec.Revision()
	preemptible := true
	for _, progress := range p.Progress {
		v := progress.View()
		if v.Attempt != 0 || v.Stage != domain.Pending && v.Stage != domain.Prepared && v.Stage != domain.Cancelled {
			preemptible = false
		}
	}
	for _, progress := range p.Progress {
		v := progress.View()
		switch v.Stage {
		case domain.Pending, domain.Prepared, domain.Dispatched, domain.AwaitingObservation:
		default:
			continue
		}
		admission, exists := admissions[v.Action]
		if !exists || !admission.Snapshot.SameWorld(current) {
			continue
		}
		since := admission.Tick
		if v.Tick > since && v.Snapshot.SameWorld(current) {
			since = v.Tick
		}
		undispatched := (v.Stage == domain.Pending || v.Stage == domain.Prepared) && !v.Unresolved
		blocked := undispatched && p.Spec.CheckDependencies(v.Action, p.Progress, scope, tick) != nil
		for _, cost := range admission.Costs {
			if cost.Count <= 0 {
				continue
			}
			c := PlanCommitment{Plan: p.Spec.ID(), Goal: goal.Goal.ID, Revision: goal.Revision, Urgency: goal.Goal.Priority, Action: v.Action,
				Amount: policy.Amount{Resource: policy.Resource(cost.Definition), Count: cost.Count}, Since: since, Expires: since + horizon, Release: CommitmentHeld, Preemptible: preemptible}
			switch {
			case blocked:
				c.Release = CommitmentBlocked
				demand = append(demand, c)
			case undispatched && tick > c.Expires:
				c.Release = CommitmentExpired
				demand = append(demand, c)
			default:
				committed = append(committed, c)
			}
		}
	}
	sort.SliceStable(committed, func(i, j int) bool { return committed[i].Action < committed[j].Action })
	sort.SliceStable(demand, func(i, j int) bool { return demand[i].Action < demand[j].Action })
	return committed, demand
}

// PreemptGoalMethod retires one undispatched method of an active goal so a
// more urgent proposal can claim what it held (#628): every action of the
// plan is cancelled through the ordinary progress journal, exactly as a
// recovered goal's undispatched methods are, and the goal stays active for
// its planner to re-evaluate at the next review. The goal's revision is the
// CAS the commitments view was derived at. A plan with any dispatched
// attempt is refused with ErrConflict: dispatched work settles natively and
// is never retired from software.
func (s *Store) PreemptGoalMethod(ctx context.Context, goal domain.GoalID, revision uint64, plan domain.PlanID) (GoalState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return GoalState{}, err
	}
	defer tx.Rollback()
	state, err := loadGoal(ctx, tx, goal)
	if err != nil {
		return GoalState{}, err
	}
	if state.Revision != revision {
		return GoalState{}, fmt.Errorf("%w: goal %s revision %d, preemption scoped to %d", ErrConflict, goal, state.Revision, revision)
	}
	if state.Retired || state.Goal.Status != domain.GoalActive {
		return GoalState{}, fmt.Errorf("%w: goal %s is not active", ErrConflict, goal)
	}
	bound := false
	for _, m := range state.Methods {
		bound = bound || m.Plan == plan
	}
	if !bound {
		return GoalState{}, fmt.Errorf("%w: plan %s is not an active method of goal %s", ErrNotFound, plan, goal)
	}
	p, err := load(ctx, tx, plan)
	if err != nil {
		return GoalState{}, err
	}
	if p.Retired || !domain.GoalWorkOpen(p.Progress) {
		return GoalState{}, fmt.Errorf("%w: plan %s has no open work", ErrConflict, plan)
	}
	for _, progress := range p.Progress {
		v := progress.View()
		if v.Attempt != 0 || v.Stage != domain.Pending && v.Stage != domain.Prepared && v.Stage != domain.Cancelled {
			return GoalState{}, fmt.Errorf("%w: plan %s has dispatched work", ErrConflict, plan)
		}
	}
	for _, progress := range p.Progress {
		v := progress.View()
		if v.Stage == domain.Cancelled {
			continue
		}
		if _, err = advanceInTransaction(ctx, tx, plan, v.Action, transition{Kind: "cancel"}); err != nil {
			return GoalState{}, err
		}
	}
	// The retirement is a goal event: bump the revision so a method commit
	// scoped to the old one (a planner mid-wave holding this goal) conflicts
	// instead of binding beside the retired plan.
	if state.Revision == ^uint64(0) {
		return GoalState{}, ErrCapacity
	}
	state.Revision++
	if _, err = tx.ExecContext(ctx, "UPDATE goals SET revision=? WHERE id=?", strconv.FormatUint(state.Revision, 10), goal); err != nil {
		return GoalState{}, err
	}
	if err = tx.Commit(); err != nil {
		return GoalState{}, err
	}
	return state, nil
}
