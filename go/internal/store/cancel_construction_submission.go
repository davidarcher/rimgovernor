package store

import (
	"bytes"
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// CancelConstructionSubmissionRequest is explicit player intent to remove the
// pending construction orders one earlier BuildRoom intent issued. It is the Go
// form of Python player_commands.CancelConstruction, which likewise carries a
// single intent_id and no per-placement selection: cancellation is always of a
// whole named construction, never of one wall of it.
type CancelConstructionSubmissionRequest struct {
	RequestID string
	World     World
	IntentID  string
}

// CancelConstructionSubmission records which of the named intent's placements
// this cancellation covers.
//
// Source is the build-room plan the intent resolved to and Targets are the
// placements in it that still have a native order to remove, in the shell's own
// expansion order. Placements that were never dispatched have nothing native to
// cancel and placements already built are deliberately preserved -- exactly
// Python capture_targets' "Completed buildings and already removed orders are
// preserved" -- so they appear in neither list.
//
// ObservedAbsent reports the whole-intent form of Python execute_target's
// 'observed_absent' result: every placement is already resolved, there is
// nothing left to cancel, and this is a valid outcome rather than an error.
// Plan and Action are then empty and no plan is committed, which is why this
// family keeps its own submission table instead of the shared submissions
// header -- that header requires exactly one committed action per request.
type CancelConstructionSubmission struct {
	Request        CancelConstructionSubmissionRequest
	Source         domain.PlanID
	Targets        []domain.ActionID
	Plan           domain.PlanID
	Action         domain.ActionID
	Revision       domain.PlanRevision
	ObservedAbsent bool
}

// ActionIDs is every committed cancellation identity in target order, derived
// from the header action exactly as BuildRoomSubmission.ActionIDs derives its
// placements.
func (v CancelConstructionSubmission) ActionIDs() []domain.ActionID {
	if v.ObservedAbsent {
		return nil
	}
	ids := make([]domain.ActionID, 0, len(v.Targets))
	for i := range v.Targets {
		ids = append(ids, cancelConstructionActionID(v.Action, i))
	}
	return ids
}

func cancelConstructionActionID(prefix domain.ActionID, index int) domain.ActionID {
	if index == 0 {
		return prefix
	}
	return domain.ActionID(fmt.Sprintf("%s-%d", prefix, index))
}

type constructionCancelPayload struct {
	Definition string
	X, Z       int32
	Stuff      string
}

func constructionCancelPayloadOf(c domain.ConstructionCancel) constructionCancelPayload {
	return constructionCancelPayload{c.Definition(), c.Cell().X, c.Cell().Z, c.Material()}
}
func reconstructConstructionCancelPayload(p constructionCancelPayload) (domain.ConstructionCancel, error) {
	return domain.NewConstructionCancel(p.Definition, domain.Cell{X: p.X, Z: p.Z}, p.Stuff)
}

// cancelConstructionPayload is the durable record of which source placements
// this cancellation covers. It is stored rather than recomputed because the
// source plan's progress moves on: a placement that was still a blueprint at
// submission may be built by the time the record is read back, and the record
// must keep describing what was actually committed.
type cancelConstructionPayload struct {
	Source  domain.PlanID
	Targets []domain.ActionID
}

func (c CancelConstructionSubmissionRequest) validate() error {
	if err := submissionID(c.RequestID); err != nil {
		return err
	}
	if err := c.World.Validate(); err != nil {
		return err
	}
	return domain.ValidateRoomIntent(c.IntentID)
}

// cancellableTargets selects the placements of one committed build-room plan
// that still have a native construction order to remove, the Go form of
// Python construction_cancellation.capture_targets' per-placement triage.
//
// A placement that was never dispatched placed nothing native, so there is
// nothing to cancel; a completed one is a finished building this command must
// never touch; a cancelled or unsuccessful one is already resolved; a refused
// dispatch placed nothing either. The remainder is judged on its receipt
// rather than its effect, because a live construction order is by definition
// still unresolved -- an accepted receipt is exactly Python's issued
// 'confirmed' flag. A dispatch whose receipt is missing or unknown refuses
// outright, matching Python's "Construction placement has an uncertain
// receipt; reconcile it before cancellation": cancelling against an uncertain
// write could destroy whatever stands there now rather than the order the
// player meant.
func cancellableTargets(state PlanState, ids []domain.ActionID) ([]domain.ActionID, []domain.ConstructionCancel, error) {
	actions := state.Spec.Actions()
	targets := make([]domain.ActionID, 0, len(ids))
	cancels := make([]domain.ConstructionCancel, 0, len(ids))
	for _, id := range ids {
		index := -1
		for i, a := range actions {
			if a.ID() == id {
				index = i
				break
			}
		}
		if index < 0 || index >= len(state.Progress) {
			return nil, nil, errors.New("construction intent placement is missing from its plan")
		}
		building, ok := actions[index].Building()
		if !ok {
			return nil, nil, errors.New("construction intent placement is not a building action")
		}
		v := state.Progress[index].View()
		if v.Attempt == 0 {
			continue
		}
		switch v.Stage {
		case domain.Completed, domain.Cancelled, domain.Unsuccessful:
			continue
		}
		receipt, known := v.Receipt.Value()
		if !known || receipt == domain.ReceiptUnknown {
			return nil, nil, errors.New("construction placement has an uncertain receipt; reconcile it before cancellation")
		}
		if receipt == domain.ReceiptRefused {
			continue
		}
		cancel, err := domain.NewConstructionCancelFor(building)
		if err != nil {
			return nil, nil, err
		}
		targets = append(targets, id)
		cancels = append(cancels, cancel)
	}
	return targets, cancels, nil
}

// SubmitCancelConstruction atomically resolves one player construction intent
// and commits a plan holding one cancellation per still-open placement.
// Submission neither acquires authority nor removes anything; a worker later
// admits each committed action, re-inspects its exact native target and only
// then dispatches.
func (s *Store) SubmitCancelConstruction(ctx context.Context, c CancelConstructionSubmissionRequest) (CancelConstructionSubmission, bool, error) {
	if err := c.validate(); err != nil {
		return CancelConstructionSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return CancelConstructionSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupCancelConstruction(ctx, tx, c.RequestID)
	if err == nil {
		if old.Request != c {
			return CancelConstructionSubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return CancelConstructionSubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return CancelConstructionSubmission{}, false, err
	}
	source, err := lookupBuildRoomIntent(ctx, tx, c.World, c.IntentID)
	if err != nil {
		return CancelConstructionSubmission{}, false, err
	}
	state, err := load(ctx, tx, source.Plan)
	if err != nil {
		return CancelConstructionSubmission{}, false, err
	}
	targets, cancels, err := cancellableTargets(state, source.ActionIDs())
	if err != nil {
		return CancelConstructionSubmission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return CancelConstructionSubmission{}, false, err
	}
	result := CancelConstructionSubmission{Request: c, Source: source.Plan, Targets: targets, Revision: 1, ObservedAbsent: len(targets) == 0}
	if !result.ObservedAbsent {
		result.Plan = domain.PlanID("cancel-construction-" + hex.EncodeToString(entropy[:16]))
		result.Action = domain.ActionID("cancel-construction-action-" + hex.EncodeToString(entropy[16:]))
		actions := make([]domain.Action, 0, len(cancels))
		for i, cancel := range cancels {
			action, err := domain.NewConstructionCancelAction(cancelConstructionActionID(result.Action, i), cancel)
			if err != nil {
				return CancelConstructionSubmission{}, false, err
			}
			actions = append(actions, action)
		}
		plan, err := domain.NewPlan(result.Plan, 1, actions)
		if err != nil {
			return CancelConstructionSubmission{}, false, err
		}
		if err = createPlan(ctx, tx, plan); err != nil {
			return CancelConstructionSubmission{}, false, err
		}
	}
	data, err := json.Marshal(cancelConstructionPayload{Source: source.Plan, Targets: targets})
	if err != nil {
		return CancelConstructionSubmission{}, false, err
	}
	var planID, actionID any
	if !result.ObservedAbsent {
		planID, actionID = string(result.Plan), string(result.Action)
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO cancel_construction_submissions(request_id,colony,load_token,map_id,intent_id,source_plan,plan_id,action_id,revision,payload) VALUES(?,?,?,?,?,?,?,?,?,?)",
		c.RequestID, c.World.Colony, c.World.Load, c.World.Map, c.IntentID, source.Plan, planID, actionID, "1", data); err != nil {
		return CancelConstructionSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return CancelConstructionSubmission{}, false, err
	}
	return result, true, nil
}

func (s *Store) LookupCancelConstructionSubmission(ctx context.Context, requestID string) (CancelConstructionSubmission, error) {
	if err := submissionID(requestID); err != nil {
		return CancelConstructionSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return CancelConstructionSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupCancelConstruction(ctx, tx, requestID)
	if err != nil {
		return CancelConstructionSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return CancelConstructionSubmission{}, err
	}
	return result, nil
}

func lookupCancelConstruction(ctx context.Context, tx *sql.Tx, id string) (CancelConstructionSubmission, error) {
	var (
		world            World
		intent, revision string
		sourcePlan       string
		plan, action     sql.NullString
		data             []byte
	)
	err := tx.QueryRowContext(ctx, "SELECT colony,load_token,map_id,intent_id,source_plan,plan_id,action_id,revision,payload FROM cancel_construction_submissions WHERE request_id=?", id).
		Scan(&world.Colony, &world.Load, &world.Map, &intent, &sourcePlan, &plan, &action, &revision, &data)
	if errors.Is(err, sql.ErrNoRows) {
		return CancelConstructionSubmission{}, ErrNotFound
	}
	if err != nil {
		return CancelConstructionSubmission{}, err
	}
	if len(data) > 32768 {
		return CancelConstructionSubmission{}, errors.New("cancel construction submission exceeds bound")
	}
	var payload cancelConstructionPayload
	if err = json.Unmarshal(data, &payload); err != nil {
		return CancelConstructionSubmission{}, err
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return CancelConstructionSubmission{}, err
	}
	if !bytes.Equal(canonical, data) {
		return CancelConstructionSubmission{}, errors.New("noncanonical cancel construction submission")
	}
	if revision != "1" {
		return CancelConstructionSubmission{}, errors.New("invalid submitted revision")
	}
	result := CancelConstructionSubmission{
		Request:        CancelConstructionSubmissionRequest{RequestID: id, World: world, IntentID: intent},
		Source:         payload.Source,
		Targets:        payload.Targets,
		Plan:           domain.PlanID(plan.String),
		Action:         domain.ActionID(action.String),
		Revision:       1,
		ObservedAbsent: !plan.Valid,
	}
	if err = result.Request.validate(); err != nil {
		return CancelConstructionSubmission{}, err
	}
	if result.Source != domain.PlanID(sourcePlan) || result.Revision == 0 || plan.Valid != action.Valid {
		return CancelConstructionSubmission{}, errors.New("cancel construction submission is corrupt")
	}
	if result.ObservedAbsent {
		if len(result.Targets) != 0 {
			return CancelConstructionSubmission{}, errors.New("cancel construction submission is corrupt")
		}
		return result, nil
	}
	if len(result.Targets) == 0 {
		return CancelConstructionSubmission{}, errors.New("cancel construction submission is corrupt")
	}
	source, err := load(ctx, tx, result.Source)
	if err != nil {
		return CancelConstructionSubmission{}, err
	}
	expected := make([]domain.ConstructionCancel, 0, len(result.Targets))
	for _, target := range result.Targets {
		found := false
		for _, a := range source.Spec.Actions() {
			if a.ID() != target {
				continue
			}
			building, ok := a.Building()
			if !ok {
				return CancelConstructionSubmission{}, errors.New("cancel construction target is not a placement")
			}
			cancel, err := domain.NewConstructionCancelFor(building)
			if err != nil {
				return CancelConstructionSubmission{}, err
			}
			expected = append(expected, cancel)
			found = true
			break
		}
		if !found {
			return CancelConstructionSubmission{}, errors.New("cancel construction target is missing from its source plan")
		}
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return CancelConstructionSubmission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != len(expected) {
		return CancelConstructionSubmission{}, errors.New("cancel construction submission plan is corrupt")
	}
	for i, a := range actions {
		if a.ID() != cancelConstructionActionID(result.Action, i) {
			return CancelConstructionSubmission{}, errors.New("cancel construction submission plan is corrupt")
		}
		actual, ok := a.ConstructionCancel()
		if !ok || actual != expected[i] {
			return CancelConstructionSubmission{}, errors.New("cancel construction submission differs from intent")
		}
	}
	return result, nil
}
