// Package store persists fresh Go plans and validated domain transitions.
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
	"io"
	"net/url"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"modernc.org/sqlite"
)

const schemaVersion = 14
const applicationID = 0x52474f31

var ErrConflict = errors.New("plan or action identity already exists")
var ErrNotFound = errors.New("plan or action not found")

type Store struct{ db *sql.DB }

// ControllerSessionID identifies one persistent controller execution namespace.
// It is independent of HTTP process sessions and survives controller restarts.
type ControllerSessionID string
type PlanState struct {
	Spec            domain.PlanSpec
	Progress        []domain.Progress
	Admissions      []ActionAdmission
	DraftAdmissions []ActionDraftAdmission
	MeleeAdmissions []ActionMeleeAdmission
}

// Open accepts a filesystem path, never a caller-supplied SQLite connection URI.
func Open(ctx context.Context, path string) (*Store, error) {
	if path == "" {
		return nil, errors.New("database path is empty")
	}
	abs, err := filepath.Abs(path)
	if err != nil {
		return nil, err
	}
	uriPath := filepath.ToSlash(abs)
	if !strings.HasPrefix(uriPath, "/") {
		uriPath = "/" + uriPath
	}
	u := url.URL{Scheme: "file", Path: uriPath}
	q := u.Query()
	q.Set("_txlock", "immediate")
	q.Set("_busy_timeout", "25")
	q.Add("_pragma", "foreign_keys(1)")
	q.Add("_pragma", "synchronous(FULL)")
	u.RawQuery = q.Encode()
	db, err := sql.Open("sqlite", u.String())
	if err != nil {
		return nil, err
	}
	db.SetMaxOpenConns(1)
	db.SetMaxIdleConns(1)
	s := &Store{db: db}
	if err = s.initialize(ctx); err != nil {
		_ = db.Close()
		return nil, err
	}
	return s, nil
}
func (s *Store) Close() error { return s.db.Close() }

