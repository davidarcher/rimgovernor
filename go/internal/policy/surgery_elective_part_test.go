package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The one chosen affordable elective whose part is off the map feeds
// the trade when nothing can fabricate it; served parts win, fabrication
// wins, an unaffordable elective never.
func TestElectivePartPurchase(t *testing.T) {
	short := func(op SurgeryOperation) SurgeryOperation {
		op.IngredientsOnMap = domain.Known(false)
		return op
	}
	eye := func(id PawnID) CarePawn {
		return wholePawn(id, 0, short(electiveOp("InstallBionicEye", "Eye", 5, 0.97)))
	}
	arm := func(id PawnID) CarePawn {
		return wholePawn(id, 0, short(electiveOp("InstallBionicArm", "Arm", 20, 0.97)))
	}
	buy := func(pawns []CarePawn, remaining map[PawnID]float64, fabricable map[Resource]bool) []SurgeryPart {
		known := domain.Known(pawns)
		ctx := SurgeryContext{HospitalBed: true, Elective: electiveShares(remaining)}
		return SurgeryPurchaseParts(known, ctx, SurgeryParts(SelectSurgery(known, nil, SurgeryContext{}).Wants), fabricable)
	}
	rows := []TradeSheetRowFact{
		{DefName: "BionicEye", TraderCount: 3, BuyPriceKnown: true, BuyPrice: 1400},
		{DefName: "BionicArm", TraderCount: 3, BuyPriceKnown: true, BuyPrice: 6000},
	}
	t.Run("only the chosen affordable elective", func(t *testing.T) {
		got := buy([]CarePawn{arm("a"), eye("b"), eye("c")}, map[PawnID]float64{"a": 500, "b": 1000, "c": 1000}, nil)
		if len(got) != 1 || got[0].Pawn != "b" || got[0].Items[0] != "BionicEye" {
			t.Fatalf("parts %+v", got)
		}
		targets := surgeryPartTargets(got, rows, map[string]bool{})
		if len(targets) != 1 || targets[0].Item != "BionicEye" || targets[0].MaxBuy != 1 || targets[0].Stock != 1 {
			t.Fatalf("targets %+v", targets)
		}
	})
	t.Run("unaffordable demands nothing", func(t *testing.T) {
		if got := buy([]CarePawn{eye("a")}, map[PawnID]float64{"a": 100}, nil); got != nil {
			t.Fatalf("parts %+v", got)
		}
	})
	t.Run("fabricable is not bought", func(t *testing.T) {
		if got := buy([]CarePawn{eye("a")}, map[PawnID]float64{"a": 1000}, map[Resource]bool{"BionicEye": true}); got != nil {
			t.Fatalf("parts %+v", got)
		}
	})
	t.Run("price ceiling holds", func(t *testing.T) {
		got := buy([]CarePawn{arm("a")}, map[PawnID]float64{"a": 1e9}, nil)
		if len(got) != 1 || len(surgeryPartTargets(got, rows, map[string]bool{})) != 0 {
			t.Fatalf("parts %+v", got)
		}
	})
	t.Run("a served want holds the elective back", func(t *testing.T) {
		leg := wholePawn("z", 0, restoreOp("InstallBionicLeg", "Leg", 30, 0.9, 1, false))
		got := buy([]CarePawn{eye("a"), leg}, map[PawnID]float64{"a": 1000}, nil)
		if len(got) != 1 || got[0].Items[0] != "BionicLeg" {
			t.Fatalf("served only: %+v", got)
		}
	})
	t.Run("a queued or stocked elective holds another back", func(t *testing.T) {
		stocked := wholePawn("a", 0, electiveOp("InstallBionicArm", "Arm", 20, 0.97))
		if got := buy([]CarePawn{stocked, eye("b")}, map[PawnID]float64{"a": 2000, "b": 1000}, nil); got != nil {
			t.Fatalf("stocked elective: %+v", got)
		}
		queued := wholePawn("a", 1)
		if got := buy([]CarePawn{queued, eye("b")}, map[PawnID]float64{"b": 1000}, nil); got != nil {
			t.Fatalf("queued bill: %+v", got)
		}
	})
	t.Run("no hospital bed", func(t *testing.T) {
		known := domain.Known([]CarePawn{eye("a")})
		ctx := SurgeryContext{Elective: electiveShares(map[PawnID]float64{"a": 1000})}
		if got := SurgeryPurchaseParts(known, ctx, nil, nil); got != nil {
			t.Fatalf("parts %+v", got)
		}
	})
}
