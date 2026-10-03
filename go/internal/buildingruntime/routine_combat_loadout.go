package buildingruntime

import (
	"context"
	"fmt"
	"log/slog"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// shieldBeltDef is the stocked apparel a melee defender wears (#1048).
const shieldBeltDef = "Apparel_ShieldBelt"

// loadoutWeaponSource is the loose-weapon read a fight's loadout takes
// (#1115); a source without it gets no weapon swaps.
type loadoutWeaponSource interface {
	ReadMapBounds(context.Context, *c.Identity, domain.Cell) (bridge.MapBounds, bridge.Result, error)
	ReadEquipWeapons(context.Context, *c.Identity, domain.Cell, domain.Cell) (bridge.EquipRead, bridge.Result, error)
}

// fightLoadout is the threat loadout (#1048) for the defenders a fight
// admits (#1115): policy.ThreatLoadout over the frame's defender rows, the
// map's loose weapons and the stocked shield belts the gear census offers.
// A loadout is optional: a failed read is logged and leaves that half out.
func (r *RoutineDefensePlanner) fightLoadout(call context.Context, state ControlState, view policy.CombatView, rows map[string]*n.PawnState, pawns []domain.PawnID, catalog *bridge.DefinitionCatalog) []policy.LoadoutOrder {
	threat := policy.ClassifyLoadoutThreat(view, false)
	live := map[domain.PawnID]policy.CombatPawnState{}
	for _, p := range view.Pawns {
		live[p.ID] = p
	}
	things, _ := frameThings(call, r.native, boundary.Identity(state.Snapshot))
	var defenders []policy.LoadoutDefender
	beltless := false
	for _, pawn := range pawns {
		row := rows[string(pawn)]
		if row == nil || row.Pawn == nil {
			continue
		}
		facts, err := equipCandidatePawnFacts(row, catalog)
		if err != nil {
			slog.Default().InfoContext(call, "fight loadout pawn: "+err.Error(), telemetry.ComponentKey, "routine-defense")
			return nil
		}
		d := policy.LoadoutDefender{EquipCandidatePawn: facts, Primary: primaryDef(row, things)}
		if p, ok := live[pawn]; ok {
			d.ShieldBelt = p.ShieldBelt
			if p.Weapon != "" {
				d.Primary = p.Weapon
			}
		}
		// The primary's def rows (#1723): a def the catalog cannot state
		// leaves the loadout out, as any failed read does.
		if d.PrimaryFacts, err = catalog.WeaponOf(d.Primary); err != nil {
			slog.Default().InfoContext(call, "fight loadout primary: "+err.Error(), telemetry.ComponentKey, "routine-defense")
			return nil
		}
		beltless = beltless || !d.ShieldBelt
		defenders = append(defenders, d)
	}
	if len(defenders) == 0 {
		return nil
	}
	identity := boundary.Identity(state.Snapshot)
	var weapons []policy.EquipCandidateWeapon
	if source, ok := r.native.(loadoutWeaponSource); ok && threat != policy.LoadoutNone {
		read, err := loadoutWeapons(call, source, identity, state, catalog)
		if err != nil {
			slog.Default().InfoContext(call, "fight loadout weapons: "+err.Error(), telemetry.ComponentKey, "routine-defense")
		}
		weapons = read
	}
	var belts []policy.LoadoutApparel
	if source, ok := r.native.(colonyFactsReader); ok && beltless {
		read, err := r.loadoutBelts(call, source, identity, state)
		if err != nil {
			slog.Default().InfoContext(call, "fight loadout belts: "+err.Error(), telemetry.ComponentKey, "routine-defense")
		}
		belts = read
	}
	return policy.ThreatLoadout(threat, defenders, weapons, belts)
}

func loadoutWeapons(call context.Context, source loadoutWeaponSource, identity *c.Identity, state ControlState, catalog *bridge.DefinitionCatalog) ([]policy.EquipCandidateWeapon, error) {
	bounds, _, err := source.ReadMapBounds(call, identity, domain.Cell{})
	if err != nil {
		return nil, err
	}
	if _, err = boundary.Context(bounds.Context, state.Snapshot); err != nil || bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0 {
		return nil, fmt.Errorf("%w: loadoutWeapons: bounds", ErrControl)
	}
	read, _, err := source.ReadEquipWeapons(call, identity, domain.Cell{}, domain.Cell{X: bounds.Bounds.Width - 1, Z: bounds.Bounds.Height - 1})
	if err != nil {
		return nil, err
	}
	if _, err = boundary.Context(read.Context, state.Snapshot); err != nil {
		return nil, fmt.Errorf("%w: loadoutWeapons: context", ErrControl)
	}
	out := make([]policy.EquipCandidateWeapon, 0, len(read.Targets))
	for _, w := range read.Targets {
		candidate, err := equipCandidateWeapon(catalog, w)
		if err != nil {
			return nil, err
		}
		out = append(out, candidate)
	}
	return out, nil
}

// loadoutBelts are the stocked shield belts the gear census offers any
// colonist as a wear candidate.
func (r *RoutineDefensePlanner) loadoutBelts(call context.Context, source colonyFactsReader, identity *c.Identity, state ControlState) ([]policy.LoadoutApparel, error) {
	reply, _, err := r.reviewer.colonyFacts(call, source, identity, true)
	if err != nil {
		return nil, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return nil, fmt.Errorf("%w: loadoutBelts: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return nil, fmt.Errorf("%w: loadoutBelts: context", ErrControl)
	}
	things, err := frameThings(call, source, identity)
	if err != nil {
		return nil, err
	}
	seen := map[string]bool{}
	var out []policy.LoadoutApparel
	for _, pawn := range observed.GetPlanning().GetObserved().GetGear().GetPawns() {
		for _, candidate := range pawn.GetCandidates() {
			thing := candidate.GetItem().GetThing()
			row, _ := things.Row(thing)
			if row.GetThing().GetDefName() != shieldBeltDef || thing.GetId() == "" || seen[thing.GetId()] {
				continue
			}
			seen[thing.GetId()] = true
			item := policy.LoadoutApparel{Thing: thing.GetId(), Definition: shieldBeltDef}
			if at := row.GetThing().GetPosition(); at != nil {
				item.Cell = domain.Cell{X: at.GetX(), Z: at.GetZ()}
			}
			out = append(out, item)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Thing < out[j].Thing })
	return out, nil
}

// primaryDef is a detail row's equipped primary def, "" unarmed.
func primaryDef(row *n.PawnState, things bridge.Things) string {
	equipment := row.GetEquipment()
	for _, item := range equipment.GetEquipped() {
		if id := equipment.GetPrimaryId(); id != "" && item.GetThing().GetId() == id {
			return gearDef(things, item.GetThing())
		}
	}
	return ""
}

// loadoutActions are the fight plan's equip and wear actions (#1115),
// committed before its first combat.orders batch, and the pawns they hold
// back from drafting: a drafted pawn's wear is refused natively
// (GearUpkeepTools.Available), so a loadout pawn equips before it drafts.
func loadoutActions(plan domain.PlanID, orders []policy.LoadoutOrder) ([]domain.Action, map[domain.PawnID]bool, error) {
	held := map[domain.PawnID]bool{}
	actions := make([]domain.Action, 0, len(orders))
	for i, order := range orders {
		id := domain.ActionID(fmt.Sprintf("%s-loadout-%d", plan, i))
		var action domain.Action
		if order.Wear {
			wear, err := domain.NewGearReplace(order.Pawn, order.Thing, order.Definition)
			if err != nil {
				return nil, nil, err
			}
			if action, err = domain.NewGearReplaceAction(id, wear); err != nil {
				return nil, nil, err
			}
		} else {
			equip, err := domain.NewEquip(order.Pawn, order.Thing, order.Definition, order.Cell)
			if err != nil {
				return nil, nil, err
			}
			if action, err = domain.NewEquipAction(id, equip); err != nil {
				return nil, nil, err
			}
		}
		held[order.Pawn] = true
		actions = append(actions, action)
	}
	return actions, held, nil
}

// loadoutPending are the pawns whose loadout action in the fight plan is
// still open; they draft once it settles.
func loadoutPending(plan store.PlanState) map[domain.PawnID]bool {
	out := map[domain.PawnID]bool{}
	for _, progress := range plan.Progress {
		if !domain.GoalWorkOpen([]domain.Progress{progress}) {
			continue
		}
		if equip, ok := progress.Action().Equip(); ok {
			out[equip.Pawn()] = true
		} else if wear, ok := progress.Action().GearReplace(); ok {
			out[wear.Pawn()] = true
		}
	}
	return out
}
