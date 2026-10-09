package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"slices"
)

const GovernorCombatRestorationKey = "family/combat_restoration"
const maxCombatRestorationBytes = 128 * 1024

// CombatRestoration is save-owned intent to undo temporary fight settings.
// It contains original settings only, never attacks, attempts or receipts.
type CombatRestoration struct {
	World   World
	Owner   domain.PlanID
	Doors   []CombatDoorRestoration
	Animals []CombatAnimalRestoration
}
type CombatDoorRestoration struct {
	Cell                domain.Cell
	HoldOpen, Forbidden bool
}
type CombatAnimalRestoration struct {
	Pawn domain.PawnID
	Area string
}

func (r CombatRestoration) Validate() error {
	if r.World.Validate() != nil || !validIdentity(string(r.Owner)) || len(r.Doors)+len(r.Animals) == 0 || len(r.Doors)+len(r.Animals) > 256 {
		return ErrCapacity
	}
	doors := map[domain.Cell]bool{}
	for _, d := range r.Doors {
		if doors[d.Cell] || d.Cell.X < 0 || d.Cell.Z < 0 {
			return ErrConflict
		}
		doors[d.Cell] = true
	}
	animals := map[domain.PawnID]bool{}
	for _, a := range r.Animals {
		if animals[a.Pawn] || !validIdentity(string(a.Pawn)) || a.Area != "" && !validIdentity(a.Area) {
			return ErrConflict
		}
		animals[a.Pawn] = true
	}
	return nil
}
func loadCombatRestoration(ctx context.Context, tx *sql.Tx) (CombatRestoration, bool, error) {
	var raw []byte
	err := tx.QueryRowContext(ctx, "SELECT payload FROM combat_restoration WHERE singleton=1").Scan(&raw)
	if errors.Is(err, sql.ErrNoRows) {
		return CombatRestoration{}, false, nil
	}
	if err != nil {
		return CombatRestoration{}, false, err
	}
	var r CombatRestoration
	if err = json.Unmarshal(raw, &r); err != nil {
		return r, false, err
	}
	return r, true, r.Validate()
}
func (s *Store) LoadCombatRestoration(ctx context.Context) (CombatRestoration, bool, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return CombatRestoration{}, false, err
	}
	defer tx.Rollback()
	r, ok, err := loadCombatRestoration(ctx, tx)
	if err != nil {
		return r, false, err
	}
	return r, ok, tx.Commit()
}
func (s *Store) SaveCombatRestoration(ctx context.Context, r CombatRestoration) error {
	if err := r.Validate(); err != nil {
		return err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	old, exists, err := loadCombatRestoration(ctx, tx)
	if err != nil {
		return err
	}
	if exists {
		if old.Owner != r.Owner || old.World != r.World {
			return ErrConflict
		}
		for _, d := range old.Doors {
			if !slices.Contains(r.Doors, d) {
				return ErrConflict
			}
		}
		for _, a := range old.Animals {
			if !slices.Contains(r.Animals, a) {
				return ErrConflict
			}
		}
	}
	if err = putSingleton(ctx, tx, "combat_restoration", r, maxCombatRestorationBytes); err != nil {
		return err
	}
	return tx.Commit()
}

// CombatRestorationActions uses the existing combat wire for doors and temporary
// area deletion, followed by native husbandry to restore each original area.
func CombatRestorationActions(r CombatRestoration, id domain.PlanID) ([]domain.Action, error) {
	if err := r.Validate(); err != nil {
		return nil, err
	}
	var commands []domain.CombatCommand
	for _, d := range r.Doors {
		mode := "close"
		if d.HoldOpen {
			mode = "hold_open"
		}
		commands = append(commands, domain.CombatCommand{Kind: "door", Cell: d.Cell, Door: mode})
		mode = "allow"
		if d.Forbidden {
			mode = "forbid"
		}
		commands = append(commands, domain.CombatCommand{Kind: "door", Cell: d.Cell, Door: mode})
	}
	for _, a := range r.Animals {
		commands = append(commands, domain.CombatCommand{Kind: "animal_clear", Pawn: a.Pawn})
	}
	if len(commands) == 0 {
		return nil, ErrConflict
	}
	batch, err := domain.NewCombatBatch(r.Owner, string(id), commands)
	if err != nil {
		return nil, err
	}
	action, err := domain.NewCombatBatchAction(domain.ActionID(string(id)+"-settings"), batch)
	if err != nil {
		return nil, err
	}
	actions := []domain.Action{action}
	for i, animal := range r.Animals {
		h, err := domain.NewHusbandry(animal.Pawn, domain.HusbandryAllowedArea, animal.Area)
		if err != nil {
			return nil, err
		}
		a, err := domain.NewHusbandryAction(domain.ActionID(fmt.Sprintf("%s-animal-%d", id, i)), h)
		if err != nil {
			return nil, err
		}
		actions = append(actions, a)
	}
	return actions, nil
}
func combatRestorationPlan(ctx context.Context, tx *sql.Tx, p PlanState) bool {
	r, ok, err := loadCombatRestoration(ctx, tx)
	if err != nil || !ok {
		return false
	}
	expected, err := CombatRestorationActions(r, p.Spec.ID())
	if err != nil || len(expected) != len(p.Spec.Actions()) {
		return false
	}
	for i, a := range expected {
		if p.Spec.Actions()[i] != a {
			return false
		}
	}
	return true
}
func (s *Store) CommitCombatRestoration(ctx context.Context, id domain.IncidentID, plan domain.PlanSpec) error {
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if plan.Validate() != nil || !combatRestorationPlan(ctx, tx, PlanState{Spec: plan}) {
		return ErrConflict
	}
	var exists int
	if err = tx.QueryRowContext(ctx, "SELECT count(*) FROM plans WHERE id=?", plan.ID()).Scan(&exists); err != nil {
		return err
	}
	if exists != 0 {
		old, err := load(ctx, tx, plan.ID())
		if err != nil {
			return err
		}
		if !combatRestorationPlan(ctx, tx, old) {
			return ErrConflict
		}
		return tx.Commit()
	}
	state, err := loadIncident(ctx, tx, id)
	if err != nil {
		return err
	}
	if state.Incident.Closed {
		return ErrConflict
	}
	// A fresh restoration decision transfers the same saved originals. Retire
	// earlier cleanup attempts without changing their historical uncertainty.
	var priorIDs []domain.PlanID
	for _, method := range state.Methods {
		prior, err := load(ctx, tx, method.Plan)
		if err != nil {
			return err
		}
		if !combatRestorationPlan(ctx, tx, prior) {
			continue
		}
		priorIDs = append(priorIDs, prior.Spec.ID())
		if _, err = tx.ExecContext(ctx, "UPDATE plans SET retired=1 WHERE id=?", prior.Spec.ID()); err != nil {
			return err
		}
	}
	if err = bindOwnerMethod(ctx, tx, state, domain.MethodID("restore-"+string(plan.ID())), "", plan); err != nil {
		return err
	}
	for _, prior := range priorIDs {
		if _, err = tx.ExecContext(ctx, "UPDATE plans SET superseded_by=? WHERE id=?", plan.ID(), prior); err != nil {
			return err
		}
	}
	return tx.Commit()
}

// FinishCombatRestoration drops saved intent only after every setting action
// completed; retiring its method and transferred batches is one transaction.
func (s *Store) FinishCombatRestoration(ctx context.Context, owner, plan domain.PlanID) error {
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	p, err := load(ctx, tx, plan)
	if err != nil {
		return err
	}
	if !combatRestorationPlan(ctx, tx, p) {
		return ErrConflict
	}
	for _, v := range p.Progress {
		if v.View().Stage != domain.Completed || v.View().Unresolved {
			return ErrConflict
		}
	}
	kept, ok, err := loadCombatRestoration(ctx, tx)
	if err != nil {
		return err
	}
	if !ok || kept.Owner != owner {
		return ErrConflict
	}
	if err = supersedeCombatBatches(ctx, tx, owner, plan, true); err != nil {
		return err
	}
	if _, err = tx.ExecContext(ctx, "DELETE FROM combat_restoration"); err != nil {
		return err
	}
	if _, err = tx.ExecContext(ctx, "UPDATE plans SET retired=1 WHERE id=?", plan); err != nil {
		return err
	}
	return tx.Commit()
}
