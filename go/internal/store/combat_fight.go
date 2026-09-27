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

// CombatEvidenceStops is how many stops of combat orders one fight's plan
// keeps as evidence (#852): the newest ones. The full history is the
// #853 recording's.
const CombatEvidenceStops = 32

// A combat fight is the one ActiveCombat plan that owns a whole fight
// (#852). Its plan has no actions: the defenders are drafted by draft
// orders in the fight's first combat.orders batch (#910) and every later
// stop's orders go out the same way, recorded here as the plan's evidence,
// the single record of combat orders. The fight row owns the draft claims
// those orders made: held while the fight is open, released by the
// controller once it closes (buildingruntime's fight release). A fight
// that is open or still holds a claim is its goal's open work.

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

// CombatFight is a fight's state: open while its drafts are held, the
// DecideCombat memory after its latest stop, and the draft claims it owns
// in World. A claim id is empty while the admission batch that drafted the
// pawn is uncertain: the pawn's claim row settles it.
type CombatFight struct {
	Open   bool
	Memory policy.CombatMemory
	World  World
	Claims map[domain.PawnID]string
}

// Holds reports whether the fight is still its goal's open work: open, or
// holding a claim not yet released.
func (f CombatFight) Holds() bool { return f.Open || len(f.Claims) > 0 }

type combatClaims struct {
	World  World
	Claims map[domain.PawnID]string
}

func initializeCombatFights(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, `CREATE TABLE combat_fights(plan_id TEXT PRIMARY KEY REFERENCES plans(id), open INTEGER NOT NULL CHECK(open IN (0,1)), memory BLOB NOT NULL, claims BLOB NOT NULL) STRICT;
CREATE TABLE combat_evidence(plan_id TEXT NOT NULL REFERENCES combat_fights(plan_id), sequence INTEGER NOT NULL, tick INTEGER NOT NULL CHECK(tick>=0), payload BLOB NOT NULL, PRIMARY KEY(plan_id,sequence)) STRICT;`)
	return err
}

func checkCombatFightsSchema(ctx context.Context, tx *sql.Tx) error {
	if _, err := tx.ExecContext(ctx, "SELECT plan_id,open,memory,claims FROM combat_fights LIMIT 0"); err != nil {
		return err
	}
	_, err := tx.ExecContext(ctx, "SELECT plan_id,sequence,tick,payload FROM combat_evidence LIMIT 0")
	return err
}

// CommitCombatFight admits a fight (#910): the goal's method on plan, which
// has no actions, and its open fight row in one transaction, with a claim
// of unknown id for every pawn of roster (the admission batch drafts them).
func (s *Store) CommitCombatFight(ctx context.Context, goal domain.GoalID, revision uint64, method domain.MethodID, plan domain.PlanSpec, memory policy.CombatMemory, world World, roster []domain.PawnID) (GoalState, error) {
	if err := plan.Validate(); err != nil {
		return GoalState{}, err
	}
	if len(plan.Actions()) != 0 || len(roster) == 0 || world.Validate() != nil {
		return GoalState{}, errors.New("combat fight needs an empty plan, a roster and a world")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return GoalState{}, err
	}
	defer tx.Rollback()
	state, err := commitGoalMethod(ctx, tx, goal, revision, method, "", plan)
	if err != nil {
		return GoalState{}, err
	}
	if err = insertCombatFight(ctx, tx, plan.ID(), memory, world, roster); err != nil {
		return GoalState{}, err
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

func insertCombatFight(ctx context.Context, tx *sql.Tx, plan domain.PlanID, memory policy.CombatMemory, world World, roster []domain.PawnID) error {
	encoded, err := json.Marshal(memory)
	if err != nil {
		return err
	}
	held := combatClaims{World: world, Claims: map[domain.PawnID]string{}}
	for _, pawn := range roster {
		held.Claims[pawn] = ""
	}
	claims, err := json.Marshal(held)
	if err != nil {
		return err
	}
	_, err = tx.ExecContext(ctx, "INSERT OR IGNORE INTO combat_fights(plan_id,open,memory,claims) VALUES(?,1,?,?)", string(plan), encoded, claims)
	return err
}

func decodeCombatFight(open int, memory, claims []byte) (CombatFight, error) {
	out := CombatFight{Open: open == 1}
	if err := json.Unmarshal(memory, &out.Memory); err != nil {
		return CombatFight{}, err
	}
	var held combatClaims
	if err := json.Unmarshal(claims, &held); err != nil {
		return CombatFight{}, err
	}
	out.World, out.Claims = held.World, held.Claims
	if out.Claims == nil {
		out.Claims = map[domain.PawnID]string{}
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
	var memory, claims []byte
	err := db.QueryRowContext(ctx, "SELECT open,memory,claims FROM combat_fights WHERE plan_id=?", string(plan)).Scan(&open, &memory, &claims)
	if errors.Is(err, sql.ErrNoRows) {
		return CombatFight{}, false, nil
	}
	if err != nil {
		return CombatFight{}, false, err
	}
	out, err := decodeCombatFight(open, memory, claims)
	return out, err == nil, err
}

// combatFightHolds reports whether plan is a fight still holding its goal
// open (CombatFight.Holds).
func combatFightHolds(ctx context.Context, tx *sql.Tx, plan domain.PlanID) (bool, error) {
	fight, ok, err := loadCombatFight(ctx, tx, plan)
	return ok && fight.Holds(), err
}

// HeldCombatFights is every fight that is open or still holds a claim.
func (s *Store) HeldCombatFights(ctx context.Context) (map[domain.PlanID]CombatFight, error) {
	rows, err := s.db.QueryContext(ctx, "SELECT plan_id,open,memory,claims FROM combat_fights")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := map[domain.PlanID]CombatFight{}
	for rows.Next() {
		var id string
		var open int
		var memory, claims []byte
		if err = rows.Scan(&id, &open, &memory, &claims); err != nil {
			return nil, err
		}
		fight, err := decodeCombatFight(open, memory, claims)
		if err != nil {
			return nil, err
		}
		if fight.Holds() {
			out[domain.PlanID(id)] = fight
		}
	}
	return out, rows.Err()
}

// RecordCombatClaims updates plan's claims: set records claim ids learned
// (a pawn the fight does not hold is added), drop forgets pawns whose claim
// was refused or released.
func (s *Store) RecordCombatClaims(ctx context.Context, plan domain.PlanID, set map[domain.PawnID]string, drop []domain.PawnID) error {
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
	for pawn, claim := range set {
		fight.Claims[pawn] = claim
	}
	for _, pawn := range drop {
		delete(fight.Claims, pawn)
	}
	claims, err := json.Marshal(combatClaims{World: fight.World, Claims: fight.Claims})
	if err != nil {
		return err
	}
	if _, err = tx.ExecContext(ctx, "UPDATE combat_fights SET claims=? WHERE plan_id=?", claims, string(plan)); err != nil {
		return err
	}
	return tx.Commit()
}

// CloseCombatFight ends plan's fight: its claims are released.
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
