package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// routineProgress records every active goal's progress (#629) from the
// evidence this review holds: the goal's open plans (a settled effect or
// observed construction since the last record is native progress; a
// dispatched order with no capable available pawn is blocked), the labor
// census, the foothold gates (the food ladder's prerequisite) and the
// deficit the review measured. Records are keyed by need, the id the
// dashboard names goals by; a goal that is recovered, cancelled or
// invalidated drops its record.
func routineProgress(ctx context.Context, tx *sql.Tx, request RoutineReviewRequest, previous RoutineReview, reset bool, needs policy.RoutineNeeds, states []GoalState) ([]policy.GoalProgress, error) {
	old := map[domain.GoalID]policy.GoalProgress{}
	if !reset {
		for _, p := range previous.Progress {
			old[p.Goal] = p
		}
	}
	deficits := map[domain.GoalID]domain.Fact[float64]{}
	for _, g := range needs.Goals {
		deficits[g.ID] = g.Deficit
	}
	var out []policy.GoalProgress
	for i, n := range needs.Assessments {
		g := states[i]
		if g.Goal.Status != domain.GoalActive && g.Goal.Status != domain.GoalSuspended {
			continue
		}
		last := old[n.ID]
		evidence := policy.ProgressEvidence{Observed: deficits[n.ID]}
		var method domain.MethodID
		labor := policy.GoalLabor(n.ID)
		for _, m := range g.Methods {
			if m.Epoch != g.Goal.Epoch {
				continue
			}
			plan, err := load(ctx, tx, m.Plan)
			if err != nil {
				return nil, err
			}
			for _, p := range plan.Progress {
				v := p.View()
				if effect, known := v.Effect.Value(); known && (effect == domain.EffectCompleted || effect == domain.EffectAbsent) && v.Tick > last.LastProgress && last.Goal == n.ID {
					evidence.Advanced = true
				}
				if observed, known := v.ConstructionObserved.Value(); known && observed > last.LastProgress && last.Goal == n.ID {
					evidence.Advanced = true
				}
				if !domain.GoalWorkOpen([]domain.Progress{p}) {
					continue
				}
				method = m.Method
				evidence.Open = true
				if v.Stage == domain.Dispatched || v.Stage == domain.AwaitingObservation || v.Unresolved {
					evidence.Dispatched = true
				}
				if receipt, known := v.Receipt.Value(); v.Stage == domain.Dispatched && v.Unresolved && (!known || receipt == domain.ReceiptUnknown) {
					evidence.Unresolved = true
				}
				if hold, ok := v.FreshHold(); ok {
					for _, reason := range hold.Reasons() {
						evidence.NativeIneligible = evidence.NativeIneligible || reason == domain.HeldNativeIneligible
					}
				}
				if labor == nil {
					labor = actionLabor(p.Action())
				}
			}
		}
		if census, known := request.Facts.Labor.Value(); known && len(labor) > 0 {
			available := false
			for _, w := range labor {
				available = available || census[w] > 0
			}
			evidence.WorkerAvailable = domain.Known(available)
		}
		contract := policy.GoalProgressContract(methodLabel(method))
		if n.ID == policy.EnsureFoodSupply {
			contract, evidence.Prerequisite, evidence.Observed = policy.FoodProgress(needs.Gates, request.Facts, request.Policy)
		}
		record := policy.ReviewGoalProgress(last, n.ID, contract, evidence, request.Tick)
		if !evidence.Advanced {
			// A deadline that passed without native progress keys the
			// failed situation out for a bounded cooldown; the planners
			// rotate the method or target (stall cancels, attempt counts).
			record, _ = policy.ExpireGoalProgress(record, contract, request.Tick, policy.CooldownKey(record.Method, string(record.Blocked)), nil)
		}
		if err := policy.ValidateGoalProgress(record, request.Tick); err != nil {
			return nil, fmt.Errorf("%s: %w", n.ID, err)
		}
		out = append(out, record)
	}
	return out, nil
}

// actionLabor is the work type an open action's order waits on when its
// goal declares no profile (a startup goal): who could take it.
func actionLabor(action domain.Action) policy.LaborProfile {
	switch action.Kind() {
	case domain.BuildingAction, domain.DeconstructionAction, domain.WallRemovalAction, domain.RepairAction:
		return policy.LaborProfile{policy.WorkConstruction}
	case domain.HaulAction:
		return policy.LaborProfile{policy.WorkHauling}
	case domain.CutPlantAction:
		return policy.LaborProfile{policy.WorkPlantCutting}
	case domain.MineAcquisitionAction, domain.ExcavationAction:
		return policy.LaborProfile{policy.WorkMining}
	case domain.CleanAction:
		return policy.LaborProfile{policy.WorkCleaning}
	case domain.AcquisitionAction:
		if huntAcquisition(action) {
			return policy.LaborProfile{policy.WorkHunting}
		}
		return policy.LaborProfile{policy.WorkPlantCutting}
	}
	return nil
}

// methodLabel is a method id without its content hash or admission salt
// ("acquire-3f9a..." reads "acquire"), so the record names the method
// family rather than one plan.
func methodLabel(id domain.MethodID) string {
	s := string(id)
	if i := strings.LastIndexByte(s, '-'); i > 0 {
		suffix := s[i+1:]
		hex := len(suffix) >= 8
		for _, c := range suffix {
			hex = hex && (c >= '0' && c <= '9' || c >= 'a' && c <= 'f')
		}
		if hex {
			s = s[:i]
		}
	}
	if len(s) > 64 {
		s = s[:64]
	}
	return s
}

// RecordProgressCooldown keys a failed situation out of need's goal for a
// bounded cooldown (policy.ProgressCooldownMax): a planner that cancelled a
// stalled order calls it with the target it gave up on so the next
// selection passes that target over until the cooldown lifts. A review
// that has moved past revision is ErrConflict; a need without a record is
// a no-op.
func (s *Store) RecordProgressCooldown(ctx context.Context, revision uint64, need domain.GoalID, key string, until domain.Tick) (RoutineReview, error) {
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
		return RoutineReview{}, fmt.Errorf("%w: routine review revision %d, cooldown under %d", ErrConflict, review.Revision, revision)
	}
	changed := false
	for i := range review.Progress {
		if review.Progress[i].Goal != need {
			continue
		}
		review.Progress[i] = policy.AddProgressCooldown(review.Progress[i], key, until, review.Tick)
		if err = policy.ValidateGoalProgress(review.Progress[i], review.Tick); err != nil {
			return RoutineReview{}, err
		}
		changed = true
	}
	if !changed {
		return review, tx.Commit()
	}
	data, err := json.Marshal(review)
	if err != nil {
		return RoutineReview{}, err
	}
	if len(data) > 1024*1024 {
		return RoutineReview{}, ErrCapacity
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO routine_review(singleton,payload) VALUES(1,?) ON CONFLICT(singleton) DO UPDATE SET payload=excluded.payload", data); err != nil {
		return RoutineReview{}, err
	}
	return review, tx.Commit()
}

// GoalProgress is need's progress record in the review, if it has one.
func (r RoutineReview) GoalProgress(need domain.GoalID) (policy.GoalProgress, bool) {
	for _, p := range r.Progress {
		if p.Goal == need {
			return p, true
		}
	}
	return policy.GoalProgress{}, false
}
