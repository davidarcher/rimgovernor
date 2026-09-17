package store

import (
	"context"
	"crypto/sha256"
	"database/sql"
	"errors"
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func routineCommitments(ctx context.Context, tx *sql.Tx, current domain.GenerationSnapshot, bindings []RoutineGoal) ([]policy.Commitment, error) {
	plans, err := loadPlans(ctx, tx, 256)
	if err != nil {
		return nil, err
	}
	var result []policy.Commitment
	for _, plan := range plans {
		var open domain.Progress
		for _, p := range plan.Progress {
			if domain.GoalWorkOpen([]domain.Progress{p}) {
				open = p
				break
			}
		}
		if open.View().Stage == "" {
			continue
		}
		var goalID domain.GoalID
		source, priority := domain.PlayerGoal, 3
		world := World{}
		err = tx.QueryRowContext(ctx, "SELECT goal_id FROM goal_methods WHERE plan_id=?", plan.Spec.ID()).Scan(&goalID)
		if err == nil {
			g, e := loadGoal(ctx, tx, goalID)
			if e != nil {
				return nil, e
			}
			source, priority = g.Goal.Source, g.Goal.Priority
			world = World{Colony: g.Goal.Snapshot.Colony, Load: g.Goal.Snapshot.Load, Map: g.Goal.Snapshot.Map}
			for _, b := range bindings {
				if goalID == b.Goal || source == domain.AutopilotGoal && strings.HasPrefix(string(goalID), "routine-") && strings.HasSuffix(string(goalID), "-"+string(b.Need)) {
					goalID = b.Need
					break
				}
			}
		} else if errors.Is(err, sql.ErrNoRows) {
			err = tx.QueryRowContext(ctx, "SELECT colony,load_token,map_id FROM submissions WHERE plan_id=?", plan.Spec.ID()).Scan(&world.Colony, &world.Load, &world.Map)
			if errors.Is(err, sql.ErrNoRows) {
				continue
			}
			if err != nil {
				return nil, err
			}
			goalID = domain.GoalID(fmt.Sprintf("player-project-%x", sha256.Sum256([]byte(plan.Spec.ID()))))
		} else {
			return nil, err
		}
		if world != (World{Colony: current.Colony, Load: current.Load, Map: current.Map}) {
			continue
		}
		labor := policy.GoalLabor(goalID)
		if source == domain.PlayerGoal && labor == nil {
			labor = policy.LaborProfile{policy.WorkConstruction}
		}
		result = append(result, policy.Commitment{Goal: goalID, Source: source, Priority: priority, Progress: open, Labor: labor})
	}
	return result, nil
}

func rankRoutineDevelopment(ctx context.Context, tx *sql.Tx, r RoutineReviewRequest, needs policy.RoutineNeeds, states []GoalState, previous policy.DevelopmentState) (policy.DevelopmentState, error) {
	var bindings []RoutineGoal
	for i, n := range needs.Assessments {
		bindings = append(bindings, RoutineGoal{Need: n.ID, Goal: states[i].Goal.ID})
	}
	commitments, err := routineCommitments(ctx, tx, r.Current, bindings)
	if err != nil {
		return policy.DevelopmentState{}, err
	}
	goals := append([]policy.DevelopmentGoal(nil), needs.Goals...)
	for i := range goals {
		for j, b := range bindings {
			if b.Need == goals[i].ID {
				g := states[j].Goal
				goals[i].Cancelled = g.Status == domain.GoalCancelled
				goals[i].Blocked = g.Status != domain.GoalActive
			}
		}
	}
	return policy.RankDevelopment(policy.DevelopmentRequest{Snapshot: r.Current, Tick: r.Tick, Workers: r.Facts.Workers, Labor: r.Facts.Labor, Limit: r.Policy.MaxDevelopmentProjects, Goals: goals, Commitments: commitments, Previous: previous})
}

// Recheck current commitments inside method admission: a player project accepted
// since the review may already have consumed its last optional slot.
func admitRoutineDevelopment(ctx context.Context, tx *sql.Tx, g domain.Goal) error {
	if g.Source != domain.AutopilotGoal || g.Priority < 3 || !strings.HasPrefix(string(g.ID), "routine-") {
		return nil
	}
	review, err := loadRoutine(ctx, tx)
	if err != nil {
		return err
	}
	if !review.Enabled {
		return fmt.Errorf("%w: routine review disabled", ErrNotAdmitted)
	}
	if review.Snapshot != g.Snapshot {
		return fmt.Errorf("%w: goal %s reviewed under snapshot %+v, current review is %+v", ErrNotAdmitted, g.ID, g.Snapshot, review.Snapshot)
	}
	var need domain.GoalID
	for _, b := range review.Goals {
		if b.Goal == g.ID {
			need = b.Need
			break
		}
	}
	if policy.DevelopmentExempt(need) {
		return nil
	}
	selected := false
	for _, row := range review.Development.Rows {
		if row.Goal == need {
			selected = row.Selected
		}
	}
	if !selected {
		return fmt.Errorf("%w: goal %s (%s) holds no development slot", ErrNotAdmitted, g.ID, need)
	}
	commitments, err := routineCommitments(ctx, tx, review.Snapshot, review.Goals)
	if err != nil {
		return err
	}
	ids := map[domain.GoalID]bool{}
	for _, c := range commitments {
		if c.Source != domain.AdviserGoal && (c.Source == domain.PlayerGoal || c.Priority >= 3) {
			ids[c.Goal] = true
		}
	}
	if len(ids) >= review.Development.Capacity {
		return fmt.Errorf("%w: development capacity %d already committed", ErrNotAdmitted, review.Development.Capacity)
	}
	return nil
}
