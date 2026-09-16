package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"strconv"
)

type ControlKind string

const (
	AcquireControl ControlKind = "acquire"
	ManualControl  ControlKind = "manual"
)

type ControlPhase string

const (
	PendingControl   ControlPhase = "pending"
	GrantedControl   ControlPhase = "granted"
	DisabledControl  ControlPhase = "disabled"
	RefusedControl   ControlPhase = "refused"
	UncertainControl ControlPhase = "uncertain"
)

type ControlRequest struct {
	RequestID string
	Kind      ControlKind
	World     World
	Plan      domain.PlanID
	Revision  domain.PlanRevision
}

// ControlRecord journals one control intent and its outcome. Records are
// ordered by insertion; there is no compare-and-swap between them because
// there is one author of orders.
type ControlRecord struct {
	Request          ControlRequest
	Phase            ControlPhase
	NativeGeneration domain.NativeGeneration
}

func (q ControlRequest) validate() error {
	if err := submissionID(q.RequestID); err != nil {
		return err
	}
	if err := q.World.Validate(); err != nil {
		return err
	}
	switch q.Kind {
	case AcquireControl:
		if err := submissionID(string(q.Plan)); err != nil {
			return err
		}
		if q.Revision == 0 {
			return errors.New("control requires plan revision")
		}
	case ManualControl:
		if q.Plan != "" || q.Revision != 0 {
			return errors.New("Manual cannot target a plan")
		}
	default:
		return errors.New("unknown control kind")
	}
	return nil
}
func validControlResult(kind ControlKind, phase ControlPhase, g domain.NativeGeneration) bool {
	switch phase {
	case GrantedControl:
		return kind == AcquireControl && g > 0
	case DisabledControl:
		return kind == ManualControl && g == 0
	case RefusedControl, UncertainControl:
		return g == 0
	}
	return false
}

const controlColumns = "request_id,kind,colony,load_token,map_id,plan_id,revision,phase,native_generation"

func scanControl(row *sql.Row) (ControlRecord, error) {
	var r ControlRecord
	var revision, generation string
	err := row.Scan(&r.Request.RequestID, &r.Request.Kind, &r.Request.World.Colony, &r.Request.World.Load, &r.Request.World.Map, &r.Request.Plan, &revision, &r.Phase, &generation)
	if errors.Is(err, sql.ErrNoRows) {
		return r, ErrNotFound
	}
	if err != nil {
		return r, err
	}
	values := []string{revision, generation}
	parsed := make([]uint64, 2)
	for i, v := range values {
		n, e := strconv.ParseUint(v, 10, 64)
		if e != nil || strconv.FormatUint(n, 10) != v {
			return ControlRecord{}, errors.New("corrupt control integer")
		}
		parsed[i] = n
	}
	r.Request.Revision = domain.PlanRevision(parsed[0])
	r.NativeGeneration = domain.NativeGeneration(parsed[1])
	if err = r.Request.validate(); err != nil {
		return ControlRecord{}, err
	}
	if (r.Phase == PendingControl && r.NativeGeneration != 0) || (r.Phase != PendingControl && !validControlResult(r.Request.Kind, r.Phase, r.NativeGeneration)) {
		return ControlRecord{}, errors.New("corrupt control result")
	}
	return r, nil
}
func lookupControl(ctx context.Context, tx *sql.Tx, id string) (ControlRecord, error) {
	record, err := scanControl(tx.QueryRowContext(ctx, "SELECT "+controlColumns+" FROM control_intents WHERE request_id=?", id))
	return checkedControl(ctx, tx, record, err)
}
func currentControl(ctx context.Context, tx *sql.Tx) (ControlRecord, error) {
	record, err := scanControl(tx.QueryRowContext(ctx, "SELECT "+controlColumns+" FROM control_intents ORDER BY rowid DESC LIMIT 1"))
	return checkedControl(ctx, tx, record, err)
}
func checkedControl(ctx context.Context, tx *sql.Tx, record ControlRecord, err error) (ControlRecord, error) {
	if err != nil {
		return ControlRecord{}, err
	}
	if record.Request.Kind == AcquireControl {
		var id string
		if err = tx.QueryRowContext(ctx, "SELECT request_id FROM submissions WHERE plan_id=?", record.Request.Plan).Scan(&id); err != nil {
			return ControlRecord{}, err
		}
		submitted, err := lookupAnySubmission(ctx, tx, id)
		if err != nil {
			return ControlRecord{}, err
		}
		if submitted.World != record.Request.World || submitted.Revision != record.Request.Revision {
			return ControlRecord{}, errors.New("corrupt control submission")
		}
	}
	return record, nil
}

