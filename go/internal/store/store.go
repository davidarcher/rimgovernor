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
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store/clock"
	"github.com/davidarcher/RimGovernor/go/internal/store/core"
	"modernc.org/sqlite"
)

const schemaVersion = 191

// SchemaVersion is the PRAGMA user_version Open requires; a database
// from another version is refused (tooling reads those raw).
func SchemaVersion() int { return schemaVersion }

const applicationID = 0x52474f31

var ErrConflict = core.ErrConflict
var ErrNotAdmitted = core.ErrNotAdmitted

// ErrActionVetoed is an action Rule's dispatch refusal (#1018).
var ErrActionVetoed = errors.New("action vetoed")
var ErrNotFound = core.ErrNotFound

type Store struct {
	db     *sql.DB
	floors *retirementFloors
	// submissions is the session-only goal-create replay cache (#1011).
	submissions goalCreateSubmissions
	// goalsWritten wakes the governor-state mirror after a commit that may
	// have created a goal (#1362), so a restart does not lose it.
	goalsWritten chan struct{}
}

// GoalsWritten fires (coalesced) after a commit that may have created a
// goal; the governor-state mirror puts it without waiting for its tick.
func (s *Store) GoalsWritten() <-chan struct{} { return s.goalsWritten }

func (s *Store) notifyGoalsWritten() {
	select {
	case s.goalsWritten <- struct{}{}:
	default:
	}
}

// ControllerSessionID identifies one persistent controller execution namespace.
// It is independent of HTTP process sessions and survives controller restarts.
type ControllerSessionID = core.ControllerSessionID
type PlanState struct {
	Retired bool
	// Method is the goal or incident method id the plan was admitted
	// under (#987), empty for a plan no method binds. It outlives the
	// goal_methods row, which RebuildGoals clears on a world's first round.
	Method     domain.MethodID
	Spec       domain.PlanSpec
	Progress   []domain.Progress
	Admissions []ActionAdmission
}

// Open accepts a filesystem path, never a caller-supplied SQLite connection URI.
// MemoryPrefix names an in-memory database under `go test`: Open accepts
// "file:<name>?mode=memory&cache=shared" only while testing, where the
// per-fixture file creation, WAL/shm files and lock syscalls of a real
// database are the dominant cost on Windows and the thing that balloons
// when several suites compete for the disk. Production always takes a
// path and always gets WAL.
const MemoryPrefix = "file:"

func Open(ctx context.Context, path string) (*Store, error) {
	if path == "" {
		return nil, errors.New("database path is empty")
	}
	memory := testing.Testing() && strings.HasPrefix(path, MemoryPrefix)
	var u *url.URL
	if memory {
		parsed, err := url.Parse(path)
		if err != nil || parsed.Query().Get("mode") != "memory" || parsed.Query().Get("cache") != "shared" {
			return nil, fmt.Errorf("in-memory database %q must be file:<name>?mode=memory&cache=shared", path)
		}
		u = parsed
	} else {
		var err error
		if u, err = fileURL(path); err != nil {
			return nil, err
		}
	}
	q := u.Query()
	q.Set("_txlock", "immediate")
	q.Set("_busy_timeout", "25")
	q.Add("_pragma", "foreign_keys(1)")
	q.Add("_pragma", "synchronous("+syncMode()+")")
	u.RawQuery = q.Encode()
	db, err := sql.Open(DriverName, u.String())
	if err != nil {
		return nil, err
	}
	db.SetMaxOpenConns(1)
	db.SetMaxIdleConns(1)
	// WAL mode is persistent in the file, so it is switched here, after the
	// per-connection synchronous setting applies, rather than as a URI pragma
	// (the driver runs those in lexicographic order, so the mode switch would
	// fsync under the default FULL setting on every fresh database).
	var journal string
	if err = retryBusy(ctx, func() error { return db.QueryRowContext(ctx, "PRAGMA journal_mode=WAL").Scan(&journal) }); err != nil {
		_ = db.Close()
		return nil, err
	}
	// An in-memory database cannot be WAL and reports "memory": the single
	// connection above is what keeps it alive, so it has no reopen semantics
	// either. Tests that reopen by path or hold a second handle use a file.
	if want := "wal"; !strings.EqualFold(journal, want) && !(memory && strings.EqualFold(journal, "memory")) {
		_ = db.Close()
		return nil, fmt.Errorf("journal mode %q, want %s", journal, want)
	}
	s := &Store{db: db, floors: floorsFor(path), goalsWritten: make(chan struct{}, 1)}
	if err = s.initialize(ctx); err != nil {
		_ = db.Close()
		return nil, err
	}
	return s, nil
}
func (s *Store) Close() error { return s.db.Close() }

