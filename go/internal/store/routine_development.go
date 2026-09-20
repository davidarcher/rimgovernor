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
		if open.View().Stage == "" || developmentExemptMethod(plan.Spec) {
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
		dispatched, err := dispatchTick(ctx, tx, open.View().Action)
		if err != nil {
			return nil, err
		}
		result = append(result, policy.Commitment{Goal: goalID, Source: source, Priority: priority, Progress: open, Labor: labor, Dispatched: dispatched})
	}
	return result, nil
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

func rankRoutineDevelopment(ctx context.Context, tx *sql.Tx, r RoutineReviewRequest, needs policy.RoutineNeeds, states []GoalState, previous policy.DevelopmentState, withheld policy.LaborProfile) (policy.DevelopmentState, error) {
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
				// Served counts retired methods too: a startup goal whose
				// campfire plan completed and retired is served, not owed.
				var served int
				if err := tx.QueryRowContext(ctx, "SELECT count(*) FROM goal_methods WHERE goal_id=? AND epoch=?", g.ID, strconv.FormatUint(g.Epoch, 10)).Scan(&served); err != nil {
					return policy.DevelopmentState{}, err
				}
				goals[i].Served = served > 0
			}
		}
	}
	return policy.RankDevelopment(policy.DevelopmentRequest{Snapshot: r.Current, Tick: r.Tick, Workers: r.Facts.Workers, Labor: r.Facts.Labor, LaborUse: r.Facts.LaborUse, Limit: r.Policy.MaxDevelopmentProjects, Goals: goals, Commitments: commitments, Previous: previous, Partial: r.PartialPlanners, Withheld: withheld})
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
// for a slot at maintenance priority). An exempt method is admitted
// without a slot and, while open, holds none (routineCommitments).
func developmentExemptMethod(plan domain.PlanSpec) bool {
	actions := plan.Actions()
	if len(actions) == 0 {
		return false
	}
	for _, action := range actions {
		letter, isDialog := action.DialogAnswer()
		husbandry, isHusbandry := action.Husbandry()
		if action.Kind() != domain.QuestAcceptAction && action.Kind() != domain.EquipAction && !(isDialog && letter.LetterToken() != "") && !(isHusbandry && husbandrySettingsWrite(husbandry.Method())) {
			return false
		}
	}
	return true
}

// husbandrySettingsWrite names the husbandry methods that change a flag on
// the animal and nothing else; tame, train, slaughter and release put a
// handler to work.
func husbandrySettingsWrite(method domain.HusbandryMethod) bool {
	switch method {
	case domain.HusbandryCancelSlaughter, domain.HusbandryCancelRelease, domain.HusbandryAllowedArea, domain.HusbandryMaster, domain.HusbandryFollowDrafted, domain.HusbandryFollowFieldwork:
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
	// The same commitments the review's ranking freed hold no slot here
	// either: a stalled one, and one whose labor idled (its row reads
	// labor_idle), or the freed slot could never be taken.
	released := map[domain.GoalID]bool{}
	for _, row := range review.Development.Rows {
		if row.Reason == policy.DevelopmentLaborIdle {
			released[row.Goal] = true
		}
	}
	ids := map[domain.GoalID]bool{}
	for _, c := range commitments {
		if c.Source != domain.AdviserGoal && (c.Source == domain.PlayerGoal || c.Priority >= 3) && !c.Stalled(review.Tick) && !released[c.Goal] {
			ids[c.Goal] = true
		}
	}
	if len(ids) >= review.Development.Capacity {
		return fmt.Errorf("%w: development capacity %d already committed", ErrNotAdmitted, review.Development.Capacity)
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
