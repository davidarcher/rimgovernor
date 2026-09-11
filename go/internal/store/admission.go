package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type MaterialCost struct {
	Definition string
	Count      int64
}

// Admission retains complete observed costs and footprint, not permission to
// dispatch without fresh policy checks. Nil Costs means unknown and is invalid;
// an empty nonnil list explicitly describes a free native placement.
type Admission struct {
	Snapshot  domain.GenerationSnapshot
	Tick      domain.Tick
	Costs     []MaterialCost
	Footprint []domain.Cell
}
type ActionAdmission struct {
	Action    domain.ActionID
	Admission Admission
}

func validateAdmission(a domain.Action, p domain.Progress, admission Admission) error {
	if err := admission.Snapshot.Validate(); err != nil {
		return err
	}
	v := p.View()
	if admission.Snapshot.Plan != v.Plan || admission.Snapshot.Revision != v.Revision || admission.Tick < 0 {
		return errors.New("admission plan or tick mismatch")
	}
	if admission.Costs == nil || len(admission.Costs) > 256 {
		return errors.New("complete bounded admission costs required")
	}
	resources := map[string]bool{}
	for _, cost := range admission.Costs {
		if !utf8.ValidString(cost.Definition) || strings.TrimSpace(cost.Definition) == "" || strings.ContainsRune(cost.Definition, 0) || len(cost.Definition) > 256 || cost.Count < 0 || resources[cost.Definition] {
			return errors.New("invalid or duplicate admission cost")
		}
		resources[cost.Definition] = true
	}
	building, ok := a.Building()
	if !ok {
		return errors.New("unsupported admission action")
	}
	if len(admission.Footprint) == 0 || len(admission.Footprint) > 4096 {
		return errors.New("complete bounded admission footprint required")
	}
	seen := map[domain.Cell]bool{}
	anchor := false
	for _, cell := range admission.Footprint {
		if cell.X < 0 || cell.Z < 0 || seen[cell] {
			return errors.New("invalid or duplicate admission cell")
		}
		seen[cell] = true
		anchor = anchor || cell == building.Cell()
	}
	if !anchor {
		return errors.New("admission footprint does not contain action anchor")
	}
	return nil
}

// ReserveAndPrepare atomically records native accounting evidence and prepares
// pending work. An unissued Prepared action may replace its record under exactly
// the same authority; its durable preparation tick remains unchanged. An observed
// absent attempt may prepare again, while unknown, completed and cancelled work
// can never lose its accounting record through this API.
func (s *Store) ReserveAndPrepare(ctx context.Context, plan domain.PlanID, action domain.ActionID, admission Admission) (domain.Progress, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return domain.Progress{}, err
	}
	defer tx.Rollback()
	state, err := load(ctx, tx, plan)
	if err != nil {
		return domain.Progress{}, err
	}
	var a domain.Action
	var p domain.Progress
	found := false
	for i, candidate := range state.Spec.Actions() {
		if candidate.ID() == action {
			a = candidate
			p = state.Progress[i]
			found = true
			break
		}
	}
	if !found {
		return domain.Progress{}, ErrNotFound
	}
	if err = validateAdmission(a, p, admission); err != nil {
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
	// Retain the newest validated observation even when preparation predates it.
	for _, old := range state.Admissions {
		if old.Action == action && admission.Tick < old.Admission.Tick {
			return domain.Progress{}, errors.New("admission observation moved backwards")
		}
	}
	data, err := json.Marshal(admission)
	if err != nil {
		return domain.Progress{}, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO admissions(action_id,payload) VALUES(?,?) ON CONFLICT(action_id) DO UPDATE SET payload=excluded.payload", action, data); err != nil {
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

func loadAdmission(ctx context.Context, tx *sql.Tx, a domain.Action, p domain.Progress) (Admission, bool, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM admissions WHERE action_id=?", a.ID()).Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return Admission{}, false, nil
		}
		return Admission{}, false, err
	}
	var admission Admission
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&admission); err != nil {
		return Admission{}, false, err
	}
	if err := decoder.Decode(new(json.RawMessage)); err != io.EOF {
		return Admission{}, false, errors.New("trailing admission data")
	}
	canonical, err := json.Marshal(admission)
	if err != nil {
		return Admission{}, false, err
	}
	if !bytes.Equal(data, canonical) {
		return Admission{}, false, errors.New("noncanonical admission record")
	}
	if err = validateAdmission(a, p, admission); err != nil {
		return Admission{}, false, fmt.Errorf("invalid action %q admission: %w", a.ID(), err)
	}
	v := p.View()
	if (v.Stage == domain.Prepared || v.Attempt > 0) && !admission.Snapshot.Matches(v.Snapshot) {
		return Admission{}, false, errors.New("admission and progress authority disagree")
	}
	if v.Unresolved && admission.Tick > v.Tick {
		return Admission{}, false, errors.New("admission is newer than dispatched progress")
	}
	return admission, true, nil
}
