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
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

type MaterialCost struct {
	Definition string
	Count      int64
}

// Admission retains complete observed costs and footprint, not permission to
// dispatch without fresh policy checks. Nil Costs means unknown and is invalid;
// an empty nonnil list explicitly describes a free native placement. Purpose
// is the spending class the method was admitted under, applied again by the
// fresh policy check at dispatch; records written before it carried one are
// routine.
type Admission struct {
	Snapshot  domain.GenerationSnapshot
	Tick      domain.Tick
	Costs     []MaterialCost
	Footprint []domain.Cell
	Purpose   policy.Purpose `json:",omitempty"`
}

// SpendingPurpose is the purpose a fresh policy check applies to this record.
func (a Admission) SpendingPurpose() policy.Purpose {
	if a.Purpose == "" {
		return policy.Routine
	}
	return a.Purpose
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
	if admission.Purpose != "" && !policy.ValidPurpose(admission.Purpose) {
		return errors.New("invalid admission purpose")
	}
	resources := map[string]bool{}
	for _, cost := range admission.Costs {
		if !utf8.ValidString(cost.Definition) || strings.TrimSpace(cost.Definition) == "" || strings.ContainsRune(cost.Definition, 0) || len(cost.Definition) > 256 || cost.Count < 0 || resources[cost.Definition] {
			return errors.New("invalid or duplicate admission cost")
		}
		resources[cost.Definition] = true
	}
	building, ok := a.Building()
	zone, isZone := a.ZoneCreate()
	if !ok && !isZone {
		return errors.New("unsupported admission action")
	}
	if isZone && (len(admission.Costs) != 0 || len(admission.Footprint) != len(zone.Cells())) {
		return errors.New("zone admission footprint or costs mismatch")
	}
	anchorCell := building.Cell()
	if isZone {
		anchorCell = zone.Cells()[0]
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
		anchor = anchor || cell == anchorCell
	}
	if isZone {
		for _, cell := range zone.Cells() {
			if !seen[cell] {
				return errors.New("zone admission footprint differs")
			}
		}
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
			a = candidate
			p = state.Progress[i]
			found = true
			break
		}
	}
	if !found {
		return domain.Progress{}, ErrNotFound
	}
	if a.Kind() != domain.BuildingAction {
		return domain.Progress{}, errors.New("typed zone preparation required")
	}
	if err = validateAdmission(a, p, admission); err != nil {
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
	// A zone's shared record is its footprint, written once at method
	// admission; the typed zone admission carries the authority it was
	// prepared under, so the footprint agrees with progress on the world,
	// plan and revision rather than on the native generation (PrepareZone).
	recorded, progressed := admission.Snapshot, v.Snapshot
	if _, isZone := a.ZoneCreate(); isZone {
		recorded.Native, progressed.Native = 0, 0
	}
	if (v.Stage == domain.Prepared || v.Attempt > 0) && !recorded.Matches(progressed) {
		return Admission{}, false, errors.New("admission and progress authority disagree")
	}
	if v.Unresolved && admission.Tick > v.Tick {
		return Admission{}, false, errors.New("admission is newer than dispatched progress")
	}
	return admission, true, nil
}
