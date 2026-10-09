package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// CombatEvidenceStops bounds retained fight evidence to the newest stops; flight recordings
// retain the full history.
const CombatEvidenceStops = 32

// An ActiveCombat fight owns its roster and tactical batches. While it is open, the undraft
// sweep preserves its roster drafts. Each stop retains native results as evidence; an open
// fight counts as Incident work.

// CombatOrderRecord is one order of a stop and its native outcome.
type CombatOrderRecord struct {
	policy.CombatOrder
	Applied   bool
	Uncertain bool   `json:",omitempty"`
	Refusal   string `json:",omitempty"`
}

// CombatStopRecord is one stop's changed orders.
type CombatStopRecord struct {
	Batch  domain.PlanID `json:",omitempty"`
	Tick   domain.Tick
	Stop   policy.StopEvent
	Orders []CombatOrderRecord
}

// CombatFight is a fight's state: open while it runs, the DecideCombat
// memory after its latest stop, and the roster of pawns it drafted in
// World.
type CombatFight struct {
	Open   bool
	Memory policy.CombatMemory
	World  World
	Roster map[domain.PawnID]bool
}

type combatRoster struct {
	World  World
	Roster []domain.PawnID
}

func initializeCombatFights(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, `CREATE TABLE combat_fights(plan_id TEXT PRIMARY KEY REFERENCES plans(id), open INTEGER NOT NULL CHECK(open IN (0,1)), memory BLOB NOT NULL, roster BLOB NOT NULL) STRICT;
CREATE TABLE combat_evidence(plan_id TEXT NOT NULL REFERENCES combat_fights(plan_id), sequence INTEGER NOT NULL, tick INTEGER NOT NULL CHECK(tick>=0), payload BLOB NOT NULL, PRIMARY KEY(plan_id,sequence)) STRICT;`)
	return err
}

func checkCombatFightsSchema(ctx context.Context, tx *sql.Tx) error {
	if _, err := tx.ExecContext(ctx, "SELECT plan_id,open,memory,roster FROM combat_fights LIMIT 0"); err != nil {
		return err
	}
	_, err := tx.ExecContext(ctx, "SELECT plan_id,sequence,tick,payload FROM combat_evidence LIMIT 0")
	return err
}

// CommitCombatFight admits a fight: the ActiveCombat incident's
// method on plan, whose actions are its threat loadout's equip and
// wear actions or a guarded subdue response, and its open fight row with roster in one
// transaction (the admission batch drafts them; a loadout pawn drafts
// later, so roster may be empty then).
func (s *Store) CommitCombatFight(ctx context.Context, incident domain.IncidentID, method domain.MethodID, plan domain.PlanSpec, memory policy.CombatMemory, world World, roster []domain.PawnID) (IncidentState, error) {
	if err := plan.Validate(); err != nil {
		return IncidentState{}, err
	}
	for _, action := range plan.Actions() {
		if action.Kind() != domain.EquipAction && action.Kind() != domain.GearReplaceAction && action.Kind() != domain.OwnedDraftAction && action.Kind() != domain.SubdueAction {
			return IncidentState{}, errors.New("combat fight plan holds only loadout or subdue actions")
		}
	}
	if len(roster)+len(plan.Actions()) == 0 || world.Validate() != nil {
		return IncidentState{}, errors.New("combat fight needs a roster or a loadout, and a world")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return IncidentState{}, err
	}
	defer tx.Rollback()
	state, err := commitIncidentMethod(ctx, tx, incident, method, "", plan, false)
	if err != nil {
		return IncidentState{}, err
	}
	if err = insertCombatFight(ctx, tx, plan.ID(), memory, world, roster); err != nil {
		return IncidentState{}, err
	}
	return state, tx.Commit()
}

// CommitFightStrip admits a strip of a downed raider beside the incident's
// open fight: a method whose plan holds only strip actions, which
// the fight's own open work does not refuse.
func (s *Store) CommitFightStrip(ctx context.Context, incident domain.IncidentID, method domain.MethodID, plan domain.PlanSpec) (IncidentState, error) {
	if err := plan.Validate(); err != nil {
		return IncidentState{}, err
	}
	if len(plan.Actions()) == 0 {
		return IncidentState{}, errors.New("empty fight strip")
	}
	for _, action := range plan.Actions() {
		if action.Kind() != domain.StripAction {
			return IncidentState{}, errors.New("fight strip plan holds only strip actions")
		}
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return IncidentState{}, err
	}
	defer tx.Rollback()
	state, err := commitIncidentMethod(ctx, tx, incident, method, "", plan, true)
	if err != nil {
		return IncidentState{}, err
	}
	return state, tx.Commit()
}

