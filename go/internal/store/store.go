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

const schemaVersion = 41
const applicationID = 0x52474f31

var ErrConflict = errors.New("plan or action identity already exists")
var ErrNotFound = errors.New("plan or action not found")

type Store struct{ db *sql.DB }

// ControllerSessionID identifies one persistent controller execution namespace.
// It is independent of HTTP process sessions and survives controller restarts.
type ControllerSessionID string
type PlanState struct {
	BillAdmissions []ActionBillAdmission
	ZoneAdmissions []ActionZoneAdmission

	Retired               bool
	Spec                  domain.PlanSpec
	Progress              []domain.Progress
	Admissions            []ActionAdmission
	DraftAdmissions       []ActionDraftAdmission
	MeleeAdmissions       []ActionMeleeAdmission
	AcquisitionAdmissions []ActionAcquisitionAdmission
	SupplyAdmissions      []ActionSupplyAdmission
	WorkAdmissions        []ActionWorkAdmission
	TendAdmissions        []ActionTendAdmission
	RescueAdmissions      []ActionRescueAdmission
	RangedAdmissions      []ActionRangedAdmission
	HaulAdmissions        []ActionHaulAdmission
	EquipAdmissions       []ActionEquipAdmission
	GearReplaceAdmissions []ActionGearReplaceAdmission
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
	q.Add("_pragma", "journal_mode(WAL)")
	q.Add("_pragma", "synchronous(NORMAL)")
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
CREATE TABLE plans(id TEXT PRIMARY KEY, revision TEXT NOT NULL, retired INTEGER NOT NULL DEFAULT 0 CHECK(retired IN (0,1)));
CREATE INDEX active_plans ON plans(id) WHERE retired=0;
CREATE TABLE retirement_floors(colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, tick INTEGER NOT NULL CHECK(tick>=0), PRIMARY KEY(colony,load_token,map_id)) STRICT;
CREATE TABLE actions(id TEXT PRIMARY KEY, plan_id TEXT NOT NULL REFERENCES plans(id), ordinal INTEGER NOT NULL, kind TEXT NOT NULL CHECK(kind IN ('building','owned_draft','melee_attack','supply_allow','work_assignment','acquisition','zone_create','tend','rescue','ranged_attack','production_bill','haul','equip','gear_replace')), definition TEXT, x INTEGER, z INTEGER, rotation TEXT, stuff TEXT, pawn TEXT, target TEXT, draft_action TEXT REFERENCES actions(id), work_payload BLOB, zone_payload BLOB, bill_payload BLOB, CHECK((kind='production_bill' AND bill_payload IS NOT NULL) OR (kind<>'production_bill' AND bill_payload IS NULL)), CHECK((kind='zone_create' AND zone_payload IS NOT NULL) OR (kind!='zone_create' AND zone_payload IS NULL)), CHECK((kind='work_assignment' AND work_payload IS NOT NULL) OR (kind!='work_assignment' AND work_payload IS NULL)), CHECK((kind='production_bill' AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND target IS NULL AND draft_action IS NULL) OR (kind='zone_create' AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND target IS NULL AND draft_action IS NULL) OR (kind='building' AND definition IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NOT NULL AND stuff IS NOT NULL AND pawn IS NULL AND target IS NULL AND draft_action IS NULL) OR (kind='owned_draft' AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NULL AND draft_action IS NULL) OR (kind IN ('melee_attack','ranged_attack') AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NOT NULL) OR (kind IN ('supply_allow','acquisition') AND definition IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind='work_assignment' AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind IN ('tend','rescue') AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind='haul' AND definition IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind='equip' AND definition IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind='gear_replace' AND definition IS NOT NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NULL)), UNIQUE(plan_id,ordinal)) STRICT;
CREATE TABLE transitions(sequence INTEGER PRIMARY KEY, action_id TEXT NOT NULL REFERENCES actions(id), payload BLOB NOT NULL);
CREATE TABLE action_dependencies(plan_id TEXT NOT NULL REFERENCES plans(id), action_id TEXT NOT NULL REFERENCES actions(id), requires_id TEXT NOT NULL REFERENCES actions(id), PRIMARY KEY(plan_id,action_id,requires_id)) STRICT;
CREATE TABLE admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL);
CREATE TABLE draft_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE supply_claims(colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, thing TEXT NOT NULL, PRIMARY KEY(colony,load_token,map_id,thing)) STRICT;
CREATE TABLE bill_claims(colony TEXT NOT NULL,load_token TEXT NOT NULL,map_id INTEGER NOT NULL,bench TEXT NOT NULL,recipe TEXT NOT NULL,PRIMARY KEY(colony,load_token,map_id,bench,recipe)) STRICT;
CREATE TABLE bill_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id),payload BLOB NOT NULL) STRICT;
CREATE TABLE zone_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE work_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE acquisition_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE supply_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE melee_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE tend_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE rescue_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE ranged_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE haul_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE equip_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE gear_replace_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE clock_attempts(request_id TEXT PRIMARY KEY, native_action_id TEXT NOT NULL UNIQUE, payload BLOB NOT NULL, phase TEXT NOT NULL CHECK(phase IN ('prepared','dispatched','uncertain','applied','refused')), reply BLOB, scope_context BLOB) STRICT;
CREATE TABLE clock_epochs(start_request_id TEXT PRIMARY KEY REFERENCES clock_attempts(request_id), stage TEXT NOT NULL CHECK(stage IN ('required','pausing','uncertain','paused','retired','superseded')), sequence TEXT NOT NULL, context BLOB, status BLOB) STRICT;
CREATE TABLE submissions(request_id TEXT PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN ('building','owned_draft')), colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, plan_id TEXT NOT NULL UNIQUE REFERENCES plans(id), action_id TEXT NOT NULL UNIQUE REFERENCES actions(id), revision TEXT NOT NULL) STRICT;
CREATE TABLE draft_submissions(request_id TEXT PRIMARY KEY REFERENCES submissions(request_id), pawn TEXT NOT NULL) STRICT;
CREATE INDEX action_transitions ON transitions(action_id,sequence);
CREATE TABLE building_submissions(request_id TEXT PRIMARY KEY REFERENCES submissions(request_id), definition TEXT NOT NULL, x INTEGER NOT NULL, z INTEGER NOT NULL, rotation TEXT NOT NULL, stuff TEXT NOT NULL) STRICT;
CREATE TABLE work_preferences(plan_id TEXT PRIMARY KEY REFERENCES plans(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE work_preference_requests(request_id TEXT PRIMARY KEY, payload BLOB NOT NULL) STRICT;`)
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
	for _, query := range []string{"SELECT action_id,payload FROM draft_admissions LIMIT 0", "SELECT action_id,payload FROM melee_admissions LIMIT 0", "SELECT action_id,payload FROM tend_admissions LIMIT 0", "SELECT action_id,payload FROM rescue_admissions LIMIT 0", "SELECT action_id,payload FROM ranged_admissions LIMIT 0", "SELECT action_id,payload FROM haul_admissions LIMIT 0", "SELECT action_id,payload FROM equip_admissions LIMIT 0", "SELECT action_id,payload FROM gear_replace_admissions LIMIT 0", "SELECT request_id,kind,colony,load_token,map_id,plan_id,action_id,revision FROM submissions LIMIT 0", "SELECT request_id,pawn FROM draft_submissions LIMIT 0"} {
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
	if err := plan.Validate(); err != nil {
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
	if err := plan.Validate(); err != nil {
		return err
	}
	var count int
	if err := tx.QueryRowContext(ctx, "SELECT count(*) FROM plans WHERE retired=0").Scan(&count); err != nil {
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
	for _, d := range plan.Dependencies() {
		if _, err := tx.ExecContext(ctx, "INSERT INTO action_dependencies(plan_id,action_id,requires_id) VALUES(?,?,?)", plan.ID(), d.Action, d.Requires); err != nil {
			return conflict(err)
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
	var retired bool
	if err := tx.QueryRowContext(ctx, "SELECT revision,retired FROM plans WHERE id=?", id).Scan(&revision, &retired); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return PlanState{}, ErrNotFound
		}
		return PlanState{}, err
	}
	r, err := strconv.ParseUint(revision, 10, 64)
	if err != nil {
		return PlanState{}, err
	}
	rows, err := tx.QueryContext(ctx, "SELECT id,kind,definition,x,z,rotation,stuff,pawn,target,draft_action,work_payload,zone_payload,bill_payload,ordinal FROM actions WHERE plan_id=? ORDER BY ordinal", id)
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
	dependencies, err := loadDependencies(ctx, tx, id)
	if err != nil {
		return PlanState{}, err
	}
	plan, err := domain.NewPlan(id, domain.PlanRevision(r), actions, dependencies...)
	if err != nil {
		return PlanState{}, err
	}
	state := PlanState{Retired: retired, Spec: plan, Progress: make([]domain.Progress, 0, len(actions))}
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
		acquisitionAdmission, acquisitionPresent, e := loadAcquisitionAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.AcquisitionAction && !acquisitionPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("acquisition progress lacks admission")
		}
		if acquisitionPresent {
			state.AcquisitionAdmissions = append(state.AcquisitionAdmissions, ActionAcquisitionAdmission{Action: a.ID(), Admission: acquisitionAdmission})
		}
		supplyAdmission, supplyPresent, e := loadSupplyAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.SupplyAllowAction && !supplyPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("supply progress lacks admission")
		}
		if supplyPresent {
			state.SupplyAdmissions = append(state.SupplyAdmissions, ActionSupplyAdmission{Action: a.ID(), Admission: supplyAdmission})
		}
		billAdmission, billPresent, e := loadBillAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.ProductionBillAction && !billPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("bill progress lacks admission")
		}
		if billPresent {
			state.BillAdmissions = append(state.BillAdmissions, ActionBillAdmission{Action: a.ID(), Admission: billAdmission})
		}
		zoneAdmission, zonePresent, e := loadZoneAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.ZoneCreateAction && !zonePresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("zone progress lacks admission")
		}
		if zonePresent {
			state.ZoneAdmissions = append(state.ZoneAdmissions, ActionZoneAdmission{Action: a.ID(), Admission: zoneAdmission})
		}
		workAdmission, workPresent, e := loadWorkAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.WorkAssignmentAction && !workPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("work progress lacks admission")
		}
		if workPresent {
			state.WorkAdmissions = append(state.WorkAdmissions, ActionWorkAdmission{Action: a.ID(), Admission: workAdmission})
		}

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
		tendAdmission, tendPresent, e := loadTendAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.TendAction && !tendPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("tend progress lacks admission")
		}
		if tendPresent {
			state.TendAdmissions = append(state.TendAdmissions, ActionTendAdmission{Action: a.ID(), Admission: tendAdmission})
		}
		rescueAdmission, rescuePresent, e := loadRescueAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.RescueAction && !rescuePresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("rescue progress lacks admission")
		}
		if rescuePresent {
			state.RescueAdmissions = append(state.RescueAdmissions, ActionRescueAdmission{Action: a.ID(), Admission: rescueAdmission})
		}
		rangedAdmission, rangedPresent, e := loadRangedAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.RangedAttackAction && !rangedPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("ranged attack progress lacks admission")
		}
		if rangedPresent {
			state.RangedAdmissions = append(state.RangedAdmissions, ActionRangedAdmission{Action: a.ID(), Admission: rangedAdmission})
		}
		haulAdmission, haulPresent, e := loadHaulAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.HaulAction && !haulPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("haul progress lacks admission")
		}
		if haulPresent {
			state.HaulAdmissions = append(state.HaulAdmissions, ActionHaulAdmission{Action: a.ID(), Admission: haulAdmission})
		}
		equipAdmission, equipPresent, e := loadEquipAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.EquipAction && !equipPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("equip progress lacks admission")
		}
		if equipPresent {
			state.EquipAdmissions = append(state.EquipAdmissions, ActionEquipAdmission{Action: a.ID(), Admission: equipAdmission})
		}
		gearReplaceAdmission, gearReplacePresent, e := loadGearReplaceAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.GearReplaceAction && !gearReplacePresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("gear replace progress lacks admission")
		}
		if gearReplacePresent {
			state.GearReplaceAdmissions = append(state.GearReplaceAdmissions, ActionGearReplaceAdmission{Action: a.ID(), Admission: gearReplaceAdmission})
		}
	}
	for _, record := range state.MeleeAdmissions {
		if err := validateMeleePrerequisite(ctx, tx, state, record.Action, record.Admission, false); err != nil {
			return PlanState{}, err
		}
	}
	for _, record := range state.RangedAdmissions {
		if err := validateRangedPrerequisite(ctx, tx, state, record.Action, record.Admission, false); err != nil {
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
	if state.Retired {
		return domain.Progress{}, errors.New("retired plan is read-only")
	}
	if event.Kind == "prepare" || event.Kind == "dispatch" {
		if err = state.Spec.CheckDependencies(action, state.Progress, event.Snapshot, event.Tick); err != nil {
			return domain.Progress{}, err
		}
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
	if current.Action().Kind() == domain.AcquisitionAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("acquisition requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.AcquisitionAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("acquisition dispatch lacks current admission")
			}
		}
	}
	if current.Action().Kind() == domain.SupplyAllowAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("supply requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.SupplyAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("supply dispatch lacks current admission")
			}
		}
	}
	if current.Action().Kind() == domain.ProductionBillAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("bill requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.BillAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("bill dispatch lacks admission")
			}
		}
		// A trusted refusal proves no bill was created, leaving the bench+recipe pair
		// claimable again; only an accepted or uncertain write may have produced one.
		if event.Kind == "receipt" && event.Receipt != domain.ReceiptRefused {
			bill, _ := current.Action().ProductionBill()
			snapshot := current.View().Snapshot
			// A reopened retry (e.g. an uncertain write later observed absent) claims the
			// same bench+recipe again; that is not a real conflict.
			if _, err := tx.ExecContext(ctx, "INSERT INTO bill_claims(colony,load_token,map_id,bench,recipe) VALUES(?,?,?,?,?) ON CONFLICT(colony,load_token,map_id,bench,recipe) DO NOTHING", snapshot.Colony, snapshot.Load, snapshot.Map, bill.Bench(), bill.Recipe()); err != nil {
				return domain.Progress{}, conflict(err)
			}
		}
	}
	if current.Action().Kind() == domain.ZoneCreateAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("zone requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.ZoneAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("zone dispatch lacks current admission")
			}
		}
	}
	if current.Action().Kind() == domain.WorkAssignmentAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("work requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.WorkAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("work dispatch lacks current admission")
			}
		}
	}
	if current.Action().Kind() == domain.TendAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("tend requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.TendAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("tend dispatch lacks current admission")
			}
		}
	}
	if current.Action().Kind() == domain.RescueAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("rescue requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.RescueAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("rescue dispatch lacks current admission")
			}
		}
	}
	if current.Action().Kind() == domain.HaulAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("haul requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.HaulAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("haul dispatch lacks current admission")
			}
		}
	}
	if current.Action().Kind() == domain.EquipAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("equip requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.EquipAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("equip dispatch lacks current admission")
			}
		}
	}
	if current.Action().Kind() == domain.GearReplaceAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("gear replace requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.GearReplaceAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("gear replace dispatch lacks current admission")
			}
		}
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
	if err = guardRangedAdvance(ctx, tx, state, action, event); err != nil {
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
