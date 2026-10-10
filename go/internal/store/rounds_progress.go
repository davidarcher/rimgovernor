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

// roundsProgress records every active goal's progress from the
// evidence this review holds: the goal's open plans (a settled effect or
// observed construction since the last record is native progress; a
// dispatched order with no capable available pawn is blocked), the labor
// census, the foothold gates (the food ladder's prerequisite) and the
// deficit the review measured. Records are keyed by need, the id the
// launcher names concerns by; a concern that is recovered, cancelled or
// invalidated drops its record.
func roundsProgress(ctx context.Context, tx *sql.Tx, request RoundsRequest, previous Rounds, reset bool, needs policy.RoundsFindings, states []WorkOwner) ([]policy.ConcernProgress, error) {
	old := map[domain.ConcernID]policy.ConcernProgress{}
	if !reset {
		for _, p := range previous.Progress {
			old[p.Concern] = p
		}
	}
	deficits := map[domain.ConcernID]domain.Fact[float64]{}
	for _, g := range needs.Concerns {
		deficits[g.ID] = g.Deficit
	}
	storageOpen := false
	for i, n := range needs.Assessments {
		if n.ID != policy.MaintainFoodStorage || !ownerActive(states[i]) {
			continue
		}
		open, err := standardOpenWork(ctx, tx, states[i])
		if err != nil {
			return nil, err
		}
		storageOpen = storageOpen || open
	}
	var out []policy.ConcernProgress
	for i, n := range needs.Assessments {
		g := states[i]
		if !ownerActive(g) {
			continue
		}
		last := old[n.ID]
		evidence := policy.ProgressEvidence{Observed: deficits[n.ID]}
		var method domain.MethodID
		labor := policy.ConcernLabor(n.ID)
		for _, m := range g.OwnerMethods() {
			if m.Episode != g.OwnerEpisode() {
				continue
			}
			plan, err := load(ctx, tx, m.Plan)
			if err != nil {
				return nil, err
			}
			for _, p := range plan.Progress {
				v := p.View()
				if effect, known := v.Effect.Value(); known && (effect == domain.EffectCompleted || effect == domain.EffectAbsent) && v.Tick > last.LastProgress && last.Concern == n.ID {
					evidence.Advanced = true
				}
				building := !plan.Retired && policy.AppliedBuildingOpen(p, request.Facts.CurrentConstruction)
				if !building && !domain.StandardWorkOpen([]domain.Progress{p}) {
					continue
				}
				method = m.Method
				evidence.Open = true
				if building || v.Stage == domain.Dispatched || v.Stage == domain.AwaitingObservation || v.Unresolved {
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
		contract := policy.ConcernProgressContract(methodLabel(method), request.Policy)
		if n.ID == policy.EnsureFoodSupply {
			contract, evidence.Prerequisite, evidence.Observed = policy.FoodProgress(request.Facts, request.Policy, storageOpen)
		}
		record := policy.ReviewConcernProgress(last, n.ID, contract, evidence, request.Tick)
		if !evidence.Advanced {
			// A deadline that passed without native progress keys the
			// failed situation out for a bounded cooldown; the planners
			// rotate the method or target (stall cancels, attempt counts).
			record, _ = policy.ExpireConcernProgress(record, contract, request.Tick, policy.CooldownKey(record.Method, string(record.Blocked)), nil)
		}
		if err := policy.ValidateConcernProgress(record, request.Tick); err != nil {
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
	case domain.BuildingAction, domain.DeconstructionAction, domain.RemoveRoofAction, domain.WallRemovalAction, domain.RepairAction, domain.MoveBuildingAction, domain.UninstallBuildingAction:
		return policy.LaborProfile{policy.WorkConstruction}
	case domain.HaulAction, domain.StripAction:
		return policy.LaborProfile{policy.WorkHauling}
	case domain.CutPlantAction, domain.AreaPlantCutAction:
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
func (s *Store) RecordProgressCooldown(ctx context.Context, revision uint64, need domain.ConcernID, key string, until domain.Tick) (Rounds, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return Rounds{}, err
	}
	defer tx.Rollback()
	review, err := loadRounds(ctx, tx)
	if err != nil {
		return Rounds{}, err
	}
	if review.Revision != revision {
		return Rounds{}, fmt.Errorf("%w: rounds revision %d, cooldown under %d", ErrConflict, review.Revision, revision)
	}
	changed := false
	for i := range review.Progress {
		if review.Progress[i].Concern != need {
			continue
		}
		review.Progress[i] = policy.AddProgressCooldown(review.Progress[i], key, until, review.Tick)
		if err = policy.ValidateConcernProgress(review.Progress[i], review.Tick); err != nil {
			return Rounds{}, err
		}
		changed = true
	}
	if !changed {
		return review, tx.Commit()
	}
	data, err := json.Marshal(review)
	if err != nil {
		return Rounds{}, err
	}
	if len(data) > 1024*1024 {
		return Rounds{}, ErrCapacity
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO rounds(singleton,payload) VALUES(1,?) ON CONFLICT(singleton) DO UPDATE SET payload=excluded.payload", data); err != nil {
		return Rounds{}, err
	}
	return review, tx.Commit()
}

// GoalProgress is need's progress record in the review, if it has one.
func (r Rounds) ConcernProgress(need domain.ConcernID) (policy.ConcernProgress, bool) {
	for _, p := range r.Progress {
		if p.Concern == need {
			return p, true
		}
	}
	return policy.ConcernProgress{}, false
}

// RecordPlannerReasons files each goal's latest planner refusal on its
// progress record (ConcernProgress.Planner); the zero note clears it (the
// planner admitted or found work). A record with no method, or one already
// naming a planner refusal or wait, is relabelled at once so the strip names the reason
// before the next review; held records keep their hold. Goals without a
// record are skipped. It reports whether anything changed.
func (s *Store) RecordPlannerReasons(ctx context.Context, reasons map[domain.ConcernID]policy.PlannerNote) (bool, error) {
	if len(reasons) == 0 {
		return false, nil
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return false, err
	}
	defer tx.Rollback()
	review, err := loadRounds(ctx, tx)
	if err != nil {
		return false, err
	}
	changed := false
	for i := range review.Progress {
		p := &review.Progress[i]
		note, ok := reasons[p.Concern]
		if !ok || p.PlannerNote() == note {
			continue
		}
		unmethoded := p.Unmethoded()
		p.Planner, p.PlannerSubject = note.Cause, note.Subject
		if unmethoded {
			p.Blocked = note.Blocked()
		}
		if err = policy.ValidateConcernProgress(*p, review.Tick); err != nil {
			return false, fmt.Errorf("%s: %w", p.Concern, err)
		}
		changed = true
	}
	if !changed {
		return false, nil
	}
	data, err := json.Marshal(review)
	if err != nil {
		return false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO rounds(singleton,payload) VALUES(1,?) ON CONFLICT(singleton) DO UPDATE SET payload=excluded.payload", data); err != nil {
		return false, err
	}
	return true, tx.Commit()
}