// OpenCombatFight records an open fight for an existing plan, as
// CommitCombatFight does; a fight already recorded is left as it is.
func (s *Store) OpenCombatFight(ctx context.Context, plan domain.PlanID, memory policy.CombatMemory, world World, roster []domain.PawnID) error {
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if err = insertCombatFight(ctx, tx, plan, memory, world, roster); err != nil {
		return err
	}
	return tx.Commit()
}

func encodeRoster(world World, roster map[domain.PawnID]bool) ([]byte, error) {
	held := combatRoster{World: world}
	for pawn := range roster {
		held.Roster = append(held.Roster, pawn)
	}
	return json.Marshal(held)
}

func insertCombatFight(ctx context.Context, tx *sql.Tx, plan domain.PlanID, memory policy.CombatMemory, world World, roster []domain.PawnID) error {
	encoded, err := json.Marshal(memory)
	if err != nil {
		return err
	}
	set := map[domain.PawnID]bool{}
	for _, pawn := range roster {
		set[pawn] = true
	}
	held, err := encodeRoster(world, set)
	if err != nil {
		return err
	}
	_, err = tx.ExecContext(ctx, "INSERT OR IGNORE INTO combat_fights(plan_id,open,memory,roster) VALUES(?,1,?,?)", string(plan), encoded, held)
	return err
}

func decodeCombatFight(open int, memory, roster []byte) (CombatFight, error) {
	out := CombatFight{Open: open == 1, Roster: map[domain.PawnID]bool{}}
	if err := json.Unmarshal(memory, &out.Memory); err != nil {
		return CombatFight{}, err
	}
	var held combatRoster
	if err := json.Unmarshal(roster, &held); err != nil {
		return CombatFight{}, err
	}
	out.World = held.World
	for _, pawn := range held.Roster {
		out.Roster[pawn] = true
	}
	return out, nil
}

// LoadCombatFight is plan's fight, if it has one.
func (s *Store) LoadCombatFight(ctx context.Context, plan domain.PlanID) (CombatFight, bool, error) {
	return loadCombatFight(ctx, s.db, plan)
}

type queryRower interface {
	QueryRowContext(context.Context, string, ...any) *sql.Row
}

func loadCombatFight(ctx context.Context, db queryRower, plan domain.PlanID) (CombatFight, bool, error) {
	var open int
	var memory, roster []byte
	err := db.QueryRowContext(ctx, "SELECT open,memory,roster FROM combat_fights WHERE plan_id=?", string(plan)).Scan(&open, &memory, &roster)
	if errors.Is(err, sql.ErrNoRows) {
		return CombatFight{}, false, nil
	}
	if err != nil {
		return CombatFight{}, false, err
	}
	out, err := decodeCombatFight(open, memory, roster)
	return out, err == nil, err
}

// combatFightHolds reports whether plan is a fight still open.
func combatFightHolds(ctx context.Context, tx *sql.Tx, plan domain.PlanID) (bool, error) {
	fight, ok, err := loadCombatFight(ctx, tx, plan)
	return ok && fight.Open, err
}

// OpenCombatFights is every open fight.
func (s *Store) OpenCombatFights(ctx context.Context) (map[domain.PlanID]CombatFight, error) {
	rows, err := s.db.QueryContext(ctx, "SELECT plan_id,open,memory,roster FROM combat_fights WHERE open=1")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := map[domain.PlanID]CombatFight{}
	for rows.Next() {
		var id string
		var open int
		var memory, roster []byte
		if err = rows.Scan(&id, &open, &memory, &roster); err != nil {
			return nil, err
		}
		fight, err := decodeCombatFight(open, memory, roster)
		if err != nil {
			return nil, err
		}
		out[domain.PlanID(id)] = fight
	}
	return out, rows.Err()
}

