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
	"github.com/davidarcher/RimGovernor/go/internal/store/acquisition"
	"github.com/davidarcher/RimGovernor/go/internal/store/bill"
	"github.com/davidarcher/RimGovernor/go/internal/store/buildingtemperature"
	"github.com/davidarcher/RimGovernor/go/internal/store/capture"
	"github.com/davidarcher/RimGovernor/go/internal/store/clock"
	"github.com/davidarcher/RimGovernor/go/internal/store/core"
	"github.com/davidarcher/RimGovernor/go/internal/store/draft"
	"github.com/davidarcher/RimGovernor/go/internal/store/equip"
	"github.com/davidarcher/RimGovernor/go/internal/store/gearreplace"
	"github.com/davidarcher/RimGovernor/go/internal/store/haul"
	"github.com/davidarcher/RimGovernor/go/internal/store/melee"
	"github.com/davidarcher/RimGovernor/go/internal/store/movement"
	"github.com/davidarcher/RimGovernor/go/internal/store/ranged"
	"github.com/davidarcher/RimGovernor/go/internal/store/repair"
	"github.com/davidarcher/RimGovernor/go/internal/store/rescue"
	"github.com/davidarcher/RimGovernor/go/internal/store/supply"
	"github.com/davidarcher/RimGovernor/go/internal/store/tend"
	"github.com/davidarcher/RimGovernor/go/internal/store/work"
	"github.com/davidarcher/RimGovernor/go/internal/store/zone"
	"modernc.org/sqlite"
)

const schemaVersion = 84

// SchemaVersion is the PRAGMA user_version Open requires; a database
// from another version is refused (tooling reads those raw).
func SchemaVersion() int { return schemaVersion }

const applicationID = 0x52474f31

var ErrConflict = core.ErrConflict
var ErrNotAdmitted = core.ErrNotAdmitted
var ErrNotFound = core.ErrNotFound

type Store struct{ db *sql.DB }