func (s *Store) begin(ctx context.Context) (*sql.Tx, error) {
	for {
		tx, err := s.db.BeginTx(ctx, nil)
		if err == nil {
			return tx, nil
		}
		var sqliteErr *sqlite.Error
		if !errors.As(err, &sqliteErr) || (sqliteErr.Code()&255 != 5 && sqliteErr.Code()&255 != 6) {
			return nil, err
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(10 * time.Millisecond):
		}
	}
}
func (s *Store) initialize(ctx context.Context) error {
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	var version, app int
	if err = tx.QueryRowContext(ctx, "PRAGMA user_version").Scan(&version); err != nil {
		return err
	}
	if err = tx.QueryRowContext(ctx, "PRAGMA application_id").Scan(&app); err != nil {
		return err
	}
	if version == 0 && app == 0 {
		var count int
		if err = tx.QueryRowContext(ctx, "SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'").Scan(&count); err != nil {
			return err
		}
		if count != 0 {
			return errors.New("unversioned nonempty database is incompatible")
		}
		_, err = tx.ExecContext(ctx, `CREATE TABLE metadata(singleton INTEGER PRIMARY KEY CHECK(singleton=1), controller_session_id TEXT NOT NULL);
CREATE TABLE plans(id TEXT PRIMARY KEY, revision TEXT NOT NULL);
CREATE TABLE actions(id TEXT PRIMARY KEY, plan_id TEXT NOT NULL REFERENCES plans(id), ordinal INTEGER NOT NULL, kind TEXT NOT NULL CHECK(kind IN ('building','owned_draft','melee_attack')), definition TEXT, x INTEGER, z INTEGER, rotation TEXT, stuff TEXT, pawn TEXT, target TEXT, draft_action TEXT REFERENCES actions(id), CHECK((kind='building' AND definition IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NOT NULL AND stuff IS NOT NULL AND pawn IS NULL AND target IS NULL AND draft_action IS NULL) OR (kind='owned_draft' AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NULL AND draft_action IS NULL) OR (kind='melee_attack' AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NOT NULL)), UNIQUE(plan_id,ordinal)) STRICT;
CREATE TABLE transitions(sequence INTEGER PRIMARY KEY, action_id TEXT NOT NULL REFERENCES actions(id), payload BLOB NOT NULL);
CREATE TABLE admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL);
CREATE TABLE draft_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE melee_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE clock_attempts(request_id TEXT PRIMARY KEY, native_action_id TEXT NOT NULL UNIQUE, payload BLOB NOT NULL, phase TEXT NOT NULL CHECK(phase IN ('prepared','dispatched','uncertain','applied','refused')), reply BLOB, scope_context BLOB) STRICT;
CREATE TABLE clock_epochs(start_request_id TEXT PRIMARY KEY REFERENCES clock_attempts(request_id), stage TEXT NOT NULL CHECK(stage IN ('required','pausing','uncertain','paused','retired','superseded')), sequence TEXT NOT NULL, context BLOB, status BLOB) STRICT;
CREATE TABLE submissions(request_id TEXT PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN ('building','owned_draft')), colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, plan_id TEXT NOT NULL UNIQUE REFERENCES plans(id), action_id TEXT NOT NULL UNIQUE REFERENCES actions(id), revision TEXT NOT NULL) STRICT;
CREATE TABLE draft_submissions(request_id TEXT PRIMARY KEY REFERENCES submissions(request_id), pawn TEXT NOT NULL) STRICT;
CREATE INDEX action_transitions ON transitions(action_id,sequence);
CREATE TABLE building_submissions(request_id TEXT PRIMARY KEY REFERENCES submissions(request_id), definition TEXT NOT NULL, x INTEGER NOT NULL, z INTEGER NOT NULL, rotation TEXT NOT NULL, stuff TEXT NOT NULL) STRICT;`)
		if err != nil {
			return err
		}
		if err = initializeClockReview(ctx, tx); err != nil {
			return err
		}
		if err = initializeGoals(ctx, tx); err != nil {
			return err
		}
		if err = initializeClockInbox(ctx, tx); err != nil {
			return err
		}
		if err = initializeClockSequence(ctx, tx); err != nil {
			return err
		}
		if err = initializeClockCheckpoint(ctx, tx); err != nil {
			return err
		}
		var entropy [32]byte
		if _, err = tx.ExecContext(ctx, `CREATE TABLE control_intents(request_id TEXT PRIMARY KEY, kind TEXT NOT NULL, colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, plan_id TEXT NOT NULL, revision TEXT NOT NULL, expected_direction TEXT NOT NULL, direction TEXT NOT NULL UNIQUE, phase TEXT NOT NULL, native_generation TEXT NOT NULL) STRICT`); err != nil {
			return err
		}
		if _, err = rand.Read(entropy[:]); err != nil {
			return err
		}
		if _, err = tx.ExecContext(ctx, "INSERT INTO metadata(singleton,controller_session_id) VALUES(1,?)", hex.EncodeToString(entropy[:])); err != nil {
			return err
		}
		if _, err = tx.ExecContext(ctx, fmt.Sprintf("PRAGMA user_version=%d", schemaVersion)); err != nil {
			return err
		}
		if _, err = tx.ExecContext(ctx, fmt.Sprintf("PRAGMA application_id=%d", applicationID)); err != nil {
			return err
		}
	} else if version != schemaVersion || app != applicationID {
		return fmt.Errorf("incompatible database application/version: %d/%d", app, version)
	}
	for _, query := range []string{"SELECT action_id,payload FROM draft_admissions LIMIT 0", "SELECT action_id,payload FROM melee_admissions LIMIT 0", "SELECT request_id,kind,colony,load_token,map_id,plan_id,action_id,revision FROM submissions LIMIT 0", "SELECT request_id,pawn FROM draft_submissions LIMIT 0"} {
		if version != 0 {
			if _, err = tx.ExecContext(ctx, query); err != nil {
				return err
			}
		}
	}
	if err = checkClockReviewSchema(ctx, tx); err != nil {
		return err
	}
	if err = checkClockInboxSchema(ctx, tx); err != nil {
		return err
	}
	if err = checkClockSequenceSchema(ctx, tx); err != nil {
		return err
	}
	if err = checkClockCheckpointSchema(ctx, tx); err != nil {
		return err
	}
	// A matching version marker alone does not establish the expected tables.
	if _, err = tx.ExecContext(ctx, "SELECT request_id,native_action_id,payload,phase,reply,scope_context FROM clock_attempts LIMIT 0"); err != nil {
		return err
	}
	if _, err = tx.ExecContext(ctx, "SELECT start_request_id,stage,sequence,context,status FROM clock_epochs LIMIT 0"); err != nil {
		return err
	}
	if _, err = tx.ExecContext(ctx, "SELECT p.id,p.revision,a.id,a.ordinal,a.kind,a.pawn,a.target,a.draft_action,a.definition,a.x,a.z,a.rotation,a.stuff,t.sequence,t.payload FROM plans p LEFT JOIN actions a ON a.plan_id=p.id LEFT JOIN transitions t ON t.action_id=a.id LIMIT 0"); err != nil {
		return err
	}
	if _, err = identity(ctx, tx); err != nil {
		return err
	}
	if _, err = tx.ExecContext(ctx, "SELECT action_id,payload FROM admissions LIMIT 0"); err != nil {
		return err
	}
	if _, err = tx.ExecContext(ctx, "SELECT request_id,definition,x,z,rotation,stuff FROM building_submissions LIMIT 0"); err != nil {
		return err
	}
	if _, err = tx.ExecContext(ctx, "SELECT "+controlColumns+" FROM control_intents LIMIT 0"); err != nil {
		return err
	}
	if _, err = currentControl(ctx, tx); err != nil && !errors.Is(err, ErrNotFound) {
		return err
	}
	return tx.Commit()
}

