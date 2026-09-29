package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func partBench(id string, recipes map[string]bool, bills ...string) ProductionBench {
	bench := ProductionBench{ID: id, Token: domain.Known("tok-" + id), Usable: domain.Known(true)}
	for _, name := range []string{"Make_BionicLeg", "Make_SimpleProstheticLeg", "Make_BionicEye"} {
		if available, ok := recipes[name]; ok {
			bench.Recipes = append(bench.Recipes, ProductionRecipe{Name: name, Available: domain.Known(available),
				Products: []ProductionProduct{{Name: name[len("Make_"):]}}})
		}
	}
	for _, bill := range bills {
		bench.Bills = append(bench.Bills, ExistingProductionBill{ID: "b-" + bill, Recipe: bill, Active: domain.Known(true)})
	}
	return bench
}

func legShort() []SurgeryPart {
	leg := []SurgeryOperation{
		restoreOp("InstallPegLeg", "Leg", 30, 0.9, 1, false),
		restoreOp("InstallSimpleProstheticLeg", "Leg", 30, 0.9, 1, false),
		restoreOp("InstallBionicLeg", "Leg", 30, 0.9, 1, false),
	}
	eye := restoreOp("InstallBionicEye", "Eye", 5, 0.9, 1, false)
	return SurgeryParts(SelectSurgery(domain.Known([]CarePawn{surgeryPawn("a", 0, append(leg, eye)...)}), nil).Wants)
}

func TestSurgeryParts(t *testing.T) {
	parts := legShort()
	if len(parts) != 2 || parts[0].Items[0] != "BionicLeg" || parts[0].Items[2] != "PegLeg" || parts[1].Items[0] != "BionicEye" {
		t.Fatalf("parts %+v", parts)
	}
	if parts[0].Priority != 84 || parts[1].Priority != 50 {
		t.Fatalf("priority follows the surgery's rank: %+v", parts)
	}
	demand := SurgeryPartDemand(parts)
	if len(demand) != 2 || demand[0].Key.Def != "BionicLeg" || demand[0].Count != 1 || demand[0].Priority != 84 {
		t.Fatalf("demand %+v", demand)
	}
	noDoctor := SurgeryParts([]SurgeryWant{{Reason: SurgeryNoDoctor, Options: []string{"InstallPegLeg"}}})
	if len(noDoctor) != 0 {
		t.Fatalf("a doctor want is not part demand: %+v", noDoctor)
	}
}

// The bill fabricates a researched part; otherwise the trade buys it.
func TestSurgeryPartBillOrTrade(t *testing.T) {
	parts := legShort()
	for _, c := range []struct {
		name    string
		benches []ProductionBench
		bill    string   // selected recipe, "" none
		trade   []string // items bought
	}{
		{"bionic researched", []ProductionBench{partBench("fab", map[string]bool{"Make_BionicLeg": true, "Make_BionicEye": true})}, "Make_BionicLeg", nil},
		{"only prosthetic researched", []ProductionBench{partBench("mach", map[string]bool{"Make_SimpleProstheticLeg": true, "Make_BionicLeg": false})}, "Make_SimpleProstheticLeg", []string{"BionicEye"}},
		{"nothing researched", []ProductionBench{partBench("fab", map[string]bool{"Make_BionicLeg": false})}, "", []string{"BionicLeg", "BionicEye"}},
		{"no bench", nil, "", []string{"BionicLeg", "BionicEye"}},
		{"leg in production, eye next", []ProductionBench{partBench("fab", map[string]bool{"Make_BionicLeg": true, "Make_BionicEye": true}, "Make_BionicLeg")}, "Make_BionicEye", nil},
	} {
		t.Run(c.name, func(t *testing.T) {
			got, ok := SelectProductionBill(SurgeryPartBill, domain.Known(c.benches), domain.Known[int64](3), domain.Fact[float64]{}, domain.Fact[float64]{}, 1, ProductionBillContext{Parts: parts})
			if ok != (c.bill != "") || ok && (got.Recipe != c.bill || got.Mode != domain.GearBatch || got.Target != 1) {
				t.Fatalf("bill %+v %v", got, ok)
			}
			need := SurgeryTradeNeed(domain.Known(TradeNeed{}), TradeSurgeryParts(parts, FabricableParts(c.benches)))
			n, _ := need.Value()
			if n.Any() != (len(c.trade) > 0) {
				t.Fatalf("need %+v", n)
			}
			rows := []TradeSheetRowFact{
				{DefName: "BionicLeg", TraderCount: 1, BuyPriceKnown: true, BuyPrice: 1500},
				{DefName: "BionicEye", TraderCount: 2, BuyPriceKnown: true, BuyPrice: 1400},
			}
			targets := routineTradeTargets(n, rows, nil, RoutineTradePolicy{}).Targets
			if len(targets) != len(c.trade) {
				t.Fatalf("targets %+v", targets)
			}
			for i, item := range c.trade {
				if targets[i].Item != item || targets[i].MaxBuy != 1 || targets[i].Stock != 1 {
					t.Fatalf("targets %+v", targets)
				}
			}
		})
	}
}

func TestSurgeryPartTradeFallsBackToCarriedItem(t *testing.T) {
	parts := []SurgeryPart{{Items: []Resource{"BionicLeg", "SimpleProstheticLeg"}, Priority: 50}}
	rows := []TradeSheetRowFact{{DefName: "SimpleProstheticLeg", TraderCount: 1, BuyPriceKnown: true, BuyPrice: 400}}
	targets := surgeryPartTargets(parts, rows, map[string]bool{})
	if len(targets) != 1 || targets[0].Item != "SimpleProstheticLeg" {
		t.Fatalf("targets %+v", targets)
	}
	if got := RoutineTradeTargets(TradeNeed{SurgeryParts: parts}, rows, nil, RoutineTradePolicy{}, domain.Known[int64](3)); got.Validate() != nil {
		t.Fatalf("invalid policy %+v: %v", got, got.Validate())
	}
}
