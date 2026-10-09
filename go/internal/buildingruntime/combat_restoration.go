package buildingruntime

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"slices"
)

var errCombatRestorationPending = errors.New("combat restoration pending")

// captureCombatSettings commits original settings before the first temporary
// mutation. Subsequent tactical changes never replace the original values.
func (r *RoundsDefensePlanner) captureCombatSettings(ctx context.Context, state ControlState, fight domain.PlanID, orders []policy.CombatOrder) error {
	if !slices.ContainsFunc(orders, func(o policy.CombatOrder) bool { return o.Kind == policy.OrderDoor || o.Kind == policy.OrderAnimalArea }) {
		return nil
	}
	kept, exists, err := r.reviewer.player.journal.LoadCombatRestoration(ctx)
	if err != nil {
		return err
	}
	world := store.World{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map}
	if exists && (kept.Owner != fight || kept.World != world) {
		return fmt.Errorf("%w: prior combat restoration pending", ErrControl)
	}
	if !exists {
		kept = store.CombatRestoration{World: world, Owner: fight}
	}
	combat, err := r.native.ReadCombat(ctx, boundary.Identity(state.Snapshot))
	if err != nil {
		return err
	}
	doors, doorsKnown := combat.DoorStates.Value()
	for _, o := range orders {
		if o.Kind == policy.OrderDoor && !slices.ContainsFunc(kept.Doors, func(d store.CombatDoorRestoration) bool { return d.Cell == o.Cell }) {
			i := slices.IndexFunc(doors, func(d policy.RoomDoor) bool { return d.Cell == o.Cell })
			if !doorsKnown || i < 0 {
				return fmt.Errorf("%w: original door setting unknown", ErrControl)
			}
			hold, hk := doors[i].HoldOpen.Value()
			forbidden, fk := doors[i].Forbidden.Value()
			if !hk || !fk {
				return fmt.Errorf("%w: original door setting unknown", ErrControl)
			}
			kept.Doors = append(kept.Doors, store.CombatDoorRestoration{Cell: o.Cell, HoldOpen: hold, Forbidden: forbidden})
		}
		if o.Kind == policy.OrderAnimalArea && !slices.ContainsFunc(kept.Animals, func(a store.CombatAnimalRestoration) bool { return a.Pawn == o.Pawn }) {
			p, ok := combat.Detail.Get(string(o.Pawn))
			if !ok || p == nil || p.AnimalState == nil || p.AnimalState.SupportsAllowedAreas == nil {
				return fmt.Errorf("%w: original animal area unknown", ErrControl)
			}
			for _, issue := range p.AnimalState.Issues {
				if issue.GetField() == "allowed_area" {
					return fmt.Errorf("%w: original animal area unknown", ErrControl)
				}
			}
			kept.Animals = append(kept.Animals, store.CombatAnimalRestoration{Pawn: o.Pawn, Area: p.AnimalState.GetAllowedAreaId()})
		}
	}
	return r.reviewer.player.journal.SaveCombatRestoration(ctx, kept)
}

// restoreCombatSettings issues only settings from save-owned restoration intent.
// A reload starts a fresh cleanup method; historical tactical batches stay dead.
func (r *RoundsDefensePlanner) restoreCombatSettings(ctx context.Context, state ControlState, kept store.CombatRestoration) error {
	if kept.World.Colony != state.Snapshot.Colony || kept.World.Map != state.Snapshot.Map {
		return nil
	}
	combat, err := r.native.ReadCombat(ctx, boundary.Identity(state.Snapshot))
	if err != nil {
		return err
	}
	tick := domain.Tick(combat.Context.GetTick())
	incident, err := r.reviewer.player.journal.OpenIncident(ctx, store.IncidentAssessment{Kind: policy.ActiveCombat, Trigger: "restore_combat_settings", Snapshot: state.Snapshot, Tick: tick})
	if err != nil {
		return err
	}
	payload, err := json.Marshal(kept)
	if err != nil {
		return err
	}
	sum := sha256.Sum256(append([]byte(fmt.Sprintf("%s/%s/%d/", kept.Owner, state.Snapshot.Load, tick)), payload...))
	id := domain.PlanID(fmt.Sprintf("restore-%x", sum[:]))
	actions, err := store.CombatRestorationActions(kept, id)
	if err != nil {
		return err
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return err
	}
	if err = r.reviewer.player.journal.CommitCombatRestoration(ctx, incident.Incident.ID, plan); err != nil {
		return err
	}
	ids := make([]domain.ActionID, len(actions))
	for i, a := range actions {
		ids[i] = a.ID()
	}
	items, err := r.hands.RunBatch(ctx, id, ids)
	if err != nil {
		return err
	}
	if len(items) != len(actions) {
		return ErrControl
	}
	for _, item := range items {
		if item.Err != nil {
			return fmt.Errorf("%w: %v", errCombatRestorationPending, item.Err)
		}
		if item.Result.Progress.View().Stage != domain.Completed {
			return fmt.Errorf("%w: uncertain", errCombatRestorationPending)
		}
		if result, known := item.Result.Progress.View().Combat.Value(); known {
			for _, v := range result.Orders() {
				if !v.Applied && v.Refusal != "not_a_door" && v.Refusal != "not_found" && v.Refusal != "not_ours" {
					return fmt.Errorf("%w: refused %s", errCombatRestorationPending, v.Refusal)
				}
			}
		}
	}
	return r.reviewer.player.journal.FinishCombatRestoration(ctx, kept.Owner, id)
}
func (r *RoundsDefensePlanner) restoreReloadedCombat(ctx context.Context, state ControlState) error {
	kept, ok, err := r.reviewer.player.journal.LoadCombatRestoration(ctx)
	if err != nil || !ok {
		return err
	}
	fight, found, err := r.reviewer.player.journal.LoadCombatFight(ctx, kept.Owner)
	if err != nil {
		return err
	}
	if kept.World.Load == state.Snapshot.Load && found && fight.Open {
		return nil
	}
	return r.restoreCombatSettings(ctx, state, kept)
}