// Identity never creates or repairs metadata. Missing or corrupt persistent
// identity must not turn an uncertain old attempt into a new execution namespace.
func (s *Store) Identity(ctx context.Context) (ControllerSessionID, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return "", err
	}
	defer tx.Rollback()
	id, err := identity(ctx, tx)
	if err != nil {
		return "", err
	}
	if err = tx.Commit(); err != nil {
		return "", err
	}
	return id, nil
}
func identity(ctx context.Context, tx *sql.Tx) (ControllerSessionID, error) {
	rows, err := tx.QueryContext(ctx, "SELECT singleton,controller_session_id FROM metadata")
	if err != nil {
		return "", err
	}
	defer rows.Close()
	var id string
	var singleton int
	if !rows.Next() {
		return "", errors.Join(errors.New("controller identity metadata is missing"), rows.Err())
	}
	if err = rows.Scan(&singleton, &id); err != nil {
		return "", err
	}
	if rows.Next() {
		return "", errors.New("controller identity metadata has multiple rows")
	}
	if err = rows.Err(); err != nil {
		return "", err
	}
	decoded, err := hex.DecodeString(id)
	if singleton != 1 || err != nil || len(decoded) != 32 || hex.EncodeToString(decoded) != id {
		return "", errors.New("corrupt controller identity metadata")
	}
	return ControllerSessionID(id), nil
}

