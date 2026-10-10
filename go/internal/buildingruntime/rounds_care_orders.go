package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// RoundsCareBillDeclarer declares the bills of MaintainSurgery (the part a
// restore operation lacks), MaintainBabyFeeding (baby-edible food) and
// MaintainMechs (the next gestation) to the work ledger (OrderDeclarer). Each is
// the one bill its selector chooses while its concern is workable; a bill
// already standing makes its need and the selector chooses none. All three are
// declare-only in the first pass: the ledger places and keeps them but never
// removes one, because the bench census classes them by recipe
// (policy.LedgerBillKind). MaintainMedicalReserves' bill is declared by
// RoundsMedicalPlanner.
type RoundsCareBillDeclarer struct {
	reviewer            *Rounder
	native              BillPlannerNative
	surgery, baby, mech bool
}

// NewRoundsCareBillDeclarer declares the bills of the concerns named. native
// serves the mechs read the gestation selection needs.
func NewRoundsCareBillDeclarer(reviewer *Rounder, native BillPlannerNative, surgery, baby, mech bool) *RoundsCareBillDeclarer {
	return &RoundsCareBillDeclarer{reviewer: reviewer, native: native, surgery: surgery, baby: baby, mech: mech}
}

// concerns are the enabled concerns, in declaration order.
func (d *RoundsCareBillDeclarer) concerns() []policy.ConcernID {
	var out []policy.ConcernID
	for _, c := range []struct {
		id      policy.ConcernID
		enabled bool
	}{{policy.MaintainSurgery, d.surgery}, {policy.MaintainBabyFeeding, d.baby}, {policy.MaintainMechs, d.mech}} {
		if c.enabled {
			out = append(out, c.id)
		}
	}
	return out
}

// DeclareOrders collects each enabled concern's declaration. A concern that is
// not workable declares nothing; an unread fact abstains.
func (d *RoundsCareBillDeclarer) DeclareOrders(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, benches []policy.GearBench) (policy.Declared, error) {
	journal := d.reviewer.player.journal
	review, err := journal.LoadRounds(ctx)
	if err != nil {
		return policy.Declared{}, err
	}
	if !review.Enabled || review.Snapshot != snapshot {
		var out policy.Declared
		for _, c := range d.concerns() {
			out.Merge(policy.Abstaining(policy.UnreadReview).For(c))
		}
		return out, nil
	}
	var out policy.Declared
	collect := func(need policy.ConcernID, enabled bool, declare func() (policy.Declared, error)) error {
		if !enabled {
			return nil
		}
		_, workable, err := journal.WorkableOwner(ctx, review, need)
		if err != nil || !workable {
			return err
		}
		one, err := declare()
		out.Merge(one.For(need))
		return err
	}
	if err = collect(policy.MaintainSurgery, d.surgery, func() (policy.Declared, error) {
		parts, productions := surgeryPartsOn(projection.Facts.MedicalPawns, projection.SurgeryContext(), benches)
		selected, gap := policy.SelectSurgeryPartBill(productions, parts)
		return policy.DeclareSelection(selected, gap == "", benches), nil
	}); err != nil {
		return policy.Declared{}, err
	}
	if err = collect(policy.MaintainBabyFeeding, d.baby, func() (policy.Declared, error) {
		babies, known := projection.Facts.BabyFeeding.Value()
		if !known {
			return policy.Abstaining(policy.UnreadBabyFeeding), nil
		}
		selected, ok := policy.SelectProductionBill(policy.BabyFoodBill, projection.ProductionBenches, projection.Facts.Colonists, domain.Fact[float64]{}, domain.Fact[float64]{}, 1, policy.ProductionBillContext{BabyFeeding: &babies})
		return policy.DeclareSelection(selected, ok, benches), nil
	}); err != nil {
		return policy.Declared{}, err
	}
	if err = collect(policy.MaintainMechs, d.mech, func() (policy.Declared, error) {
		gestation, known, err := mechGestation(ctx, d.native, boundary.Identity(snapshot), projection)
		if err != nil {
			return abstainOnRead(ctx, policy.UnreadMechs)
		}
		if !known {
			return policy.Abstaining(policy.UnreadMechs), nil
		}
		selected, gap, err := policy.SelectMechGestationBill(mechBenches(benches), gestation)
		if err != nil {
			return policy.Declared{}, err
		}
		return policy.DeclareSelection(selected, gap == "", benches), nil
	}); err != nil {
		return policy.Declared{}, err
	}
	return out, nil
}