// BeginControl journals intent before native work. Replay is historical evidence,
// never permission to dispatch again; unresolved Pending records remain uncertain.
func (s *Store) BeginControl(ctx context.Context, q ControlRequest) (ControlRecord, bool, error) {
	if err := q.validate(); err != nil {
		return ControlRecord{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ControlRecord{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupControl(ctx, tx, q.RequestID)
	if err == nil {
		if old.Request != q {
			return ControlRecord{}, false, ErrConflict
		}
		return old, false, tx.Commit()
	}
	if !errors.Is(err, ErrNotFound) {
		return ControlRecord{}, false, err
	}
	if q.Kind == AcquireControl {
		var submissionID string
		if err = tx.QueryRowContext(ctx, "SELECT request_id FROM submissions WHERE plan_id=?", q.Plan).Scan(&submissionID); errors.Is(err, sql.ErrNoRows) {
			return ControlRecord{}, false, ErrNotFound
		} else if err != nil {
			return ControlRecord{}, false, err
		}
		submitted, e := lookupAnySubmission(ctx, tx, submissionID)
		if e != nil {
			return ControlRecord{}, false, e
		}
		if submitted.World != q.World || submitted.Revision != q.Revision {
			return ControlRecord{}, false, ErrConflict
		}
	}
	var count int
	if err = tx.QueryRowContext(ctx, "SELECT count(*) FROM control_intents").Scan(&count); err != nil {
		return ControlRecord{}, false, err
	}
	if count >= 4096 {
		return ControlRecord{}, false, ErrCapacity
	}
	result := ControlRecord{Request: q, Phase: PendingControl}
	_, err = tx.ExecContext(ctx, "INSERT INTO control_intents("+controlColumns+") VALUES(?,?,?,?,?,?,?,?,?)", q.RequestID, q.Kind, q.World.Colony, q.World.Load, q.World.Map, q.Plan, fmt.Sprint(q.Revision), PendingControl, "0")
	if err != nil {
		return ControlRecord{}, false, err
	}
	if err = tx.Commit(); err != nil {
		return ControlRecord{}, false, err
	}
	return result, true, nil
}
func (s *Store) LookupControl(ctx context.Context, id string) (ControlRecord, error) {
	if err := submissionID(id); err != nil {
		return ControlRecord{}, err
	}
	return s.readControl(ctx, id, false)
}
func (s *Store) CurrentControl(ctx context.Context) (ControlRecord, error) {
	return s.readControl(ctx, "", true)
}
func (s *Store) readControl(ctx context.Context, id string, current bool) (ControlRecord, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ControlRecord{}, err
	}
	defer tx.Rollback()
	var r ControlRecord
	if current {
		r, err = currentControl(ctx, tx)
	} else {
		r, err = lookupControl(ctx, tx, id)
	}
	if err != nil {
		return ControlRecord{}, err
	}
	return r, tx.Commit()
}
func (s *Store) CompleteControl(ctx context.Context, id string, phase ControlPhase, generation domain.NativeGeneration) (ControlRecord, error) {
	if err := submissionID(id); err != nil {
		return ControlRecord{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ControlRecord{}, err
	}
	defer tx.Rollback()
	r, err := lookupControl(ctx, tx, id)
	if err != nil {
		return ControlRecord{}, err
	}
	if !validControlResult(r.Request.Kind, phase, generation) {
		return ControlRecord{}, errors.New("invalid control completion")
	}
	if r.Phase != PendingControl {
		if r.Phase != phase || r.NativeGeneration != generation {
			return ControlRecord{}, ErrConflict
		}
		return r, tx.Commit()
	}
	if _, err = tx.ExecContext(ctx, "UPDATE control_intents SET phase=?,native_generation=? WHERE request_id=?", phase, fmt.Sprint(generation), id); err != nil {
		return ControlRecord{}, err
	}
	r.Phase = phase
	r.NativeGeneration = generation
	return r, tx.Commit()
}
