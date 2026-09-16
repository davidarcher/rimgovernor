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

// There is one author of orders. Control is a pause flag: Resume enables
// autonomous play for a world, Pause halts it.
const (
	ResumeControl ControlKind = "resume"
	PauseControl  ControlKind = "pause"
)

type ControlPhase string

const (
	PendingControl   ControlPhase = "pending"
	RunningControl   ControlPhase = "running"
	PausedControl    ControlPhase = "paused"
	RefusedControl   ControlPhase = "refused"
	UncertainControl ControlPhase = "uncertain"
)

type ControlRequest struct {
	RequestID string
	Kind      ControlKind
	World     World
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
	case ResumeControl, PauseControl:
		return nil
	}
	return errors.New("unknown control kind")
}
func validControlResult(kind ControlKind, phase ControlPhase, g domain.NativeGeneration) bool {
	switch phase {
	case RunningControl:
		return kind == ResumeControl && g > 0
	case PausedControl:
		return kind == PauseControl && g == 0
	case RefusedControl, UncertainControl:
		return g == 0
	}
	return false
}

const controlColumns = "request_id,kind,colony,load_token,map_id,phase,native_generation"

func scanControl(row *sql.Row) (ControlRecord, error) {
	var r ControlRecord
	var generation string
	err := row.Scan(&r.Request.RequestID, &r.Request.Kind, &r.Request.World.Colony, &r.Request.World.Load, &r.Request.World.Map, &r.Phase, &generation)
	if errors.Is(err, sql.ErrNoRows) {
		return r, ErrNotFound
	}
	if err != nil {
		return r, err
	}
	n, e := strconv.ParseUint(generation, 10, 64)
	if e != nil || strconv.FormatUint(n, 10) != generation {
		return ControlRecord{}, errors.New("corrupt control integer")
	}
	r.NativeGeneration = domain.NativeGeneration(n)
	if err = r.Request.validate(); err != nil {
		return ControlRecord{}, err
	}
	if (r.Phase == PendingControl && r.NativeGeneration != 0) || (r.Phase != PendingControl && !validControlResult(r.Request.Kind, r.Phase, r.NativeGeneration)) {
		return ControlRecord{}, errors.New("corrupt control result")
	}
	return r, nil
}
func lookupControl(ctx context.Context, tx *sql.Tx, id string) (ControlRecord, error) {
	return scanControl(tx.QueryRowContext(ctx, "SELECT "+controlColumns+" FROM control_intents WHERE request_id=?", id))
}
func currentControl(ctx context.Context, tx *sql.Tx) (ControlRecord, error) {
	return scanControl(tx.QueryRowContext(ctx, "SELECT "+controlColumns+" FROM control_intents ORDER BY rowid DESC LIMIT 1"))
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
	var count int
	if err = tx.QueryRowContext(ctx, "SELECT count(*) FROM control_intents").Scan(&count); err != nil {
		return ControlRecord{}, false, err
	}
	if count >= 4096 {
		return ControlRecord{}, false, ErrCapacity
	}
	result := ControlRecord{Request: q, Phase: PendingControl}
	_, err = tx.ExecContext(ctx, "INSERT INTO control_intents("+controlColumns+") VALUES(?,?,?,?,?,?,?)", q.RequestID, q.Kind, q.World.Colony, q.World.Load, q.World.Map, PendingControl, "0")
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

// RootPlanID names the empty plan whose identity carries autonomous authority
// for one world. Routine methods and player submissions are authorized
// against it; it never holds actions of its own.
func RootPlanID(w World) domain.PlanID {
	return domain.PlanID(fmt.Sprintf("root/%s/%s/%d", w.Colony, w.Load, w.Map))
}

// EnsureRootPlan returns the world's root plan, creating it on first Resume.
func (s *Store) EnsureRootPlan(ctx context.Context, w World) (PlanState, error) {
	if err := w.Validate(); err != nil {
		return PlanState{}, err
	}
	id := RootPlanID(w)
	tx, err := s.begin(ctx)
	if err != nil {
		return PlanState{}, err
	}
	defer tx.Rollback()
	state, err := load(ctx, tx, id)
	if err == nil {
		return state, tx.Commit()
	}
	if !errors.Is(err, ErrNotFound) {
		return PlanState{}, err
	}
	plan, err := domain.NewPlan(id, 1, nil)
	if err != nil {
		return PlanState{}, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return PlanState{}, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO root_plans(plan_id,colony,load_token,map_id) VALUES(?,?,?,?)", id, w.Colony, w.Load, w.Map); err != nil {
		return PlanState{}, err
	}
	if state, err = load(ctx, tx, id); err != nil {
		return PlanState{}, err
	}
	return state, tx.Commit()
}

// planWorld resolves the world a plan belongs to: the root plan's own world or
// the submission that produced a player plan.
func planWorld(ctx context.Context, tx *sql.Tx, plan domain.PlanID) (World, error) {
	var world World
	err := tx.QueryRowContext(ctx, "SELECT colony,load_token,map_id FROM root_plans WHERE plan_id=? UNION ALL SELECT colony,load_token,map_id FROM submissions WHERE plan_id=?", plan, plan).Scan(&world.Colony, &world.Load, &world.Map)
	if errors.Is(err, sql.ErrNoRows) {
		return World{}, ErrNotFound
	}
	return world, err
}