// CreatePlan initializes every action atomically. IDs remain unique across plans.
func (s *Store) CreatePlan(ctx context.Context, plan domain.PlanSpec) error {
	if _, err := domain.NewPlan(plan.ID(), plan.Revision(), plan.Actions()); err != nil {
		return err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if err = createPlan(ctx, tx, plan); err != nil {
		return err
	}
	return tx.Commit()
}
func createPlan(ctx context.Context, tx *sql.Tx, plan domain.PlanSpec) error {
	var count int
	if err := tx.QueryRowContext(ctx, "SELECT count(*) FROM plans").Scan(&count); err != nil {
		return err
	}
	if count >= 256 {
		return ErrCapacity
	}
	if _, err := tx.ExecContext(ctx, "INSERT INTO plans(id,revision) VALUES(?,?)", plan.ID(), strconv.FormatUint(uint64(plan.Revision()), 10)); err != nil {
		return conflict(err)
	}
	for i, a := range plan.Actions() {
		if err := insertAction(ctx, tx, plan.ID(), i, a); err != nil {
			return err
		}
	}
	return nil
}

func conflict(err error) error {
	var e *sqlite.Error
	if errors.As(err, &e) && e.Code()&255 == 19 {
		return fmt.Errorf("%w: %v", ErrConflict, err)
	}
	return err
}

func (s *Store) LoadPlan(ctx context.Context, id domain.PlanID) (PlanState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return PlanState{}, err
	}
	defer tx.Rollback()
	state, err := load(ctx, tx, id)
	if err != nil {
		return PlanState{}, err
	}
	if err = tx.Commit(); err != nil {
		return PlanState{}, err
	}
	return state, nil
}
func load(ctx context.Context, tx *sql.Tx, id domain.PlanID) (PlanState, error) {
	var revision string
	if err := tx.QueryRowContext(ctx, "SELECT revision FROM plans WHERE id=?", id).Scan(&revision); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return PlanState{}, ErrNotFound
		}
		return PlanState{}, err
	}
	r, err := strconv.ParseUint(revision, 10, 64)
	if err != nil {
		return PlanState{}, err
	}
	rows, err := tx.QueryContext(ctx, "SELECT id,kind,definition,x,z,rotation,stuff,pawn,target,draft_action,ordinal FROM actions WHERE plan_id=? ORDER BY ordinal", id)
	if err != nil {
		return PlanState{}, err
	}
	var actions []domain.Action
	for rows.Next() {
		a, ordinal, e := scanAction(rows)
		if e != nil {
			rows.Close()
			return PlanState{}, e
		}
		if ordinal != len(actions) {
			rows.Close()
			return PlanState{}, errors.New("noncontiguous action order")
		}
		actions = append(actions, a)
	}
	err = rows.Err()
	rows.Close()
	if err != nil {
		return PlanState{}, err
	}
	plan, err := domain.NewPlan(id, domain.PlanRevision(r), actions)
	if err != nil {
		return PlanState{}, err
	}
	state := PlanState{Spec: plan, Progress: make([]domain.Progress, 0, len(actions))}
	for _, a := range actions {
		p, e := domain.NewProgress(plan, a.ID())
		if e != nil {
			return PlanState{}, e
		}
		rows, e := tx.QueryContext(ctx, "SELECT payload FROM transitions WHERE action_id=? ORDER BY sequence", a.ID())
		if e != nil {
			return PlanState{}, e
		}
		for rows.Next() {
			var data []byte
			if e = rows.Scan(&data); e != nil {
				break
			}
			var event transition
			e = decode(data, &event)
			if e != nil {
				break
			}
			if event.DraftReceipt != nil && event.DraftReceipt.Claim != nil {
				e = checkClaimSession(ctx, tx, *event.DraftReceipt.Claim)
			}
			if e == nil && event.DraftObserve != nil && event.DraftObserve.Claim != nil {
				e = checkClaimSession(ctx, tx, *event.DraftObserve.Claim)
			}
			if e == nil {
				p, e = apply(p, event)
			}
			if e != nil {
				break
			}
		}
		if e == nil {
			e = rows.Err()
		}
		rows.Close()
		if e != nil {
			return PlanState{}, fmt.Errorf("invalid action %q history: %w", a.ID(), e)
		}
		state.Progress = append(state.Progress, p)
		admission, present, err := loadAdmission(ctx, tx, a, p)
		if err != nil {
			return PlanState{}, err
		}
		draftAdmission, draftPresent, e := loadDraftAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if _, draft := a.OwnedDraft(); draft && !draftPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("draft progress lacks admission")
		}
		if draftPresent {
			state.DraftAdmissions = append(state.DraftAdmissions, ActionDraftAdmission{Action: a.ID(), Admission: draftAdmission})
		}
		if present {
			state.Admissions = append(state.Admissions, ActionAdmission{Action: a.ID(), Admission: admission})
		}
		meleeAdmission, meleePresent, e := loadMeleeAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.MeleeAttackAction && !meleePresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("melee progress lacks admission")
		}
		if meleePresent {
			state.MeleeAdmissions = append(state.MeleeAdmissions, ActionMeleeAdmission{Action: a.ID(), Admission: meleeAdmission})
		}
	}
	for _, record := range state.MeleeAdmissions {
		if err := validateMeleePrerequisite(ctx, tx, state, record.Action, record.Admission, false); err != nil {
			return PlanState{}, err
		}
	}
	return state, nil
}

// transition is a private persistence boundary, not a second progress model.
type transition struct {
	Kind                   string
	Snapshot               domain.GenerationSnapshot
	Tick                   domain.Tick
	Attempt                domain.AttemptID
	Receipt                domain.Receipt
	Observation            domain.Observation
	DraftReceipt           *draftReceiptEvent              `json:",omitempty"`
	DraftObserve           *draftObserveEvent              `json:",omitempty"`
	DraftBegin             *domain.DraftRelease            `json:",omitempty"`
	DraftResult            *draftResultEvent               `json:",omitempty"`
	DraftCleanupObserve    *domain.DraftCleanupObservation `json:",omitempty"`
	DraftScopeSupersession *domain.DraftScopeSupersession  `json:",omitempty"`
}

