package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func (r *RoutineGearPlanner) weaponDemand(ctx context.Context, state ControlState, gear *o.GearSnapshot, benches []policy.GearBench) ([]policy.Amount, error) {
	source, ok := r.native.(RoutineEquipSource)
	if !ok {
		return nil, nil
	}
	identity := boundary.Identity(state.Snapshot)
	ids := []string{}
	for _, p := range gear.Pawns {
		ids = append(ids, p.GetPawn().GetId())
	}
	reply, _, err := source.ReadCombatPawns(ctx, identity, ids)
	if err != nil {
		return nil, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return nil, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < gear.Context.GetTick() {
		return nil, ErrControl
	}
	// An exact-ID census counts every other pawn on the map as filtered, so
	// only the requested rows establish completeness: matched, returned and
	// the decoded rows must each cover the whole request, the same contract
	// RoutineEquipPlanner reads the identical census under. Demanding
	// filtered == 0 here refused every real colony (a single animal or
	// visitor is enough) and failed the whole gear step with ErrControl, so
	// MaintainEquipment never planned a wear or bill method past its apparel
	// policies and never recovered (#660).
	counts := observed.Completeness
	if counts == nil || counts.Page == nil || !counts.GetPage().GetComplete() || counts.GetPage().GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != uint64(len(ids)) || counts.GetReturned() != uint64(len(ids)) || len(observed.Pawns) != len(ids) {
		return nil, ErrControl
	}
	pawns := []policy.EquipCandidatePawn{}
	for _, p := range observed.Pawns {
		pawns = append(pawns, equipCandidatePawnFacts(p))
	}
	bounds, _, err := source.ReadMapBounds(ctx, identity, domain.Cell{})
	if err != nil {
		return nil, err
	}
	if _, err = boundary.Context(bounds.Context, state.Snapshot); err != nil || bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0 {
		return nil, ErrControl
	}
	weapons, _, err := source.ReadEquipWeapons(ctx, identity, domain.Cell{}, domain.Cell{X: bounds.Bounds.Width - 1, Z: bounds.Bounds.Height - 1})
	if err != nil {
		return nil, err
	}
	if _, err = boundary.Context(weapons.Context, state.Snapshot); err != nil {
		return nil, ErrControl
	}
	candidates := []policy.EquipCandidateWeapon{}
	for _, w := range weapons.Targets {
		candidates = append(candidates, policy.EquipCandidateWeapon{Thing: w.Thing, Definition: w.Definition, Cell: w.Cell, Class: policy.ClassifyWeapon(w.ByTrade, w.Ranged, w.Melee), BiocodedTo: w.BiocodedTo, Biocoded: w.Biocoded})
	}
	recipes := []policy.GearRecipe{}
	for _, b := range benches {
		rows, known := b.Recipes.Value()
		if !known {
			return nil, ErrControl
		}
		recipes = append(recipes, rows...)
	}
	return policy.WeaponProductionDemand(pawns, candidates, recipes), nil
}