// ErrIncompatible is Open's refusal of a database another schema version
// or application wrote.
var ErrIncompatible = errors.New("incompatible database application/version")

// OpenOrReplace is Open for the service: a database from another schema
// version is not migrated (saves regenerate) but moved aside, with its
// WAL and shared-memory files, to <path>.v<old>.bak[.n], and a fresh one
// is created in its place. It returns the moved-aside path, or "" when
// the database opened as it was.
func OpenOrReplace(ctx context.Context, path string) (*Store, string, error) {
	s, err := Open(ctx, path)
	if err == nil || !errors.Is(err, ErrIncompatible) {
		return s, "", err
	}
	found := strings.TrimSpace(strings.TrimPrefix(err.Error(), ErrIncompatible.Error()+":"))
	old := path + ".v" + strings.ReplaceAll(found, "/", "-") + ".bak"
	aside := old
	for n := 1; ; n++ {
		if _, statErr := os.Stat(aside); errors.Is(statErr, os.ErrNotExist) {
			break
		}
		aside = fmt.Sprintf("%s.%d", old, n)
	}
	if err = os.Rename(path, aside); err != nil {
		return nil, "", err
	}
	for _, suffix := range []string{"-wal", "-shm"} {
		if renameErr := os.Rename(path+suffix, aside+suffix); renameErr != nil && !errors.Is(renameErr, os.ErrNotExist) {
			return nil, "", renameErr
		}
	}
	s, err = Open(ctx, path)
	return s, aside, err
}

// fileURL is the file: URI SQLite opens the database at path through.
func fileURL(path string) (*url.URL, error) {
	abs, err := filepath.Abs(path)
	if err != nil {
		return nil, err
	}
	uriPath := filepath.ToSlash(abs)
	if !strings.HasPrefix(uriPath, "/") {
		uriPath = "/" + uriPath
	}
	return &url.URL{Scheme: "file", Path: uriPath}, nil
}

// Snapshot copies the database into a new file at path through SQLite's
// online backup (CopyPages), consistent even while another process holds
// it open: an acceptance checkpoint's copy of a service's durable state.
func (s *Store) Snapshot(ctx context.Context, path string) error {
	if _, err := os.Stat(path); err == nil {
		return fmt.Errorf("snapshot: %s already exists", path)
	}
	u, err := fileURL(path)
	if err != nil {
		return err
	}
	return CopyPages(ctx, s.db, u.String())
}

// syncMode keeps WAL durability in production. Under `go test` every test opens
// a fresh database, and the per-commit and close-time fsyncs dominate the
// suite's wall clock on Windows without verifying anything the tests assert
// (crash durability needs a killed process, not a returned Commit).
func syncMode() string {
	if testing.Testing() {
		return "OFF"
	}
	return "NORMAL"
}

func (s *Store) begin(ctx context.Context) (*sql.Tx, error) {
	var tx *sql.Tx
	err := retryBusy(ctx, func() (err error) {
		tx, err = s.db.BeginTx(ctx, nil)
		return err
	})
	return tx, err
}