func decode(data []byte, event *transition) error {
	if len(data) > 32768 {
		return errors.New("transition exceeds bound")
	}
	d := json.NewDecoder(bytes.NewReader(data))
	d.DisallowUnknownFields()
	if err := d.Decode(event); err != nil {
		return err
	}
	if err := d.Decode(new(json.RawMessage)); err != io.EOF {
		return errors.New("trailing transition data")
	}
	// Store records are canonical, so duplicate fields and silently defaulted fields
	// are corruption rather than alternative input syntax.
	if event.DraftBegin != nil && event.DraftBegin.Sequence == 0 {
		return errors.New("missing persisted cleanup sequence")
	}
	canonical, err := json.Marshal(event)
	if err != nil {
		return err
	}
	if !bytes.Equal(canonical, data) {
		return errors.New("noncanonical transition record")
	}
	return nil
}
func apply(p domain.Progress, e transition) (domain.Progress, error) {
	if err := validateDraftEvent(e); err != nil {
		return p, err
	}
	switch e.Kind {
	case "draft_receipt", "draft_observe", "draft_begin", "draft_result", "draft_cleanup_observe", "draft_scope_supersession":
		return applyDraft(p, e)
	case "prepare":
		return p.Prepare(e.Snapshot, e.Tick)
	case "dispatch":
		return p.MarkDispatched(e.Snapshot, e.Tick)
	case "receipt":
		return p.RecordReceipt(e.Attempt, e.Receipt)
	case "observe":
		return p.Observe(e.Observation, e.Snapshot)
	case "cancel":
		return p.Cancel()
	default:
		return p, fmt.Errorf("unknown transition %q", e.Kind)
	}
}
func (s *Store) advance(ctx context.Context, plan domain.PlanID, action domain.ActionID, event transition) (domain.Progress, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return domain.Progress{}, err
	}
	defer tx.Rollback()
	next, err := advanceInTransaction(ctx, tx, plan, action, event)
	if err != nil {
		return domain.Progress{}, err
	}
	if err = tx.Commit(); err != nil {
		return domain.Progress{}, err
	}
	return next, nil
}

func advanceInTransaction(ctx context.Context, tx *sql.Tx, plan domain.PlanID, action domain.ActionID, event transition) (domain.Progress, error) {
	if event.Kind == "prepare" || event.Kind == "dispatch" {
		if err := guardGoalWork(ctx, tx, plan, event.Snapshot, event.Tick); err != nil {
			return domain.Progress{}, err
		}
	}
	state, err := load(ctx, tx, plan)
	if err != nil {
		return domain.Progress{}, err
	}
	var current domain.Progress
	found := false
	for _, p := range state.Progress {
		if p.View().Action == action {
			current = p
			found = true
			break
		}
	}
	if !found {
		return domain.Progress{}, ErrNotFound
	}
	for _, record := range state.Admissions {
		if record.Action != action {
			continue
		}
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("accounted work requires ReserveAndPrepare")
		}
		if event.Kind == "dispatch" && event.Tick < record.Admission.Tick {
			return domain.Progress{}, errors.New("dispatch predates latest admission observation")
		}
	}
	if err = guardDraftAdvance(ctx, tx, state, action, event); err != nil {
		return domain.Progress{}, err
	}
	if err = guardMeleeAdvance(ctx, tx, state, action, event); err != nil {
		return domain.Progress{}, err
	}
	next, err := apply(current, event)
	if err != nil {
		return domain.Progress{}, err
	}
	if event.Kind == "draft_begin" {
		cleanup, _ := next.View().DraftCleanup.Value()
		release, _ := cleanup.Release.Value()
		event.DraftBegin = &release
	}
	data, err := json.Marshal(event)
	if len(data) > 32768 {
		return domain.Progress{}, errors.New("transition exceeds bound")
	}
	if err != nil {
		return domain.Progress{}, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO transitions(action_id,payload) VALUES(?,?)", action, data); err != nil {
		return domain.Progress{}, err
	}
	return next, nil
}
func (s *Store) Prepare(ctx context.Context, plan domain.PlanID, action domain.ActionID, snapshot domain.GenerationSnapshot, tick domain.Tick) (domain.Progress, error) {
	return s.advance(ctx, plan, action, transition{Kind: "prepare", Snapshot: snapshot, Tick: tick})
}

// Dispatch authorizes a native call only on success. Any persistence error means
// the caller has no authorization to write; reopen and inspect before continuing.
func (s *Store) Dispatch(ctx context.Context, plan domain.PlanID, action domain.ActionID, snapshot domain.GenerationSnapshot, tick domain.Tick) (domain.Progress, error) {
	return s.advance(ctx, plan, action, transition{Kind: "dispatch", Snapshot: snapshot, Tick: tick})
}
func (s *Store) RecordReceipt(ctx context.Context, plan domain.PlanID, action domain.ActionID, attempt domain.AttemptID, receipt domain.Receipt) (domain.Progress, error) {
	return s.advance(ctx, plan, action, transition{Kind: "receipt", Attempt: attempt, Receipt: receipt})
}
func (s *Store) Observe(ctx context.Context, plan domain.PlanID, observation domain.Observation, current domain.GenerationSnapshot) (domain.Progress, error) {
	return s.advance(ctx, plan, observation.Action, transition{Kind: "observe", Snapshot: current, Observation: observation})
}
func (s *Store) Cancel(ctx context.Context, plan domain.PlanID, action domain.ActionID) (domain.Progress, error) {
	return s.advance(ctx, plan, action, transition{Kind: "cancel"})
}
