package store

import (
	"context"
	"crypto/sha256"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// routinePlan is one current-world, non-retired plan and the need (or
// player project) it serves.
type routinePlan struct {
	state    PlanState
	goal     domain.GoalID
	source   domain.GoalSource
	priority int
}

// routinePlans maps every non-retired plan of the current world to its
// goal: routine plans to their bound need, player submissions to a
// per-plan project id. Plans of another world, and plans with neither a
// goal method nor a submission, are skipped.
func routinePlans(ctx context.Context, tx *sql.Tx, current domain.GenerationSnapshot, bindings []RoutineGoal) ([]routinePlan, error) {
	plans, err := loadPlans(ctx, tx, 256)
	if err != nil {
		return nil, err
	}
	var result []routinePlan
	for _, plan := range plans {
		var goalID domain.GoalID
		source, priority := domain.PlayerGoal, 3
		world := World{}
		admitted := 0
		err = tx.QueryRowContext(ctx, "SELECT goal_id,priority FROM goal_methods WHERE plan_id=?", plan.Spec.ID()).Scan(&goalID, &admitted)
		if err == nil {
			g, e := loadGoal(ctx, tx, goalID)
			if e != nil {
				return nil, e
			}
			// The priority the method was admitted under, not the goal's
			// current one: work a priority-2 goal admitted outside the
			// ranked queue never turns into a slot hold when the goal
			// drops back to 3 (#705), and slot work stays one.
			source, priority = g.Goal.Source, admitted
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
		result = append(result, routinePlan{plan, goalID, source, priority})
	}
	return result, nil
}

func routineCommitments(ctx context.Context, tx *sql.Tx, current domain.GenerationSnapshot, bindings []RoutineGoal) ([]policy.Commitment, error) {
	plans, err := routinePlans(ctx, tx, current, bindings)
	if err != nil {
		return nil, err
	}
	return commitmentsOf(ctx, tx, plans)
}

func commitmentsOf(ctx context.Context, tx *sql.Tx, plans []routinePlan) ([]policy.Commitment, error) {
	var result []policy.Commitment
	for _, plan := range plans {
		var open domain.Progress
		for _, p := range plan.state.Progress {
			if domain.GoalWorkOpen([]domain.Progress{p}) {
				open = p
				break
			}
		}
		if open.View().Stage == "" || developmentExemptMethod(plan.state.Spec) {
			continue
		}
		labor := policy.GoalLabor(plan.goal)
		if plan.source == domain.PlayerGoal && labor == nil {
			labor = policy.LaborProfile{policy.WorkConstruction}
		}
		dispatched, err := dispatchTick(ctx, tx, open.View().Action)
		if err != nil {
			return nil, err
		}
		targets := domain.Unknown[policy.WorkTargets]()
		for _, a := range plan.state.Spec.Actions() {
			if a.ID() == open.View().Action {
				targets = policy.ActionWorkTargets(a)
			}
		}
		result = append(result, policy.Commitment{Goal: plan.goal, Source: plan.source, Priority: plan.priority, Progress: open, Labor: labor, Dispatched: dispatched, Targets: targets})
	}
	return result, nil
}

// readyWorkOf is the shadow ready-work projection (#645) of the review's
// plans: recorded beside the development rows, read by no admission.
// Stage inputs (bill ingredients, crop readiness) are not observed here
// yet, so staged work reads awaiting_observation rather than ready.
func readyWorkOf(r RoutineReviewRequest, plans []routinePlan, goals []policy.DevelopmentGoal) policy.ReadyWorkReport {
	var ready []policy.ReadyPlan
	for _, p := range plans {
		ready = append(ready, policy.ReadyPlan{Goal: p.goal, Spec: p.state.Spec, Progress: p.state.Progress})
	}
	var unserved []domain.GoalID
	for _, g := range goals {
		if !g.Served && !g.Blocked && g.Labor != nil {
			unserved = append(unserved, g.ID)
		}
	}
	return policy.ProjectReadyWork(policy.ReadyRequest{Snapshot: r.Current, Tick: r.Tick, Plans: ready, Unserved: unserved})
}

// DispatchTick is the tick of the action's latest dispatch transition,
// unknown when the action was never dispatched.
func (s *Store) DispatchTick(ctx context.Context, action domain.ActionID) (domain.Fact[domain.Tick], error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return domain.Unknown[domain.Tick](), err
	}
	defer tx.Rollback()
	return dispatchTick(ctx, tx, action)
}

// dispatchTick is the tick of the action's latest dispatch transition.
func dispatchTick(ctx context.Context, tx *sql.Tx, action domain.ActionID) (domain.Fact[domain.Tick], error) {
	rows, err := tx.QueryContext(ctx, "SELECT payload FROM transitions WHERE action_id=? ORDER BY sequence", action)
	if err != nil {
		return domain.Unknown[domain.Tick](), err
	}
	defer rows.Close()
	result := domain.Unknown[domain.Tick]()
	for rows.Next() {
		var data []byte
		if err := rows.Scan(&data); err != nil {
			return domain.Unknown[domain.Tick](), err
		}
		var event transition
		if err := decode(data, &event); err != nil {
			return domain.Unknown[domain.Tick](), err
		}
		if event.Kind == "dispatch" {
			result = domain.Known(event.Tick)
		}
	}
	return result, rows.Err()
}

func rankRoutineDevelopment(ctx context.Context, tx *sql.Tx, r RoutineReviewRequest, needs policy.RoutineNeeds, states []GoalState, previous policy.DevelopmentState, withheld policy.LaborProfile, stage policy.ColonyStageRecord, limit int, records []DependencyRecord) (policy.DevelopmentState, policy.ReadyWorkReport, []DependencyRecord, error) {
	var bindings []RoutineGoal
	for i, n := range needs.Assessments {
		bindings = append(bindings, RoutineGoal{Need: n.ID, Goal: states[i].Goal.ID})
	}
	plans, err := routinePlans(ctx, tx, r.Current, bindings)
	if err != nil {
		return policy.DevelopmentState{}, policy.ReadyWorkReport{}, nil, err
	}
	commitments, err := commitmentsOf(ctx, tx, plans)
	if err != nil {
		return policy.DevelopmentState{}, policy.ReadyWorkReport{}, nil, err
	}
	kept, dependencies, err := routineDependencies(ctx, tx, records, bindings, states, r.Facts, r.Tick)
	if err != nil {
		return policy.DevelopmentState{}, policy.ReadyWorkReport{}, nil, err
	}
	goals := append([]policy.DevelopmentGoal(nil), needs.Goals...)
	for i := range goals {
		for j, b := range bindings {
			if b.Need == goals[i].ID {
				g := states[j].Goal
				goals[i].Cancelled = g.Status == domain.GoalCancelled
				goals[i].Blocked = g.Status != domain.GoalActive
				// Served counts retired methods too: a startup goal whose
				// campfire plan completed and retired is served, not owed.
				var served int
				if err := tx.QueryRowContext(ctx, "SELECT count(*) FROM goal_methods WHERE goal_id=? AND epoch=?", g.ID, strconv.FormatUint(g.Epoch, 10)).Scan(&served); err != nil {
					return policy.DevelopmentState{}, policy.ReadyWorkReport{}, nil, err
				}
				goals[i].Served = served > 0
			}
		}
	}
	state, err := policy.RankDevelopment(policy.DevelopmentRequest{Snapshot: r.Current, Tick: r.Tick, Workers: r.Facts.Workers, Labor: r.Facts.Labor, LaborUse: r.Facts.LaborUse, Limit: limit, Stage: stage, Goals: goals, Commitments: commitments, Previous: previous, Partial: r.PartialPlanners, Withheld: withheld, Auto: r.Policy.AutoDevelopment, Census: r.Facts.WorkerCensus, Dependencies: dependencies})
	if err != nil {
		return policy.DevelopmentState{}, policy.ReadyWorkReport{}, nil, err
	}
	return state, readyWorkOf(r, plans, goals), kept, nil
}

// developmentExemptMethod reports a method that is no development project:
// every action is a QuestAccept or a joiner-letter answer (one native write
// with no pawn work behind it; the quest's own parts walk the joiner in, so
// MaintainPopulation answers it without a slot, the way ProductionPolicy's
// configuration pushes are exempt by need), or an Equip of a weapon the
// colony already owns (one pawn walks to a loose bow and picks it up, a
// minute of forced work that builds nothing; #411: EnsureBasicDefense
// waited three days behind a wood haul and a herbal bill for a slot while
// five bows lay on the ground), or a husbandry settings write (a
// designation cancel, an allowed area, a master or a follow flag: one
// native write, no handler work; #577: takeover/herd-removal parked on
// no_work while MaintainHerd's cancel of a Manual slaughter flag waited
// for a slot at maintenance priority), or a zone deletion (one native
// write dissolving a zone the tidy already re-sited, #611), or an apparel
// policy write (one native settings write per pawn, no pawn work; #660:
// eight colonists waited one slot per review round for their policies and
// MaintainEquipment spent a whole window before its first wear order). An exempt
// method is admitted without a slot and, while open, holds none
// (routineCommitments).
func developmentExemptMethod(plan domain.PlanSpec) bool {
	actions := plan.Actions()
	if len(actions) == 0 {
		return false
	}
	for _, action := range actions {
		letter, isDialog := action.DialogAnswer()
		husbandry, isHusbandry := action.Husbandry()
		if action.Kind() != domain.QuestAcceptAction && action.Kind() != domain.EquipAction && action.Kind() != domain.ZoneDeleteAction && action.Kind() != domain.ApparelPolicyAction && !(isDialog && letter.LetterToken() != "") && !(isHusbandry && husbandrySettingsWrite(husbandry.Method())) {
			return false
		}
	}
	return true
}

// husbandrySettingsWrite names the husbandry methods that change a flag on
// the animal and nothing else; tame, slaughter and release put a
// handler to work. Train is the Animals tab tick box (SetWantedRecursive):
// MaintainHerd never holds a development slot, so a slot-gated train was
// refused forever (#697).
func husbandrySettingsWrite(method domain.HusbandryMethod) bool {
	switch method {
	case domain.HusbandryTrain, domain.HusbandryCancelSlaughter, domain.HusbandryCancelRelease, domain.HusbandryAllowedArea, domain.HusbandryMaster, domain.HusbandryFollowDrafted, domain.HusbandryFollowFieldwork:
		return true
	}
	return false
}

// Recheck current commitments inside method admission: a player project accepted
// since the review may already have consumed its last optional slot. A
// method that is a pure settings write (developmentExemptMethod) is
// admitted without a slot, like a DevelopmentExempt need.
func admitRoutineDevelopment(ctx context.Context, tx *sql.Tx, g domain.Goal, plan domain.PlanSpec) error {
	if g.Source != domain.AutopilotGoal || g.Priority < 3 || !strings.HasPrefix(string(g.ID), "routine-") || developmentExemptMethod(plan) {
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
	// The ranking's own accounting (policy.AdmitDevelopment) on the
	// commitments as they stand now: a player project or another admission
	// since the review holds its slot and worker. The same commitments the
	// ranking freed hold nothing here either: a stalled one, and one whose
	// labor idled (its row reads labor_idle).
	commitments, err := routineCommitments(ctx, tx, review.Snapshot, review.Goals)
	if err != nil {
		return err
	}
	state := review.Development.State()
	released := map[domain.GoalID]bool{}
	for _, row := range state.Rows {
		if row.Reason == policy.DevelopmentLaborIdle {
			released[row.Goal] = true
		}
	}
	var withheld policy.LaborProfile
	for _, h := range state.Holds {
		if h.Goal == "" {
			withheld = append(withheld, h.Labor...)
		}
	}
	if err = policy.AdmitDevelopment(state, need, policy.CommitmentHolds(commitments, review.Tick, released, state.Auto, withheld)); err != nil {
		return fmt.Errorf("%w: %v", ErrNotAdmitted, err)
	}
	return nil
}

// YieldRoutineDevelopment records that the planner of need has no method
// under review revision (its retries are exhausted or every fallback
// refused) and hands the need's development slot to the next
// capacity-deferred goal of the same review (policy.YieldDevelopment). The
// row then reads method_unavailable instead of an unexplained selection, a
// planner queued later in the same wave sees the recipient selected, and
// the next review ranks the yielder behind the goals it yielded to. A
// review that has moved past revision is ErrConflict; a need that is not
// selected is a no-op. It grants no execution authority: method admission
// still rechecks the recipient's selection.
func (s *Store) YieldRoutineDevelopment(ctx context.Context, revision uint64, need domain.GoalID) (RoutineReview, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return RoutineReview{}, err
	}
	defer tx.Rollback()
	review, err := loadRoutine(ctx, tx)
	if err != nil {
		return RoutineReview{}, err
	}
	if review.Revision != revision {
		return RoutineReview{}, fmt.Errorf("%w: routine review revision %d, yield under %d", ErrConflict, review.Revision, revision)
	}
	if !review.Enabled {
		return review, tx.Commit()
	}
	state := policy.YieldDevelopment(review.Development.State(), need)
	if err = policy.ValidateDevelopmentState(state); err != nil {
		return RoutineReview{}, err
	}
	review.Development = developmentRecord(state)
	data, err := json.Marshal(review)
	if err != nil {
		return RoutineReview{}, err
	}
	if len(data) > 512*1024 {
		return RoutineReview{}, ErrCapacity
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO routine_review(singleton,payload) VALUES(1,?) ON CONFLICT(singleton) DO UPDATE SET payload=excluded.payload", data); err != nil {
		return RoutineReview{}, err
	}
	return review, tx.Commit()
}