// UpdateCombatRoster adds the pawns a stop drafted and forgets those whose
// draft order was refused: the fight no longer needs them drafted.
func (s *Store) UpdateCombatRoster(ctx context.Context, plan domain.PlanID, add, drop []domain.PawnID) error {
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	fight, ok, err := loadCombatFight(ctx, tx, plan)
	if err != nil {
		return err
	}
	if !ok {
		return fmt.Errorf("%w: combat fight %s", ErrNotFound, plan)
	}
	for _, pawn := range add {
		fight.Roster[pawn] = true
	}
	for _, pawn := range drop {
		delete(fight.Roster, pawn)
	}
	roster, err := encodeRoster(fight.World, fight.Roster)
	if err != nil {
		return err
	}
	if _, err = tx.ExecContext(ctx, "UPDATE combat_fights SET roster=? WHERE plan_id=?", roster, string(plan)); err != nil {
		return err
	}
	return tx.Commit()
}

// CloseCombatFight ends plan's fight; the undraft sweep then undrafts its
// roster.
func (s *Store) CloseCombatFight(ctx context.Context, plan domain.PlanID) error {
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	kept, ok, err := loadCombatRestoration(ctx, tx)
	if err != nil {
		return err
	}
	if ok && kept.Owner == plan {
		return ErrConflict
	}
	if _, err = tx.ExecContext(ctx, "UPDATE combat_fights SET open=0 WHERE plan_id=?", plan); err != nil {
		return err
	}
	if err = supersedeCombatBatches(ctx, tx, plan, plan, false); err != nil {
		return err
	}
	return tx.Commit()
}

// SaveCombatMemory replaces an open fight's memory without evidence: a
// re-formation that ordered nothing.
func (s *Store) SaveCombatMemory(ctx context.Context, plan domain.PlanID, memory policy.CombatMemory) error {
	encoded, err := json.Marshal(memory)
	if err != nil {
		return err
	}
	_, err = s.db.ExecContext(ctx, "UPDATE combat_fights SET memory=? WHERE plan_id=?", encoded, string(plan))
	return err
}

// RecordCombatStop appends one stop's orders to plan's evidence, keeps the
// newest CombatEvidenceStops and saves the memory after it. A stop with
// no orders is not recorded.
func (s *Store) RecordCombatStop(ctx context.Context, plan domain.PlanID, stop CombatStopRecord, memory policy.CombatMemory) error {
	if len(stop.Orders) == 0 || stop.Tick < 0 {
		return errors.New("combat stop records no orders")
	}
	payload, err := json.Marshal(stop)
	if err != nil {
		return err
	}
	encoded, err := json.Marshal(memory)
	if err != nil {
		return err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	result, err := tx.ExecContext(ctx, "UPDATE combat_fights SET memory=? WHERE plan_id=?", encoded, string(plan))
	if err != nil {
		return err
	}
	if n, err := result.RowsAffected(); err != nil || n != 1 {
		return errors.Join(errors.New("combat fight not recorded"), err)
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO combat_evidence(plan_id,sequence,tick,payload) VALUES(?,(SELECT COALESCE(MAX(sequence),0)+1 FROM combat_evidence WHERE plan_id=?),?,?)", string(plan), string(plan), int64(stop.Tick), payload); err != nil {
		return err
	}
	if _, err = tx.ExecContext(ctx, "DELETE FROM combat_evidence WHERE plan_id=? AND sequence<=(SELECT MAX(sequence) FROM combat_evidence WHERE plan_id=?)-?", string(plan), string(plan), CombatEvidenceStops); err != nil {
		return err
	}
	if stop.Batch != "" {
		if err = supersedeCombatBatches(ctx, tx, plan, stop.Batch, false); err != nil {
			return err
		}
	}
	return tx.Commit()
}

// CombatEvidence is plan's recorded stops, oldest first.
func (s *Store) CombatEvidence(ctx context.Context, plan domain.PlanID) ([]CombatStopRecord, error) {
	rows, err := s.db.QueryContext(ctx, "SELECT payload FROM combat_evidence WHERE plan_id=? ORDER BY sequence", string(plan))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []CombatStopRecord
	for rows.Next() {
		var payload []byte
		if err = rows.Scan(&payload); err != nil {
			return nil, err
		}
		var stop CombatStopRecord
		if err = json.Unmarshal(payload, &stop); err != nil {
			return nil, err
		}
		out = append(out, stop)
	}
	return out, rows.Err()
}
