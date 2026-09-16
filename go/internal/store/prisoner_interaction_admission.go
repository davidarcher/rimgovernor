package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"io"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type PrisonerInteractionAdmission struct {
	Snapshot          domain.GenerationSnapshot
	Tick              domain.Tick
	Pawn              domain.PawnID
	Interaction       domain.PrisonerInteractionMode
	PawnSnapshotToken string
}
type ActionPrisonerInteractionAdmission struct {
	Action    domain.ActionID
	Admission PrisonerInteractionAdmission
}

func validatePrisonerInteractionAdmission(a domain.Action, p domain.Progress, admission PrisonerInteractionAdmission) error {
	if err := admission.Snapshot.Validate(); err != nil {
		return err
	}
	v := p.View()
	if admission.Snapshot.Plan != v.Plan || admission.Snapshot.Revision != v.Revision || admission.Snapshot.Native == 0 || admission.Tick < 0 {
		return errors.New("admission plan or tick mismatch")
	}
	interaction, ok := a.PrisonerInteraction()
	if !ok || interaction.Pawn() != admission.Pawn || interaction.Interaction() != admission.Interaction || submissionID(admission.PawnSnapshotToken) != nil {
		return errors.New("invalid prisoner interaction admission")
	}
	return nil
}

// PreparePrisonerInteraction atomically records the exact prisoner CAS
// evidence and prepares pending work. This record is evidence, not a lease;
// the executor must inspect and prepare again before dispatch after restart.
func (s *Store) PreparePrisonerInteraction(ctx context.Context, plan domain.PlanID, action domain.ActionID, admission PrisonerInteractionAdmission) (domain.Progress, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return domain.Progress{}, err
	}
	defer tx.Rollback()
	if err = guardGoalWork(ctx, tx, plan, admission.Snapshot, admission.Tick); err != nil {
		return domain.Progress{}, err
	}
	state, err := load(ctx, tx, plan)
	if err != nil {
		return domain.Progress{}, err
	}
	if err = state.Spec.CheckDependencies(action, state.Progress, admission.Snapshot, admission.Tick); err != nil {
		return domain.Progress{}, err
	}
	var a domain.Action
	var p domain.Progress
	found := false
	for i, candidate := range state.Spec.Actions() {
		if candidate.ID() == action {
			a, p, found = candidate, state.Progress[i], true
			break
		}
	}
	if !found {
		return domain.Progress{}, ErrNotFound
	}
	if err = validatePrisonerInteractionAdmission(a, p, admission); err != nil {
		return domain.Progress{}, err
	}
	v := p.View()
	if v.Unresolved || admission.Tick < v.Tick {
		return domain.Progress{}, errors.New("admission cannot replace unresolved or newer progress")
	}
	switch v.Stage {
	case domain.Pending:
		p, err = p.Prepare(admission.Snapshot, admission.Tick)
		if err != nil {
			return domain.Progress{}, err
		}
	case domain.Prepared:
		if !v.Snapshot.Matches(admission.Snapshot) {
			return domain.Progress{}, errors.New("prepared admission authority changed")
		}
	default:
		return domain.Progress{}, errors.New("admission replacement requires pending or prepared work")
	}
	for _, old := range state.PrisonerInteractionAdmissions {
		if old.Action == action && admission.Tick < old.Admission.Tick {
			return domain.Progress{}, errors.New("admission observation moved backwards")
		}
	}
	data, err := json.Marshal(admission)
	if err != nil {
		return domain.Progress{}, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO prisoner_interaction_admissions(action_id,payload) VALUES(?,?) ON CONFLICT(action_id) DO UPDATE SET payload=excluded.payload", action, data); err != nil {
		return domain.Progress{}, err
	}
	if v.Stage == domain.Pending {
		event, err := json.Marshal(transition{Kind: "prepare", Snapshot: admission.Snapshot, Tick: admission.Tick})
		if err != nil {
			return domain.Progress{}, err
		}
		if _, err = tx.ExecContext(ctx, "INSERT INTO transitions(action_id,payload) VALUES(?,?)", action, event); err != nil {
			return domain.Progress{}, err
		}
	}
	if err = tx.Commit(); err != nil {
		return domain.Progress{}, err
	}
	return p, nil
}

func loadPrisonerInteractionAdmission(ctx context.Context, tx *sql.Tx, a domain.Action, p domain.Progress) (PrisonerInteractionAdmission, bool, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM prisoner_interaction_admissions WHERE action_id=?", a.ID()).Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return PrisonerInteractionAdmission{}, false, nil
		}
		return PrisonerInteractionAdmission{}, false, err
	}
	var admission PrisonerInteractionAdmission
	if len(data) > 32768 {
		return PrisonerInteractionAdmission{}, false, errors.New("prisoner interaction admission exceeds bound")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&admission); err != nil {
		return PrisonerInteractionAdmission{}, false, err
	}
	if err := decoder.Decode(new(json.RawMessage)); err != io.EOF {
		return PrisonerInteractionAdmission{}, false, errors.New("trailing admission data")
	}
	canonical, err := json.Marshal(admission)
	if err != nil {
		return PrisonerInteractionAdmission{}, false, err
	}
	if !bytes.Equal(data, canonical) {
		return PrisonerInteractionAdmission{}, false, errors.New("noncanonical admission record")
	}
	if err = validatePrisonerInteractionAdmission(a, p, admission); err != nil {
		return PrisonerInteractionAdmission{}, false, fmt.Errorf("invalid action %q admission: %w", a.ID(), err)
	}
	v := p.View()
	if (v.Stage == domain.Prepared || v.Attempt > 0) && !admission.Snapshot.Matches(v.Snapshot) {
		return PrisonerInteractionAdmission{}, false, errors.New("admission and progress authority disagree")
	}
	if (v.Unresolved || v.Stage == domain.Completed || v.Stage == domain.Unsuccessful) && admission.Tick > v.Tick {
		return PrisonerInteractionAdmission{}, false, errors.New("admission is newer than dispatched progress")
	}
	return admission, true, nil
}