// ControllerSessionID identifies one persistent controller execution namespace.
// It is independent of HTTP process sessions and survives controller restarts.
type ControllerSessionID = core.ControllerSessionID
type PlanState struct {
	BillAdmissions []ActionBillAdmission
	ZoneAdmissions []ActionZoneAdmission

	Retired                       bool
	Spec                          domain.PlanSpec
	Progress                      []domain.Progress
	Admissions                    []ActionAdmission
	DraftAdmissions               []ActionDraftAdmission
	MeleeAdmissions               []ActionMeleeAdmission
	AcquisitionAdmissions         []ActionAcquisitionAdmission
	SupplyAdmissions              []ActionSupplyAdmission
	WorkAdmissions                []ActionWorkAdmission
	TendAdmissions                []ActionTendAdmission
	RescueAdmissions              []ActionRescueAdmission
	CaptureAdmissions             []ActionCaptureAdmission
	RangedAdmissions              []ActionRangedAdmission
	MovementAdmissions            []ActionMovementAdmission
	HaulAdmissions                []ActionHaulAdmission
	EquipAdmissions               []ActionEquipAdmission
	GearReplaceAdmissions         []ActionGearReplaceAdmission
	RepairAdmissions              []ActionRepairAdmission
	CleanAdmissions               []ActionCleanAdmission
	WasteAdmissions               []ActionWasteAdmission
	MoodReliefAdmissions          []ActionMoodReliefAdmission
	RecoveryServiceAdmissions     []ActionRecoveryServiceAdmission
	BuildingTemperatureAdmissions []ActionBuildingTemperatureAdmission
	BedAssignAdmissions           []ActionBedAssignAdmission
	ResearchSelectAdmissions      []ActionResearchSelectAdmission
	ConfirmColonyNamesAdmissions  []ActionConfirmColonyNamesAdmission
	DialogAnswerAdmissions        []ActionDialogAnswerAdmission
	TradeAdmissions               []ActionTradeAdmission
	HusbandryAdmissions           []ActionHusbandryAdmission
	HomeCoverageAdmissions        []ActionHomeCoverageAdmission
	PrisonerInteractionAdmissions []ActionPrisonerInteractionAdmission
	MineAcquisitionAdmissions     []ActionMineAcquisitionAdmission
	CutPlantAdmissions            []ActionCutPlantAdmission
	ExcavationAdmissions          []ActionExcavationAdmission
	WallRemovalAdmissions         []ActionWallRemovalAdmission
	ProductionPolicyAdmissions    []ActionProductionPolicyAdmission
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
	s := &Store{db: db}
	if err = s.initialize(ctx); err != nil {
		_ = db.Close()
		return nil, err
	}
	return s, nil
}
func (s *Store) Close() error { return s.db.Close() }

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
CREATE TABLE plans(id TEXT PRIMARY KEY, revision TEXT NOT NULL, retired INTEGER NOT NULL DEFAULT 0 CHECK(retired IN (0,1)));
CREATE INDEX active_plans ON plans(id) WHERE retired=0;
CREATE TABLE retirement_floors(colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, tick INTEGER NOT NULL CHECK(tick>=0), PRIMARY KEY(colony,load_token,map_id)) STRICT;
CREATE TABLE actions(id TEXT PRIMARY KEY, plan_id TEXT NOT NULL REFERENCES plans(id), ordinal INTEGER NOT NULL, kind TEXT NOT NULL CHECK(kind IN ('building','owned_draft','melee_attack','supply_allow','work_assignment','acquisition','zone_create','tend','rescue','capture','ranged_attack','production_bill','haul','equip','gear_replace','repair','clean','waste','recovery_service','movement','research_select','husbandry','home_coverage','prisoner_interaction','mine_acquisition','wall_removal','excavation','production_policy','building_temperature','bed_medical','grower_crop','bed_assign','mood_relief','dialog_answer','naming_confirmation','cut_plant','trade')), definition TEXT, x INTEGER, z INTEGER, rotation TEXT, stuff TEXT, pawn TEXT, target TEXT, draft_action TEXT REFERENCES actions(id), work_payload BLOB, zone_payload BLOB, bill_payload BLOB, wall_removal_payload BLOB, production_policy_payload BLOB, building_temperature_payload BLOB, mood_relief_payload BLOB, trade_payload BLOB, CHECK((kind='trade' AND trade_payload IS NOT NULL) OR (kind<>'trade' AND trade_payload IS NULL)), CHECK((kind='building_temperature' AND building_temperature_payload IS NOT NULL) OR (kind<>'building_temperature' AND building_temperature_payload IS NULL)), CHECK((kind='mood_relief' AND mood_relief_payload IS NOT NULL) OR (kind<>'mood_relief' AND mood_relief_payload IS NULL)), CHECK((kind='production_bill' AND bill_payload IS NOT NULL) OR (kind<>'production_bill' AND bill_payload IS NULL)), CHECK((kind='zone_create' AND zone_payload IS NOT NULL) OR (kind!='zone_create' AND zone_payload IS NULL)), CHECK((kind='work_assignment' AND work_payload IS NOT NULL) OR (kind!='work_assignment' AND work_payload IS NULL)), CHECK((kind='wall_removal' AND wall_removal_payload IS NOT NULL) OR (kind<>'wall_removal' AND wall_removal_payload IS NULL)), CHECK((kind='production_policy' AND production_policy_payload IS NOT NULL) OR (kind<>'production_policy' AND production_policy_payload IS NULL)), CHECK((kind='trade' AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND target IS NULL AND draft_action IS NULL) OR (kind='production_bill' AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND target IS NULL AND draft_action IS NULL) OR (kind='zone_create' AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND target IS NULL AND draft_action IS NULL) OR (kind='wall_removal' AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND target IS NULL AND draft_action IS NULL) OR (kind='production_policy' AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND target IS NULL AND draft_action IS NULL) OR (kind='excavation' AND definition IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND target IS NULL AND draft_action IS NULL) OR (kind='building' AND definition IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NOT NULL AND stuff IS NOT NULL AND pawn IS NULL AND target IS NULL AND draft_action IS NULL) OR (kind='owned_draft' AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NULL AND draft_action IS NULL) OR (kind IN ('melee_attack','ranged_attack') AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NOT NULL) OR (kind IN ('supply_allow','acquisition','mine_acquisition','cut_plant') AND definition IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind='work_assignment' AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind IN ('tend','rescue','capture') AND definition IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind='haul' AND definition IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind='equip' AND definition IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind='gear_replace' AND definition IS NOT NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind IN ('repair','clean','waste') AND definition IS NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind IN ('recovery_service') AND definition IS NOT NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NOT NULL AND draft_action IS NULL) OR (kind='movement' AND definition IS NULL AND x IS NOT NULL AND z IS NOT NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NOT NULL AND target IS NULL AND draft_action IS NOT NULL) OR (kind='research_select' AND definition IS NOT NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND pawn IS NULL AND target IS NULL AND draft_action IS NULL) OR (kind='husbandry' AND definition IN ('train','slaughter','tame','release','allowed_area','master','follow_drafted','follow_fieldwork') AND target IS NOT NULL AND pawn IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND draft_action IS NULL AND ((definition='train' AND stuff IS NOT NULL) OR (definition IN ('slaughter','tame','release') AND stuff IS NULL) OR (definition IN ('allowed_area','master')) OR (definition IN ('follow_drafted','follow_fieldwork') AND stuff IN ('true','false')))) OR (kind='home_coverage' AND definition IS NOT NULL AND target IS NOT NULL AND pawn IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND draft_action IS NULL) OR (kind='prisoner_interaction' AND definition IN ('recruit','maintain') AND target IS NOT NULL AND pawn IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND draft_action IS NULL) OR (kind='building_temperature' AND target IS NOT NULL AND definition IS NULL AND pawn IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND draft_action IS NULL) OR (kind='bed_medical' AND target IS NOT NULL AND definition IN ('true','false') AND stuff IS NOT NULL AND pawn IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND draft_action IS NULL) OR (kind='grower_crop' AND target IS NOT NULL AND definition IS NOT NULL AND stuff IS NOT NULL AND pawn IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND draft_action IS NULL) OR (kind='bed_assign' AND pawn IS NOT NULL AND target IS NOT NULL AND definition IS NOT NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND draft_action IS NULL) OR (kind='mood_relief' AND pawn IS NOT NULL AND definition IS NULL AND target IS NULL AND x IS NULL AND z IS NULL AND rotation IS NULL AND stuff IS NULL AND draft_action IS NULL) OR (kind='dialog_answer' AND definition IS NOT NULL AND x IS NOT NULL AND z IS NOT NULL AND pawn IS NULL AND target IS NULL AND rotation IS NULL AND stuff IS NULL AND draft_action IS NULL) OR (kind='naming_confirmation' AND definition IS NOT NULL AND stuff IS NOT NULL AND x IS NOT NULL AND z IS NULL AND pawn IS NULL AND target IS NULL AND rotation IS NULL AND draft_action IS NULL)), UNIQUE(plan_id,ordinal)) STRICT;
CREATE TABLE transitions(sequence INTEGER PRIMARY KEY, action_id TEXT NOT NULL REFERENCES actions(id), payload BLOB NOT NULL);
CREATE TABLE action_dependencies(plan_id TEXT NOT NULL REFERENCES plans(id), action_id TEXT NOT NULL REFERENCES actions(id), requires_id TEXT NOT NULL REFERENCES actions(id), coupled INTEGER NOT NULL DEFAULT 0 CHECK(coupled IN (0,1)), PRIMARY KEY(plan_id,action_id,requires_id)) STRICT;
CREATE TABLE admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL);
CREATE TABLE draft_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE bill_claims(colony TEXT NOT NULL,load_token TEXT NOT NULL,map_id INTEGER NOT NULL,bench TEXT NOT NULL,recipe TEXT NOT NULL,PRIMARY KEY(colony,load_token,map_id,bench,recipe)) STRICT;
CREATE TABLE bill_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id),payload BLOB NOT NULL) STRICT;
CREATE TABLE zone_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE work_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE acquisition_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE supply_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE melee_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE tend_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE rescue_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE capture_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE ranged_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE movement_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE haul_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE equip_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE gear_replace_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE repair_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE clean_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE waste_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE mood_relief_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE recovery_service_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE building_temperature_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE bed_assign_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE research_select_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE confirm_colony_names_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE dialog_answer_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE trade_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE husbandry_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE home_coverage_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE prisoner_interaction_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE mine_acquisition_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE cut_plant_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE excavation_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE wall_removal_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE production_policy_admissions(action_id TEXT PRIMARY KEY REFERENCES actions(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE clock_attempts(request_id TEXT PRIMARY KEY, native_action_id TEXT NOT NULL UNIQUE, payload BLOB NOT NULL, phase TEXT NOT NULL CHECK(phase IN ('prepared','dispatched','uncertain','applied','refused')), reply BLOB, scope_context BLOB) STRICT;
CREATE TABLE clock_epochs(start_request_id TEXT PRIMARY KEY REFERENCES clock_attempts(request_id), stage TEXT NOT NULL CHECK(stage IN ('required','pausing','uncertain','paused','retired','superseded')), sequence TEXT NOT NULL, context BLOB, status BLOB) STRICT;
CREATE TABLE submissions(request_id TEXT PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN ('building','research_select','resource_policy')), colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, plan_id TEXT NOT NULL UNIQUE REFERENCES plans(id), action_id TEXT NOT NULL UNIQUE REFERENCES actions(id), revision TEXT NOT NULL) STRICT;
CREATE INDEX action_transitions ON transitions(action_id,sequence);
CREATE TABLE building_submissions(request_id TEXT PRIMARY KEY REFERENCES submissions(request_id), definition TEXT NOT NULL, x INTEGER NOT NULL, z INTEGER NOT NULL, rotation TEXT NOT NULL, stuff TEXT NOT NULL) STRICT;
CREATE TABLE research_select_submissions(request_id TEXT PRIMARY KEY REFERENCES submissions(request_id), payload BLOB NOT NULL) STRICT;
CREATE TABLE work_preferences(plan_id TEXT PRIMARY KEY REFERENCES plans(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE work_preference_requests(request_id TEXT PRIMARY KEY, payload BLOB NOT NULL) STRICT;
CREATE TABLE population_policy_submissions(request_id TEXT PRIMARY KEY, colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, maximum INTEGER NOT NULL, food_days REAL NOT NULL) STRICT;
CREATE TABLE population_policies(colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, request_id TEXT NOT NULL REFERENCES population_policy_submissions(request_id), maximum INTEGER NOT NULL, food_days REAL NOT NULL, PRIMARY KEY(colony,load_token,map_id)) STRICT;
CREATE TABLE expedition_policy_submissions(request_id TEXT PRIMARY KEY, colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, patch BLOB NOT NULL, minimum_home_colonists INTEGER NOT NULL, minimum_home_food_days REAL NOT NULL, travel_food_margin_days REAL NOT NULL, maximum_travel_days REAL NOT NULL, maximum_caravans INTEGER NOT NULL, minimum_goodwill INTEGER NOT NULL, minimum_destination_temperature REAL NOT NULL, maximum_destination_temperature REAL NOT NULL, keep_home_doctor INTEGER NOT NULL CHECK(keep_home_doctor IN (0,1)), require_return_storage INTEGER NOT NULL CHECK(require_return_storage IN (0,1))) STRICT;
CREATE TABLE expedition_policies(colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, request_id TEXT NOT NULL REFERENCES expedition_policy_submissions(request_id), minimum_home_colonists INTEGER NOT NULL, minimum_home_food_days REAL NOT NULL, travel_food_margin_days REAL NOT NULL, maximum_travel_days REAL NOT NULL, maximum_caravans INTEGER NOT NULL, minimum_goodwill INTEGER NOT NULL, minimum_destination_temperature REAL NOT NULL, maximum_destination_temperature REAL NOT NULL, keep_home_doctor INTEGER NOT NULL CHECK(keep_home_doctor IN (0,1)), require_return_storage INTEGER NOT NULL CHECK(require_return_storage IN (0,1)), PRIMARY KEY(colony,load_token,map_id)) STRICT;
CREATE TABLE population_decision_submissions(request_id TEXT PRIMARY KEY, colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, pawn TEXT NOT NULL, decision TEXT NOT NULL CHECK(decision IN ('rescue','capture','recruit','ignore'))) STRICT;
CREATE TABLE population_decisions(colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, pawn TEXT NOT NULL, request_id TEXT NOT NULL REFERENCES population_decision_submissions(request_id), decision TEXT NOT NULL CHECK(decision IN ('rescue','capture','recruit','ignore')), PRIMARY KEY(colony,load_token,map_id,pawn)) STRICT;
CREATE TABLE resource_policy_submissions(request_id TEXT PRIMARY KEY REFERENCES submissions(request_id), patch BLOB NOT NULL, resource TEXT NOT NULL, reserve INTEGER NOT NULL, spending TEXT NOT NULL CHECK(spending IN ('normal','defense_only','stop'))) STRICT;
CREATE TABLE resource_policies(colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, resource TEXT NOT NULL, request_id TEXT NOT NULL REFERENCES resource_policy_submissions(request_id), reserve INTEGER NOT NULL, spending TEXT NOT NULL CHECK(spending IN ('normal','defense_only','stop')), PRIMARY KEY(colony,load_token,map_id,resource)) STRICT;`)
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
		if err = initializeCaravanTracking(ctx, tx); err != nil {
			return err
		}
		if err = initializeTradeSessions(ctx, tx); err != nil {
			return err
		}
		if err = initializeTradeSessionReferences(ctx, tx); err != nil {
			return err
		}
		var entropy [32]byte
		if _, err = tx.ExecContext(ctx, `CREATE TABLE control_intents(request_id TEXT PRIMARY KEY, kind TEXT NOT NULL, colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, phase TEXT NOT NULL, native_generation TEXT NOT NULL) STRICT`); err != nil {
			return err
		}
		if _, err = tx.ExecContext(ctx, `CREATE TABLE root_plans(plan_id TEXT PRIMARY KEY REFERENCES plans(id), colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL) STRICT`); err != nil {
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
	for _, query := range []string{"SELECT action_id,payload FROM draft_admissions LIMIT 0", "SELECT action_id,payload FROM melee_admissions LIMIT 0", "SELECT action_id,payload FROM tend_admissions LIMIT 0", "SELECT action_id,payload FROM rescue_admissions LIMIT 0", "SELECT action_id,payload FROM capture_admissions LIMIT 0", "SELECT action_id,payload FROM ranged_admissions LIMIT 0", "SELECT action_id,payload FROM movement_admissions LIMIT 0", "SELECT action_id,payload FROM haul_admissions LIMIT 0", "SELECT action_id,payload FROM equip_admissions LIMIT 0", "SELECT action_id,payload FROM gear_replace_admissions LIMIT 0", "SELECT action_id,payload FROM recovery_service_admissions LIMIT 0", "SELECT action_id,payload FROM research_select_admissions LIMIT 0", "SELECT action_id,payload FROM confirm_colony_names_admissions LIMIT 0", "SELECT action_id,payload FROM dialog_answer_admissions LIMIT 0", "SELECT action_id,payload FROM husbandry_admissions LIMIT 0", "SELECT action_id,payload FROM home_coverage_admissions LIMIT 0", "SELECT action_id,payload FROM prisoner_interaction_admissions LIMIT 0", "SELECT action_id,payload FROM mine_acquisition_admissions LIMIT 0", "SELECT action_id,payload FROM cut_plant_admissions LIMIT 0", "SELECT action_id,payload FROM trade_admissions LIMIT 0", "SELECT action_id,payload FROM excavation_admissions LIMIT 0", "SELECT action_id,payload FROM wall_removal_admissions LIMIT 0", "SELECT action_id,payload FROM production_policy_admissions LIMIT 0", "SELECT action_id,payload FROM building_temperature_admissions LIMIT 0", "SELECT action_id,payload FROM bed_assign_admissions LIMIT 0", "SELECT request_id,kind,colony,load_token,map_id,plan_id,action_id,revision FROM submissions LIMIT 0"} {
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
	if err = checkCaravanTrackingSchema(ctx, tx); err != nil {
		return err
	}
	if err = checkTradeSessionsSchema(ctx, tx); err != nil {
		return err
	}
	if err = checkTradeSessionReferencesSchema(ctx, tx); err != nil {
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
	rows, err := tx.QueryContext(ctx, "SELECT id,kind,definition,x,z,rotation,stuff,pawn,target,draft_action,work_payload,zone_payload,bill_payload,wall_removal_payload,production_policy_payload,building_temperature_payload,mood_relief_payload,trade_payload,ordinal FROM actions WHERE plan_id=? ORDER BY ordinal", id)
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
		acquisitionAdmission, acquisitionPresent, e := acquisition.LoadAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.AcquisitionAction && !acquisitionPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("acquisition progress lacks admission")
		}
		if acquisitionPresent {
			state.AcquisitionAdmissions = append(state.AcquisitionAdmissions, ActionAcquisitionAdmission{Action: a.ID(), Admission: acquisitionAdmission})
		}
		mineAcquisitionAdmission, mineAcquisitionPresent, e := loadMineAcquisitionAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.MineAcquisitionAction && !mineAcquisitionPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("mine acquisition progress lacks admission")
		}
		if mineAcquisitionPresent {
			state.MineAcquisitionAdmissions = append(state.MineAcquisitionAdmissions, ActionMineAcquisitionAdmission{Action: a.ID(), Admission: mineAcquisitionAdmission})
		}
		cutPlantAdmission, cutPlantPresent, e := loadCutPlantAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.CutPlantAction && !cutPlantPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("cut plant progress lacks admission")
		}
		if cutPlantPresent {
			state.CutPlantAdmissions = append(state.CutPlantAdmissions, ActionCutPlantAdmission{Action: a.ID(), Admission: cutPlantAdmission})
		}
		excavationAdmission, excavationPresent, e := loadExcavationAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.ExcavationAction && !excavationPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("excavation progress lacks admission")
		}
		if excavationPresent {
			state.ExcavationAdmissions = append(state.ExcavationAdmissions, ActionExcavationAdmission{Action: a.ID(), Admission: excavationAdmission})
		}
		productionPolicyAdmission, productionPolicyPresent, e := loadProductionPolicyAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.ProductionPolicyAction && !productionPolicyPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("production policy progress lacks admission")
		}
		if productionPolicyPresent {
			state.ProductionPolicyAdmissions = append(state.ProductionPolicyAdmissions, ActionProductionPolicyAdmission{Action: a.ID(), Admission: productionPolicyAdmission})
		}
		supplyAdmission, supplyPresent, e := supply.LoadAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.SupplyAllowAction && !supplyPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("supply progress lacks admission")
		}
		if supplyPresent {
			state.SupplyAdmissions = append(state.SupplyAdmissions, ActionSupplyAdmission{Action: a.ID(), Admission: supplyAdmission})
		}
		billAdmission, billPresent, e := bill.LoadAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.ProductionBillAction && !billPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("bill progress lacks admission")
		}
		if billPresent {
			state.BillAdmissions = append(state.BillAdmissions, ActionBillAdmission{Action: a.ID(), Admission: billAdmission})
		}
		zoneAdmission, zonePresent, e := zone.LoadAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.ZoneCreateAction && !zonePresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("zone progress lacks admission")
		}
		if zonePresent {
			state.ZoneAdmissions = append(state.ZoneAdmissions, ActionZoneAdmission{Action: a.ID(), Admission: zoneAdmission})
		}
		workAdmission, workPresent, e := work.LoadAdmission(ctx, tx, a, p)
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
		draftAdmission, draftPresent, e := draft.LoadAdmission(ctx, tx, a, p)
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
		meleeAdmission, meleePresent, e := melee.LoadAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.MeleeAttackAction && !meleePresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("melee progress lacks admission")
		}
		if meleePresent {
			state.MeleeAdmissions = append(state.MeleeAdmissions, ActionMeleeAdmission{Action: a.ID(), Admission: meleeAdmission})
		}
		tendAdmission, tendPresent, e := tend.LoadAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.TendAction && !tendPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("tend progress lacks admission")
		}
		if tendPresent {
			state.TendAdmissions = append(state.TendAdmissions, ActionTendAdmission{Action: a.ID(), Admission: tendAdmission})
		}
		rescueAdmission, rescuePresent, e := rescue.LoadAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.RescueAction && !rescuePresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("rescue progress lacks admission")
		}
		if rescuePresent {
			state.RescueAdmissions = append(state.RescueAdmissions, ActionRescueAdmission{Action: a.ID(), Admission: rescueAdmission})
		}
		captureAdmission, capturePresent, e := capture.LoadAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.CaptureAction && !capturePresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("capture progress lacks admission")
		}
		if capturePresent {
			state.CaptureAdmissions = append(state.CaptureAdmissions, ActionCaptureAdmission{Action: a.ID(), Admission: captureAdmission})
		}
		rangedAdmission, rangedPresent, e := ranged.LoadAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.RangedAttackAction && !rangedPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("ranged attack progress lacks admission")
		}
		if rangedPresent {
			state.RangedAdmissions = append(state.RangedAdmissions, ActionRangedAdmission{Action: a.ID(), Admission: rangedAdmission})
		}
		movementAdmission, movementPresent, e := movement.LoadAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.MovementAction && !movementPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("movement progress lacks admission")
		}
		if movementPresent {
			state.MovementAdmissions = append(state.MovementAdmissions, ActionMovementAdmission{Action: a.ID(), Admission: movementAdmission})
		}
		haulAdmission, haulPresent, e := haul.LoadAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.HaulAction && !haulPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("haul progress lacks admission")
		}
		if haulPresent {
			state.HaulAdmissions = append(state.HaulAdmissions, ActionHaulAdmission{Action: a.ID(), Admission: haulAdmission})
		}
		equipAdmission, equipPresent, e := equip.LoadAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.EquipAction && !equipPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("equip progress lacks admission")
		}
		if equipPresent {
			state.EquipAdmissions = append(state.EquipAdmissions, ActionEquipAdmission{Action: a.ID(), Admission: equipAdmission})
		}
		gearReplaceAdmission, gearReplacePresent, e := gearreplace.LoadAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.GearReplaceAction && !gearReplacePresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("gear replace progress lacks admission")
		}
		if gearReplacePresent {
			state.GearReplaceAdmissions = append(state.GearReplaceAdmissions, ActionGearReplaceAdmission{Action: a.ID(), Admission: gearReplaceAdmission})
		}
		repairAdmission, repairPresent, e := repair.LoadAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.RepairAction && !repairPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("repair progress lacks admission")
		}
		if repairPresent {
			state.RepairAdmissions = append(state.RepairAdmissions, ActionRepairAdmission{Action: a.ID(), Admission: repairAdmission})
		}
		cleanAdmission, cleanPresent, e := loadCleanAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.CleanAction && !cleanPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("clean progress lacks admission")
		}
		if cleanPresent {
			state.CleanAdmissions = append(state.CleanAdmissions, ActionCleanAdmission{Action: a.ID(), Admission: cleanAdmission})
		}
		wasteAdmission, wastePresent, e := loadWasteAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.WasteAction && !wastePresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("waste progress lacks admission")
		}
		if wastePresent {
			state.WasteAdmissions = append(state.WasteAdmissions, ActionWasteAdmission{Action: a.ID(), Admission: wasteAdmission})
		}
		moodReliefAdmission, moodReliefPresent, e := loadMoodReliefAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.MoodReliefAction && !moodReliefPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("mood relief progress lacks admission")
		}
		if moodReliefPresent {
			state.MoodReliefAdmissions = append(state.MoodReliefAdmissions, ActionMoodReliefAdmission{Action: a.ID(), Admission: moodReliefAdmission})
		}
		recoveryServiceAdmission, recoveryServicePresent, e := loadRecoveryServiceAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.RecoveryServiceAction && !recoveryServicePresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("recovery service progress lacks admission")
		}
		if recoveryServicePresent {
			state.RecoveryServiceAdmissions = append(state.RecoveryServiceAdmissions, ActionRecoveryServiceAdmission{Action: a.ID(), Admission: recoveryServiceAdmission})
		}
		buildingTemperatureAdmission, buildingTemperaturePresent, e := buildingtemperature.LoadAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if buildingtemperature.Kind(a.Kind()) && !buildingTemperaturePresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("building temperature progress lacks admission")
		}
		if buildingTemperaturePresent {
			state.BuildingTemperatureAdmissions = append(state.BuildingTemperatureAdmissions, ActionBuildingTemperatureAdmission{Action: a.ID(), Admission: buildingTemperatureAdmission})
		}
		bedAssignAdmission, bedAssignPresent, e := loadBedAssignAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.BedAssignAction && !bedAssignPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("bed assign progress lacks admission")
		}
		if bedAssignPresent {
			state.BedAssignAdmissions = append(state.BedAssignAdmissions, ActionBedAssignAdmission{Action: a.ID(), Admission: bedAssignAdmission})
		}
		researchSelectAdmission, researchSelectPresent, e := loadResearchSelectAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.ResearchSelectAction && !researchSelectPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("research select progress lacks admission")
		}
		if researchSelectPresent {
			state.ResearchSelectAdmissions = append(state.ResearchSelectAdmissions, ActionResearchSelectAdmission{Action: a.ID(), Admission: researchSelectAdmission})
		}
		confirmColonyNamesAdmission, confirmColonyNamesPresent, e := loadConfirmColonyNamesAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.NamingConfirmationAction && !confirmColonyNamesPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("naming confirmation progress lacks admission")
		}
		if confirmColonyNamesPresent {
			state.ConfirmColonyNamesAdmissions = append(state.ConfirmColonyNamesAdmissions, ActionConfirmColonyNamesAdmission{Action: a.ID(), Admission: confirmColonyNamesAdmission})
		}
		dialogAnswerAdmission, dialogAnswerPresent, e := loadDialogAnswerAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.DialogAnswerAction && !dialogAnswerPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("dialog answer progress lacks admission")
		}
		if dialogAnswerPresent {
			state.DialogAnswerAdmissions = append(state.DialogAnswerAdmissions, ActionDialogAnswerAdmission{Action: a.ID(), Admission: dialogAnswerAdmission})
		}
		tradeAdmission, tradePresent, e := loadTradeAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.TradeAction && !tradePresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("trade progress lacks admission")
		}
		if tradePresent {
			state.TradeAdmissions = append(state.TradeAdmissions, ActionTradeAdmission{Action: a.ID(), Admission: tradeAdmission})
		}
		husbandryAdmission, husbandryPresent, e := loadHusbandryAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.HusbandryAction && !husbandryPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("husbandry progress lacks admission")
		}
		if husbandryPresent {
			state.HusbandryAdmissions = append(state.HusbandryAdmissions, ActionHusbandryAdmission{Action: a.ID(), Admission: husbandryAdmission})
		}
		homeCoverageAdmission, homeCoveragePresent, e := loadHomeCoverageAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.HomeCoverageAction && !homeCoveragePresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("home coverage progress lacks admission")
		}
		if homeCoveragePresent {
			state.HomeCoverageAdmissions = append(state.HomeCoverageAdmissions, ActionHomeCoverageAdmission{Action: a.ID(), Admission: homeCoverageAdmission})
		}
		prisonerInteractionAdmission, prisonerInteractionPresent, e := loadPrisonerInteractionAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.PrisonerInteractionAction && !prisonerInteractionPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("prisoner interaction progress lacks admission")
		}
		if prisonerInteractionPresent {
			state.PrisonerInteractionAdmissions = append(state.PrisonerInteractionAdmissions, ActionPrisonerInteractionAdmission{Action: a.ID(), Admission: prisonerInteractionAdmission})
		}
		wallRemovalAdmission, wallRemovalPresent, e := loadWallRemovalAdmission(ctx, tx, a, p)
		if e != nil {
			return PlanState{}, e
		}
		if a.Kind() == domain.WallRemovalAction && !wallRemovalPresent && (p.View().Stage == domain.Prepared || p.View().Attempt > 0) {
			return PlanState{}, errors.New("wall removal progress lacks admission")
		}
		if wallRemovalPresent {
			state.WallRemovalAdmissions = append(state.WallRemovalAdmissions, ActionWallRemovalAdmission{Action: a.ID(), Admission: wallRemovalAdmission})
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
	for _, record := range state.MovementAdmissions {
		if err := validateMovementPrerequisite(ctx, tx, state, record.Action, record.Admission, false); err != nil {
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
	HeldReasons            []domain.HeldReason             `json:",omitempty"`
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
		if event.Kind == "dispatch" && !acquisition.GuardDispatch(state.AcquisitionAdmissions, action, event.Snapshot, event.Tick) {
			return domain.Progress{}, errors.New("acquisition dispatch lacks current admission")
		}
	}
	if current.Action().Kind() == domain.SupplyAllowAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("supply requires typed preparation")
		}
		if event.Kind == "dispatch" && !supply.GuardDispatch(state.SupplyAdmissions, action, event.Snapshot, event.Tick) {
			return domain.Progress{}, errors.New("supply dispatch lacks current admission")
		}
	}
	if current.Action().Kind() == domain.MineAcquisitionAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("mine acquisition requires typed preparation")
		}
		if event.Kind == "dispatch" && !mineAcquisitionGuardDispatch(state.MineAcquisitionAdmissions, action, event.Snapshot, event.Tick) {
			return domain.Progress{}, errors.New("mine acquisition dispatch lacks current admission")
		}
	}
	if current.Action().Kind() == domain.CutPlantAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("cut plant requires typed preparation")
		}
		if event.Kind == "dispatch" && !cutPlantGuardDispatch(state.CutPlantAdmissions, action, event.Snapshot, event.Tick) {
			return domain.Progress{}, errors.New("cut plant dispatch lacks current admission")
		}
	}
	if current.Action().Kind() == domain.ExcavationAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("excavation requires typed preparation")
		}
		if event.Kind == "dispatch" && !excavationGuardDispatch(state.ExcavationAdmissions, action, event.Snapshot, event.Tick) {
			return domain.Progress{}, errors.New("excavation dispatch lacks current admission")
		}
	}
	if current.Action().Kind() == domain.ProductionBillAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("bill requires typed preparation")
		}
		if event.Kind == "dispatch" && !bill.GuardDispatch(state.BillAdmissions, action, event.Snapshot, event.Tick) {
			return domain.Progress{}, errors.New("bill dispatch lacks admission")
		}
		// A trusted refusal proves no bill was created, leaving the bench+recipe pair
		// claimable again; only an accepted or uncertain write may have produced one.
		if event.Kind == "receipt" && event.Receipt != domain.ReceiptRefused && event.Receipt != domain.ReceiptUnsent {
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
		if event.Kind == "dispatch" && !zone.GuardDispatch(state.ZoneAdmissions, action, event.Snapshot, event.Tick) {
			return domain.Progress{}, errors.New("zone dispatch lacks current admission")
		}
	}
	if current.Action().Kind() == domain.WorkAssignmentAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("work requires typed preparation")
		}
		if event.Kind == "dispatch" && !work.GuardDispatch(state.WorkAdmissions, action, event.Snapshot, event.Tick) {
			return domain.Progress{}, errors.New("work dispatch lacks current admission")
		}
	}
	if current.Action().Kind() == domain.TendAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("tend requires typed preparation")
		}
		if event.Kind == "dispatch" && !tend.GuardDispatch(state.TendAdmissions, action, event.Snapshot, event.Tick) {
			return domain.Progress{}, errors.New("tend dispatch lacks current admission")
		}
	}
	if current.Action().Kind() == domain.RescueAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("rescue requires typed preparation")
		}
		if event.Kind == "dispatch" && !rescue.GuardDispatch(state.RescueAdmissions, action, event.Snapshot, event.Tick) {
			return domain.Progress{}, errors.New("rescue dispatch lacks current admission")
		}
	}
	if current.Action().Kind() == domain.CaptureAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("capture requires typed preparation")
		}
		if event.Kind == "dispatch" && !capture.GuardDispatch(state.CaptureAdmissions, action, event.Snapshot, event.Tick) {
			return domain.Progress{}, errors.New("capture dispatch lacks current admission")
		}
	}
	if current.Action().Kind() == domain.HaulAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("haul requires typed preparation")
		}
		if event.Kind == "dispatch" && !haul.GuardDispatch(state.HaulAdmissions, action, event.Snapshot, event.Tick) {
			return domain.Progress{}, errors.New("haul dispatch lacks current admission")
		}
	}
	if current.Action().Kind() == domain.EquipAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("equip requires typed preparation")
		}
		if event.Kind == "dispatch" && !equip.GuardDispatch(state.EquipAdmissions, action, event.Snapshot, event.Tick) {
			return domain.Progress{}, errors.New("equip dispatch lacks current admission")
		}
	}
	if current.Action().Kind() == domain.GearReplaceAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("gear replace requires typed preparation")
		}
		if event.Kind == "dispatch" && !gearreplace.GuardDispatch(state.GearReplaceAdmissions, action, event.Snapshot, event.Tick) {
			return domain.Progress{}, errors.New("gear replace dispatch lacks current admission")
		}
	}
	if current.Action().Kind() == domain.RepairAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("repair requires typed preparation")
		}
		if event.Kind == "dispatch" && !repair.GuardDispatch(state.RepairAdmissions, action, event.Snapshot, event.Tick) {
			return domain.Progress{}, errors.New("repair dispatch lacks current admission")
		}
	}
	if current.Action().Kind() == domain.CleanAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("clean requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.CleanAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("clean dispatch lacks current admission")
			}
		}
	}
	if current.Action().Kind() == domain.WasteAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("waste requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.WasteAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("waste dispatch lacks current admission")
			}
		}
	}
	if buildingtemperature.Kind(current.Action().Kind()) {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("building temperature requires typed preparation")
		}
		if event.Kind == "dispatch" && !buildingtemperature.GuardDispatch(state.BuildingTemperatureAdmissions, action, event.Snapshot, event.Tick) {
			return domain.Progress{}, errors.New("building temperature dispatch lacks current admission")
		}
	}
	if current.Action().Kind() == domain.BedAssignAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("bed assign requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.BedAssignAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("bed assign dispatch lacks current admission")
			}
		}
	}
	if current.Action().Kind() == domain.RecoveryServiceAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("recovery service requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.RecoveryServiceAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("recovery service dispatch lacks current admission")
			}
		}
	}
	if current.Action().Kind() == domain.HomeCoverageAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("home coverage requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.HomeCoverageAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("home coverage dispatch lacks current admission")
			}
		}
	}
	if current.Action().Kind() == domain.WallRemovalAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("wall removal requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.WallRemovalAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("wall removal dispatch lacks current admission")
			}
		}
	}
	if current.Action().Kind() == domain.ResearchSelectAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("research select requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.ResearchSelectAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("research select dispatch lacks current admission")
			}
		}
	}
	if current.Action().Kind() == domain.NamingConfirmationAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("naming confirmation requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.ConfirmColonyNamesAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("naming confirmation dispatch lacks current admission")
			}
		}
	}
	if current.Action().Kind() == domain.DialogAnswerAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("dialog answer requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.DialogAnswerAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("dialog answer dispatch lacks current admission")
			}
		}
	}
	if current.Action().Kind() == domain.TradeAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("trade requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.TradeAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("trade dispatch lacks current admission")
			}
		}
	}
	if current.Action().Kind() == domain.ProductionPolicyAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("production policy requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.ProductionPolicyAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("production policy dispatch lacks current admission")
			}
		}
	}
	if current.Action().Kind() == domain.HusbandryAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("husbandry requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.HusbandryAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("husbandry dispatch lacks current admission")
			}
		}
	}
	if current.Action().Kind() == domain.PrisonerInteractionAction {
		if event.Kind == "prepare" {
			return domain.Progress{}, errors.New("prisoner interaction requires typed preparation")
		}
		if event.Kind == "dispatch" {
			matched := false
			for _, record := range state.PrisonerInteractionAdmissions {
				if record.Action == action && record.Admission.Snapshot == event.Snapshot && record.Admission.Tick <= event.Tick {
					matched = true
				}
			}
			if !matched {
				return domain.Progress{}, errors.New("prisoner interaction dispatch lacks current admission")
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
	if err = guardMovementAdvance(ctx, tx, state, action, event); err != nil {
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
