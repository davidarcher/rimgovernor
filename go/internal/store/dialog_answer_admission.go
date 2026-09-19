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

// DialogAnswerAdmission is the native preview evidence PrepareDialogAnswer
// records for one AnswerDialog action (#156): the exact window/option the
// preview accepted, the same shape as ConfirmColonyNamesAdmission, its naming
// precedent.
type DialogAnswerAdmission struct {
	Snapshot    domain.GenerationSnapshot
	Tick        domain.Tick
	WindowID    int32
	OptionIndex int32
	OptionLabel string
	LetterToken string `json:",omitempty"`
}
type ActionDialogAnswerAdmission struct {
	Action    domain.ActionID
	Admission DialogAnswerAdmission
}

func validateDialogAnswerAdmission(a domain.Action, p domain.Progress, admission DialogAnswerAdmission) error {
	if err := admission.Snapshot.Validate(); err != nil {
		return err
	}
	v := p.View()
	if admission.Snapshot.Plan != v.Plan || admission.Snapshot.Revision != v.Revision || admission.Snapshot.Native == 0 || admission.Tick < 0 {
		return errors.New("admission plan or tick mismatch")
	}
	value, ok := a.DialogAnswer()
	if !ok || value.WindowID() != admission.WindowID || value.OptionIndex() != admission.OptionIndex || value.OptionLabel() != admission.OptionLabel || value.LetterToken() != admission.LetterToken {
		return errors.New("invalid dialog answer admission")
	}
	return nil
}

// PrepareDialogAnswer atomically records the exact native-validators-
// accept CAS evidence and prepares pending work. This record is evidence, not
// a lease; the executor must inspect and prepare again before dispatch after
// restart.
func (s *Store) PrepareDialogAnswer(ctx context.Context, plan domain.PlanID, action domain.ActionID, admission DialogAnswerAdmission) (domain.Progress, error) {
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
	if err = validateDialogAnswerAdmission(a, p, admission); err != nil {
		return domain.Progress{}, err
	}
	v := p.View()
	if v.Unresolved || admission.Tick < v.Tick {
		return domain.Progress{}, errors.New("admission cannot replace unresolved or newer progress")
	}
	// A prepared action has no write outstanding (dispatch is recorded before
	// any native write), so authority that moved since its preparation
	// re-prepares it under the current snapshot instead of stranding it.
	prepare := v.Stage == domain.Pending || v.Stage == domain.Prepared && !v.Snapshot.Matches(admission.Snapshot)
	switch v.Stage {
	case domain.Pending, domain.Prepared:
		if prepare {
			p, err = p.Prepare(admission.Snapshot, admission.Tick)
			if err != nil {
				return domain.Progress{}, err
			}
		}
	default:
		return domain.Progress{}, errors.New("admission replacement requires pending or prepared work")
	}
	for _, old := range state.DialogAnswerAdmissions {
		if old.Action == action && admission.Tick < old.Admission.Tick {
			return domain.Progress{}, errors.New("admission observation moved backwards")
		}
	}
	data, err := json.Marshal(admission)
	if err != nil {
		return domain.Progress{}, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO dialog_answer_admissions(action_id,payload) VALUES(?,?) ON CONFLICT(action_id) DO UPDATE SET payload=excluded.payload", action, data); err != nil {
		return domain.Progress{}, err
	}
	if prepare {
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

func loadDialogAnswerAdmission(ctx context.Context, tx *sql.Tx, a domain.Action, p domain.Progress) (DialogAnswerAdmission, bool, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM dialog_answer_admissions WHERE action_id=?", a.ID()).Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return DialogAnswerAdmission{}, false, nil
		}
		return DialogAnswerAdmission{}, false, err
	}
	var admission DialogAnswerAdmission
	if len(data) > 32768 {
		return DialogAnswerAdmission{}, false, errors.New("dialog answer admission exceeds bound")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&admission); err != nil {
		return DialogAnswerAdmission{}, false, err
	}
	if err := decoder.Decode(new(json.RawMessage)); err != io.EOF {
		return DialogAnswerAdmission{}, false, errors.New("trailing admission data")
	}
	canonical, err := json.Marshal(admission)
	if err != nil {
		return DialogAnswerAdmission{}, false, err
	}
	if !bytes.Equal(data, canonical) {
		return DialogAnswerAdmission{}, false, errors.New("noncanonical admission record")
	}
	if err = validateDialogAnswerAdmission(a, p, admission); err != nil {
		return DialogAnswerAdmission{}, false, fmt.Errorf("invalid action %q admission: %w", a.ID(), err)
	}
	v := p.View()
	if (v.Stage == domain.Prepared || v.Attempt > 0) && !admission.Snapshot.Matches(v.Snapshot) {
		return DialogAnswerAdmission{}, false, errors.New("admission and progress authority disagree")
	}
	if (v.Unresolved || v.Stage == domain.Completed || v.Stage == domain.Unsuccessful) && admission.Tick > v.Tick {
		return DialogAnswerAdmission{}, false, errors.New("admission is newer than dispatched progress")
	}
	return admission, true, nil
}
