package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// CombatEvidenceStops is how many stops of combat orders one fight's plan
// keeps as evidence (#852): the newest ones. The full history is the
// #853 recording's.
const CombatEvidenceStops = 32

// A combat fight is the one ActiveCombat plan that owns a whole fight
// (#852). Its plan holds only the defenders' owned drafts; its orders go
// out through combat.orders at each stop and are recorded here as the
// plan's evidence, the single record of combat orders. While the fight is
// open the worker keeps the plan's completed drafts; closing it releases
// them through ordinary draft cleanup.

// CombatOrderRecord is one order of a stop and its native outcome.
type CombatOrderRecord struct {
	policy.CombatOrder
	Applied bool
	Refusal string `json:",omitempty"`
}

// CombatStopRecord is one stop's changed orders.
type CombatStopRecord struct {
	Tick   domain.Tick
	Stop   policy.StopEvent
	Orders []CombatOrderRecord
}

// CombatFight is a fight's state: open while its drafts are held, and the
// DecideCombat memory after its latest stop.
type CombatFight struct {
	Open   bool
	Memory policy.CombatMemory
}

func initializeCombatFights(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, `CREATE TABLE combat_fights(plan_id TEXT PRIMARY KEY REFERENCES plans(id), open INTEGER NOT NULL CHECK(open IN (0,1)), memory BLOB NOT NULL) STRICT;
CREATE TABLE combat_evidence(plan_id TEXT NOT NULL REFERENCES combat_fights(plan_id), sequence INTEGER NOT NULL, tick INTEGER NOT NULL CHECK(tick>=0), payload BLOB NOT NULL, PRIMARY KEY(plan_id,sequence)) STRICT;`)
	return err
}

func checkCombatFightsSchema(ctx context.Context, tx *sql.Tx) error {
	if _, err := tx.ExecContext(ctx, "SELECT plan_id,open,memory FROM combat_fights LIMIT 0"); err != nil {
		return err
	}
	_, err := tx.ExecContext(ctx, "SELECT plan_id,sequence,tick,payload FROM combat_evidence LIMIT 0")
	return err
}

// OpenCombatFight records plan as an open fight with its formation memory;
// a fight already recorded is left as it is.
func (s *Store) OpenCombatFight(ctx context.Context, plan domain.PlanID, memory policy.CombatMemory) error {
	encoded, err := json.Marshal(memory)
	if err != nil {
		return err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if _, err = tx.ExecContext(ctx, "INSERT OR IGNORE INTO combat_fights(plan_id,open,memory) VALUES(?,1,?)", string(plan), encoded); err != nil {
		return err
	}
	return tx.Commit()
}

// LoadCombatFight is plan's fight, if it has one.
func (s *Store) LoadCombatFight(ctx context.Context, plan domain.PlanID) (CombatFight, bool, error) {
	var open int
	var memory []byte
	err := s.db.QueryRowContext(ctx, "SELECT open,memory FROM combat_fights WHERE plan_id=?", string(plan)).Scan(&open, &memory)
	if errors.Is(err, sql.ErrNoRows) {
		return CombatFight{}, false, nil
	}
	if err != nil {
		return CombatFight{}, false, err
	}
	out := CombatFight{Open: open == 1}
	if err = json.Unmarshal(memory, &out.Memory); err != nil {
		return CombatFight{}, false, err
	}
	return out, true, nil
}

// OpenCombatFights is every open fight's plan.
func (s *Store) OpenCombatFights(ctx context.Context) (map[domain.PlanID]bool, error) {
	rows, err := s.db.QueryContext(ctx, "SELECT plan_id FROM combat_fights WHERE open=1")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := map[domain.PlanID]bool{}
	for rows.Next() {
		var id string
		if err = rows.Scan(&id); err != nil {
			return nil, err
		}
		out[domain.PlanID(id)] = true
	}
	return out, rows.Err()
}

// CloseCombatFight ends plan's fight: its drafts are released.
func (s *Store) CloseCombatFight(ctx context.Context, plan domain.PlanID) error {
	_, err := s.db.ExecContext(ctx, "UPDATE combat_fights SET open=0 WHERE plan_id=?", string(plan))
	return err
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
