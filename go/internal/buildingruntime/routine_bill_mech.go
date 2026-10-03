package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// mechSource is the native read MaintainMechs needs beyond the bill census:
// the mechs a mechanitor controls, by id (their kinds feed the role choice).
type mechSource interface {
	ReadMechs(context.Context, *c.Identity, []string) ([]policy.MechInput, error)
}

var _ mechSource = (*bridge.Client)(nil)

// mechGestation assembles the gestation goal's inputs (#1686) from the
// projection: the colonist mechanitors (pawn rows), the gestators and
// wastepacks (Biotech colony section), the mech catalog and the work roster,
// and the mechs the mechanitors control (one read by id). known is false
// while the colony section or the pawn facts are unread: the goal then
// decides nothing.
func mechGestation(call context.Context, native any, identity *c.Identity, projection observation.ColonyProjection) (g policy.MechGestation, known bool, err error) {
	colony, ck := projection.Biotech.Value()
	pawns, pk := projection.WorkPawns.Value()
	if !ck || !pk {
		return g, false, nil
	}
	g.Catalog = projection.MechCatalog
	var ids []string
	for _, p := range pawns {
		bt, ok := p.Biotech.Value()
		if !ok {
			continue
		}
		if m, mk := bt.Mechanitor.Value(); mk && m != nil {
			g.Mechanitors = append(g.Mechanitors, policy.MechanitorInput{ID: p.ID, PawnMechanitor: *m})
			ids = append(ids, m.ControlledMechs...)
		}
	}
	if len(ids) > 0 {
		source, ok := native.(mechSource)
		if !ok {
			return g, false, fmt.Errorf("%w: mechGestation: native cannot read mechs", ErrControl)
		}
		if g.Mechs, err = source.ReadMechs(call, identity, ids); err != nil {
			return g, false, err
		}
	}
	if roster, ok := projection.Facts.WorkRoster.Value(); ok {
		g.Coverage = roster
	}
	for _, x := range colony.Gestators {
		_, billed := x.BillID.Value()
		g.Gestators = append(g.Gestators, policy.GestatorFact{ID: x.ID, Active: domain.Known(billed), WasteCount: x.WasteCount})
	}
	for _, x := range colony.Wastepacks {
		g.Wastepacks = append(g.Wastepacks, policy.WastepackFact{Count: x.Count, Frozen: x.Frozen, InAtomizer: x.InAtomizer})
	}
	g.Chargers = colony.MechChargerRows()
	return g, true, nil
}

// mechBenches converts the gear bench census into production benches that
// carry each recipe's mech kind (RecipeState.mech_kind) and the bills
// standing; Available is researched and offered here.
func mechBenches(reads []bridge.GearBenchRead) []policy.ProductionBench {
	benches := partBenches(reads)
	kinds := map[string]map[string]string{}
	for _, read := range reads {
		recipes, ok := read.Bench.Recipes.Value()
		if !ok {
			continue
		}
		kinds[read.Bench.ID] = map[string]string{}
		for _, recipe := range recipes {
			kinds[read.Bench.ID][recipe.Definition] = recipe.MechKind
		}
	}
	for i, bench := range benches {
		for j, recipe := range bench.Recipes {
			benches[i].Recipes[j].MechKind = kinds[bench.ID][recipe.Name]
		}
	}
	return benches
}

// mechSelection is the next gestation bill: the gear bench census for the
// gestators' recipes and bills, the projection for the rest.
func (r *RoutineBillPlanner) mechSelection(call context.Context, state ControlState, projection observation.ColonyProjection) (policy.BillSelection, bool, error) {
	source, ok := r.native.(artBenchSource)
	if !ok {
		return policy.BillSelection{}, false, fmt.Errorf("%w: mechSelection: native cannot read the bill census", ErrControl)
	}
	identity := boundary.Identity(state.Snapshot)
	gestation, known, err := mechGestation(call, r.native, identity, projection)
	if err != nil || !known {
		return policy.BillSelection{}, false, err
	}
	reads, _, err := source.ReadGearBenches(call, identity)
	if err != nil {
		return policy.BillSelection{}, false, err
	}
	return policy.SelectMechGestationBill(mechBenches(reads), gestation)
}