// retryBusy repeats operation while it fails with SQLITE_BUSY or SQLITE_LOCKED.
func retryBusy(ctx context.Context, operation func() error) error {
	for {
		err := operation()
		if err == nil {
			return nil
		}
		var sqliteErr *sqlite.Error
		if !errors.As(err, &sqliteErr) || (sqliteErr.Code()&255 != 5 && sqliteErr.Code()&255 != 6) {
			return err
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
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
CREATE TABLE plans(id TEXT PRIMARY KEY, revision TEXT NOT NULL, method_id TEXT, retired INTEGER NOT NULL DEFAULT 0 CHECK(retired IN (0,1)));
CREATE INDEX active_plans ON plans(id) WHERE retired=0;
CREATE TABLE actions(id TEXT PRIMARY KEY, plan_id TEXT NOT NULL REFERENCES plans(id), ordinal INTEGER NOT NULL, kind TEXT NOT NULL CHECK(kind IN ('building','owned_draft','subdue','supply_allow','supply_forbid','work_assignment','acquisition','zone_create','tend','rescue','capture','production_bill','haul','equip','gear_replace','apparel_policy','repair','clean','waste','recovery_service','movement','research_select','husbandry','home_coverage','prisoner_interaction','quest_accept','ritual','ability','ignite','royalty','mine_acquisition','wall_removal','excavation','building_temperature','bed_medical','grower_crop','assign','mood_relief','dialog_answer','naming_confirmation','cut_plant','cover_clearance','deconstruction','trade','claim_building','auto_refuel','surgery','auto_home_area','zone_delete','zone_cell_edit','stockpile_patch','open_casket','caravan_departure','move_building','uninstall_building','foundation_removal','floor_removal','use_item','strip','drop_equipment','acquisition_withdraw','area','pawn_settings','policy_prune','remove_roof','reading_policy','drug_policy','food_policy','area_plant_cut','wastepack_haul')), definition TEXT, x INTEGER, z INTEGER, rotation TEXT, stuff TEXT, pawn TEXT, target TEXT, draft_action TEXT REFERENCES actions(id), work_payload BLOB, zone_payload BLOB, bill_payload BLOB, wall_removal_payload BLOB, building_temperature_payload BLOB, mood_relief_payload BLOB, trade_payload BLOB, caravan_payload BLOB, ritual_payload BLOB, CHECK(ritual_payload IS NULL OR kind='ritual'), CHECK((kind='trade' AND trade_payload IS NOT NULL) OR (kind<>'trade' AND trade_payload IS NULL)), CHECK((kind='caravan_departure' AND caravan_payload IS NOT NULL) OR (kind<>'caravan_departure' AND caravan_payload IS NULL)), CHECK((kind='building_temperature' AND building_temperature_payload IS NOT NULL) OR (kind<>'building_temperature' AND building_temperature_payload IS NULL)), CHECK((kind='mood_relief' AND mood_relief_payload IS NOT NULL) OR (kind<>'mood_relief' AND mood_relief_payload IS NULL)), CHECK((kind='production_bill' AND bill_payload IS NOT NULL) OR (kind<>'production_bill' AND bill_payload IS NULL)), CHECK((kind IN ('zone_create','zone_cell_edit','stockpile_patch','area','policy_prune','remove_roof','reading_policy','drug_policy','food_policy','area_plant_cut') AND zone_payload IS NOT NULL) OR (kind NOT IN ('zone_create','zone_cell_edit','stockpile_patch','area','policy_prune','remove_roof','reading_policy','drug_policy','food_policy','area_plant_cut') AND zone_payload IS NULL) OR kind='deconstruction'), CHECK((kind='work_assignment' AND work_payload IS NOT NULL) OR (kind!='work_assignment' AND work_payload IS NULL)), CHECK((kind='wall_removal' AND wall_removal_payload IS NOT NULL) OR (kind<>'wall_removal' AND wall_removal_payload IS NULL)), CHECK((kind IN ('trade','caravan_departure') AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND target IS NULL AND draft_action IS NULL) OR (kind='production_bill' AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND target IS NULL AND draft_action IS NULL) OR (kind='zone_create' AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND target IS NULL AND draft_action IS NULL) OR (kind='wall_removal' AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND target IS NULL AND draft_action IS NULL) OR (kind IN ('excavation','foundation_removal','floor_removal') AND definition IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND target IS NULL AND draft_action IS NULL) OR (kind='building' AND definition IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NOT NULL AND stuff IS NOT NULL AND pawn IS NULL AND target IS NULL AND draft_action IS NULL) OR (kind='use_item' AND definition IS NOT NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind='owned_draft' AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NULL AND draft_action IS NULL) OR (kind='subdue' AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NOT NULL) OR (kind IN ('supply_allow','supply_forbid','acquisition','acquisition_withdraw','mine_acquisition','cut_plant') AND definition IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind='deconstruction' AND definition IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NULL AND (stuff IS NULL OR stuff='swap_wall') AND pawn IS NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind='cover_clearance' AND definition IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NULL AND stuff IN ('Mine','CutPlant','Haul','Deconstruct') AND pawn IS NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind='work_assignment' AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NULL AND draft_action IS NULL) OR (kind IN ('tend','rescue','capture','drop_equipment') AND (definition IS NULL OR kind='capture') AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind='haul' AND definition IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind='equip' AND definition IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind='gear_replace' AND definition IS NOT NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind IN ('repair','clean','waste','open_casket') AND definition IS NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind IN ('recovery_service') AND definition IS NOT NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind='movement' AND definition IS NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NULL AND draft_action IS NOT NULL) OR (kind='research_select' AND definition IS NOT NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND target IS NULL AND draft_action IS NULL) OR (kind='husbandry' AND definition IN ('train','slaughter','prioritize_slaughter','tame','release','cancel_slaughter','cancel_release','sterilize','allowed_area','master','follow_drafted','follow_fieldwork') AND target IS NOT NULL AND pawn IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND draft_action IS NULL AND ((definition IN ('train','prioritize_slaughter') AND stuff IS NOT NULL) OR (definition IN ('slaughter','tame','release','cancel_slaughter','cancel_release','sterilize') AND stuff IS NULL) OR (definition IN ('allowed_area','master')) OR (definition IN ('follow_drafted','follow_fieldwork') AND stuff IN ('true','false')))) OR (kind='home_coverage' AND definition IS NOT NULL AND target IS NOT NULL AND pawn IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND draft_action IS NULL) OR (kind='prisoner_interaction' AND definition IN ('recruit','maintain') AND target IS NOT NULL AND pawn IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND draft_action IS NULL) OR (kind='ignite' AND pawn IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND definition IS NULL AND rotation IS NULL AND stuff IS NULL AND target IS NULL AND draft_action IS NULL) OR (kind='ability' AND pawn IS NOT NULL AND definition IS NOT NULL AND rotation IS NULL AND stuff IS NULL AND draft_action IS NULL AND (x IS NULL) = (z IS NULL) AND (x IS NULL OR target IS NULL)) OR (kind='royalty' AND pawn IS NOT NULL AND target IS NOT NULL AND definition IS NOT NULL AND stuff IN ('choose_permit') AND x IS NULL AND z IS NULL AND rotation IS NULL AND draft_action IS NULL) OR (kind='ritual' AND pawn IS NOT NULL AND target IS NULL AND rotation IS NULL AND draft_action IS NULL AND ((definition IN ('bestowing') AND stuff='start' AND x IS NULL AND z IS NULL AND ritual_payload IS NULL) OR (definition IS NOT NULL AND stuff='begin' AND x IS NOT NULL AND z IS NOT NULL AND ritual_payload IS NOT NULL))) OR (kind='quest_accept' AND target IS NOT NULL AND definition IS NOT NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND draft_action IS NULL) OR (kind='building_temperature' AND target IS NOT NULL AND definition IS NULL AND pawn IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND draft_action IS NULL) OR (kind='bed_medical' AND target IS NOT NULL AND definition IN ('true','false','prisoners') AND stuff IS NULL AND pawn IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND draft_action IS NULL) OR (kind='grower_crop' AND target IS NOT NULL AND definition IS NOT NULL AND stuff IS NULL AND pawn IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND draft_action IS NULL) OR (kind='assign' AND pawn IS NOT NULL AND target IS NOT NULL AND definition IS NOT NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND (stuff IS NULL OR stuff='swap') AND draft_action IS NULL) OR (kind='mood_relief' AND pawn IS NOT NULL AND definition IS NULL AND target IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND draft_action IS NULL) OR (kind='apparel_policy' AND definition IS NOT NULL AND x IS NULL AND z IS NULL AND pawn IS NULL AND target IS NULL AND rotation IS NULL AND stuff IS NULL AND draft_action IS NULL) OR (kind='dialog_answer' AND definition IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND pawn IS NULL AND target IS NULL AND rotation IS NULL AND draft_action IS NULL) OR (kind='naming_confirmation' AND definition IS NOT NULL AND stuff IS NOT NULL AND x IS NOT NULL AND z IS NULL AND pawn IS NULL AND target IS NULL AND rotation IS NULL AND draft_action IS NULL) OR (kind IN ('zone_cell_edit','stockpile_patch','zone_delete') AND target IS NOT NULL AND stuff IS NULL AND definition IS NULL AND pawn IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND draft_action IS NULL) OR (kind='surgery' AND pawn IS NOT NULL AND definition IS NOT NULL AND x IS NOT NULL AND stuff IN ('true','false') AND z IS NULL AND rotation IS NULL AND draft_action IS NULL) OR (kind='auto_home_area' AND definition IN ('true','false') AND target IS NULL AND stuff IS NULL AND pawn IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND draft_action IS NULL) OR (kind='auto_refuel' AND target IS NOT NULL AND definition IN ('true','false') AND stuff IS NULL AND pawn IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND draft_action IS NULL) OR (kind='claim_building' AND target IS NOT NULL AND stuff IS NULL AND definition IS NULL AND pawn IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND draft_action IS NULL) OR (kind='strip' AND target IS NOT NULL AND definition IS NULL AND pawn IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND draft_action IS NULL) OR (kind IN ('move_building','uninstall_building') AND target IS NOT NULL AND definition IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NOT NULL AND stuff IS NULL AND pawn IS NULL AND draft_action IS NULL) OR (kind='wastepack_haul' AND definition IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind='area' AND definition IN ('create','set_cells','clear_cells','delete') AND x IS NULL AND z IS NULL AND rotation IS NULL AND (stuff IS NULL OR stuff='pollution_clear') AND pawn IS NULL AND draft_action IS NULL OR (kind='pawn_settings' AND pawn IS NOT NULL AND (definition IN ('Ignore','Attack','Flee','self_tend:true','self_tend:false','medicine_carry:0','medicine_carry:1','medicine_carry:2','medicine_carry:3','NoCare','NoMeds','HerbalOrWorse','NormalOrWorse','Best') OR definition LIKE 'nickname:_%' OR definition LIKE 'reading_policy:_%' OR definition LIKE 'drug_policy:_%' OR definition LIKE 'food_policy:_%' OR definition LIKE 'mech_work_mode:_%' OR definition LIKE 'mech_control_group:_%') AND target IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND draft_action IS NULL) OR (kind='policy_prune' AND definition IN ('outfit','drug','food','reading','allowed_area') AND target IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND draft_action IS NULL) OR (kind IN ('reading_policy','food_policy') AND definition IS NOT NULL AND target IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND draft_action IS NULL) OR (kind='drug_policy' AND definition IS NOT NULL AND target IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND draft_action IS NULL) OR (kind IN ('remove_roof','area_plant_cut') AND definition IS NULL AND target IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND draft_action IS NULL))), UNIQUE(plan_id,ordinal)) STRICT;
CREATE TABLE transitions(sequence INTEGER PRIMARY KEY, action_id TEXT NOT NULL REFERENCES actions(id), payload BLOB NOT NULL);
CREATE TABLE action_dependencies(plan_id TEXT NOT NULL REFERENCES plans(id), action_id TEXT NOT NULL REFERENCES actions(id), requires_id TEXT NOT NULL REFERENCES actions(id), coupled INTEGER NOT NULL DEFAULT 0 CHECK(coupled IN (0,1)), PRIMARY KEY(plan_id,action_id,requires_id)) STRICT;
CREATE TABLE admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL);
CREATE TABLE bill_claims(colony TEXT NOT NULL,load_token TEXT NOT NULL,map_id INTEGER NOT NULL,bench TEXT NOT NULL,recipe TEXT NOT NULL,PRIMARY KEY(colony,load_token,map_id,bench,recipe)) STRICT;
CREATE TABLE clock_attempts(request_id TEXT PRIMARY KEY, native_action_id TEXT NOT NULL UNIQUE, payload BLOB NOT NULL, phase TEXT NOT NULL CHECK(phase IN ('prepared','dispatched','uncertain','applied','refused')), reply BLOB, scope_context BLOB) STRICT;
CREATE TABLE clock_epochs(start_request_id TEXT PRIMARY KEY REFERENCES clock_attempts(request_id), stage TEXT NOT NULL CHECK(stage IN ('required','pausing','uncertain','paused','retired','superseded')), sequence TEXT NOT NULL, context BLOB, status BLOB) STRICT;
CREATE TABLE submissions(request_id TEXT PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN ('building','research_select')), colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, plan_id TEXT NOT NULL UNIQUE REFERENCES plans(id), action_id TEXT NOT NULL UNIQUE REFERENCES actions(id), revision TEXT NOT NULL) STRICT;
CREATE INDEX action_transitions ON transitions(action_id,sequence);
CREATE TABLE building_submissions(request_id TEXT PRIMARY KEY REFERENCES submissions(request_id), definition TEXT NOT NULL, x INTEGER NOT NULL, z INTEGER NOT NULL, rotation TEXT NOT NULL, stuff TEXT NOT NULL) STRICT;
CREATE TABLE research_select_submissions(request_id TEXT PRIMARY KEY REFERENCES submissions(request_id), payload BLOB NOT NULL) STRICT;
CREATE TABLE population_decision_submissions(request_id TEXT PRIMARY KEY, colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, pawn TEXT NOT NULL, decision TEXT NOT NULL CHECK(decision IN ('rescue','capture','recruit','ignore'))) STRICT;
CREATE TABLE population_decisions(colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, pawn TEXT NOT NULL, request_id TEXT NOT NULL REFERENCES population_decision_submissions(request_id), decision TEXT NOT NULL CHECK(decision IN ('rescue','capture','recruit','ignore')), PRIMARY KEY(colony,load_token,map_id,pawn)) STRICT;`)
		if err != nil {
			return err
		}
		if err = clock.InitializeReview(ctx, tx); err != nil {
			return err
		}
		if err = initializeGoals(ctx, tx); err != nil {
			return err
		}
		if err = clock.InitializeInbox(ctx, tx); err != nil {
			return err
		}
		if err = clock.InitializeSequence(ctx, tx); err != nil {
			return err
		}
		if err = clock.InitializeCheckpoint(ctx, tx); err != nil {
			return err
		}
		if err = initializeColonyExtent(ctx, tx); err != nil {
			return err
		}
		if err = initializeLayoutPlan(ctx, tx); err != nil {
			return err
		}
		if err = initializeLayoutTidies(ctx, tx); err != nil {
			return err
		}
		if err = initializeCombatFights(ctx, tx); err != nil {
			return err
		}
		var entropy [32]byte
		if _, err = tx.ExecContext(ctx, `CREATE TABLE control_intents(request_id TEXT PRIMARY KEY, kind TEXT NOT NULL, colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, phase TEXT NOT NULL, native_generation TEXT NOT NULL) STRICT`); err != nil {
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
		return fmt.Errorf("%w: %d/%d", ErrIncompatible, app, version)
	}
	for _, query := range []string{"SELECT request_id,kind,colony,load_token,map_id,plan_id,action_id,revision FROM submissions LIMIT 0"} {
		if version != 0 {
			if _, err = tx.ExecContext(ctx, query); err != nil {
				return err
			}
		}
	}
	if err = clock.CheckReviewSchema(ctx, tx); err != nil {
		return err
	}
	if err = clock.CheckInboxSchema(ctx, tx); err != nil {
		return err
	}
	if err = clock.CheckSequenceSchema(ctx, tx); err != nil {
		return err
	}
	if err = clock.CheckCheckpointSchema(ctx, tx); err != nil {
		return err
	}
	if err = checkColonyExtentSchema(ctx, tx); err != nil {
		return err
	}
	if err = checkLayoutPlanSchema(ctx, tx); err != nil {
		return err
	}
	if err = checkLayoutTidySchema(ctx, tx); err != nil {
		return err
	}
	if err = checkCombatFightsSchema(ctx, tx); err != nil {
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
	if _, err = tx.ExecContext(ctx, "SELECT request_id,payload FROM research_select_submissions LIMIT 0"); err != nil {
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
	return core.Identity(ctx, tx)
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
		if _, err := tx.ExecContext(ctx, "INSERT INTO action_dependencies(plan_id,action_id,requires_id,coupled) VALUES(?,?,?,?)", plan.ID(), d.Action, d.Requires, d.Coupled); err != nil {
			return conflict(err)
		}
	}
	return nil
}

func conflict(err error) error {
	return core.Conflict(err)
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
	var method sql.NullString
	if err := tx.QueryRowContext(ctx, "SELECT revision,retired,method_id FROM plans WHERE id=?", id).Scan(&revision, &retired, &method); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return PlanState{}, ErrNotFound
		}
		return PlanState{}, err
	}
	r, err := strconv.ParseUint(revision, 10, 64)
	if err != nil {
		return PlanState{}, err
	}
	rows, err := tx.QueryContext(ctx, "SELECT id,kind,definition,x,z,rotation,stuff,pawn,target,draft_action,work_payload,zone_payload,bill_payload,wall_removal_payload,building_temperature_payload,mood_relief_payload,trade_payload,caravan_payload,ritual_payload,ordinal FROM actions WHERE plan_id=? ORDER BY ordinal", id)
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
	state := PlanState{Retired: retired, Method: domain.MethodID(method.String), Spec: plan, Progress: make([]domain.Progress, 0, len(actions))}
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
			p, e = apply(p, event)
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
		if present {
			state.Admissions = append(state.Admissions, ActionAdmission{Action: a.ID(), Admission: admission})
		}
	}
	return state, nil
}

// transition is a private persistence boundary, not a second progress model.
type transition struct {
	Kind        string
	floors      *retirementFloors // set by the Store for a prepare or dispatch
	Snapshot    domain.GenerationSnapshot
	Tick        domain.Tick
	Attempt     domain.AttemptID
	Receipt     domain.Receipt
	Observation domain.Observation
	Zone        string              `json:",omitempty"`
	HeldReasons []domain.HeldReason `json:",omitempty"`
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
	switch e.Kind {
	case "prepare":
		return p.Prepare(e.Snapshot, e.Tick)
	case "dispatch":
		return p.MarkDispatched(e.Snapshot, e.Tick)
	case "receipt":
		if e.Zone != "" {
			return p.RecordZoneReceipt(e.Attempt, e.Receipt, e.Zone)
		}
		return p.RecordReceipt(e.Attempt, e.Receipt)
	case "observe":
		return p.Observe(e.Observation, e.Snapshot)
	case "hold":
		return p.Hold(e.HeldReasons, e.Tick)
	case "cancel":
		return p.Cancel()
	case "withdraw":
		return p.Withdraw(e.Snapshot, e.Tick)
	default:
		return p, fmt.Errorf("unknown transition %q", e.Kind)
	}
}
func (s *Store) advance(ctx context.Context, plan domain.PlanID, action domain.ActionID, event transition) (domain.Progress, error) {
	event.floors = s.floors
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
		if err := guardGoalWork(ctx, tx, event.floors, plan, event.Snapshot, event.Tick); err != nil {
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
		if err = checkDependencies(ctx, tx, state, action, event.Snapshot, event.Tick); err != nil {
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
	if event.Kind == "dispatch" {
		if err = vetoAction(ctx, tx, current.Action()); err != nil {
			return domain.Progress{}, err
		}
	}
	if current.Action().Kind() == domain.ProductionBillAction {
		// A trusted refusal proves no bill was created, leaving the bench+recipe pair
		// claimable again; only an accepted or uncertain write may have produced one.
		if production, _ := current.Action().ProductionBill(); event.Kind == "receipt" && event.Receipt != domain.ReceiptRefused && event.Receipt != domain.ReceiptUnsent && production.Mode() != domain.GearBatch {
			bill, _ := current.Action().ProductionBill()
			snapshot := current.View().Snapshot
			// A reopened retry (e.g. an uncertain write later observed absent) claims the
			// same bench+recipe again; that is not a real conflict.
			if _, err := tx.ExecContext(ctx, "INSERT INTO bill_claims(colony,load_token,map_id,bench,recipe) VALUES(?,?,?,?,?) ON CONFLICT(colony,load_token,map_id,bench,recipe) DO NOTHING", snapshot.Colony, snapshot.Load, snapshot.Map, bill.Bench(), bill.ClaimRecipe()); err != nil {
				return domain.Progress{}, conflict(err)
			}
		}
	}
	// A zone_create prepares only under its method's footprint record,
	// which keeps the footprint accounted while the intent is in flight.
	if current.Action().Kind() == domain.ZoneCreateAction && event.Kind == "prepare" {
		accounted := false
		for _, record := range state.Admissions {
			accounted = accounted || record.Action == action && record.Admission.Tick <= event.Tick
		}
		if !accounted {
			return domain.Progress{}, errors.New("zone requires shared footprint admission")
		}
	}
	for _, record := range state.Admissions {
		if record.Action != action {
			continue
		}
		if event.Kind == "dispatch" && event.Tick < record.Admission.Tick {
			return domain.Progress{}, errors.New("dispatch predates latest admission observation")
		}
	}
	next, err := apply(current, event)
	if err != nil {
		return domain.Progress{}, err
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

// RecordZoneReceipt records an applied zone_create's receipt with the zone
// identity native created, which zone claims read.
func (s *Store) RecordZoneReceipt(ctx context.Context, plan domain.PlanID, action domain.ActionID, attempt domain.AttemptID, zone string) (domain.Progress, error) {
	return s.advance(ctx, plan, action, transition{Kind: "receipt", Attempt: attempt, Receipt: domain.ReceiptAccepted, Zone: zone})
}
func (s *Store) Observe(ctx context.Context, plan domain.PlanID, observation domain.Observation, current domain.GenerationSnapshot) (domain.Progress, error) {
	return s.advance(ctx, plan, observation.Action, transition{Kind: "observe", Snapshot: current, Observation: observation})
}

func (s *Store) Cancel(ctx context.Context, plan domain.PlanID, action domain.ActionID) (domain.Progress, error) {
	return s.advance(ctx, plan, action, transition{Kind: "cancel"})
}

// Withdraw opens the native withdrawal attempt of a cancelled, still-pending
// dispatch (domain.Progress.Withdraw); the executor writes the cancellation
// only after this is durable, as with Dispatch.
func (s *Store) Withdraw(ctx context.Context, plan domain.PlanID, action domain.ActionID, snapshot domain.GenerationSnapshot, tick domain.Tick) (domain.Progress, error) {
	return s.advance(ctx, plan, action, transition{Kind: "withdraw", Snapshot: snapshot, Tick: tick})
}

// Hold durably records why a not-yet-dispatched action is currently stuck.
// It reuses the same transaction-guarded replay as every other transition, so
// a concurrent transition committed first makes this one observe (and get
// rejected against) the up-to-date Progress, never a stale one.
func (s *Store) Hold(ctx context.Context, plan domain.PlanID, action domain.ActionID, reasons []domain.HeldReason, tick domain.Tick) (domain.Progress, error) {
	return s.advance(ctx, plan, action, transition{Kind: "hold", Tick: tick, HeldReasons: reasons})
}
