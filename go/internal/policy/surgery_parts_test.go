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
	return SurgeryParts(SelectSurgery(domain.Known([]CarePawn{surgeryPawn("a", 0, append(leg, eye)...)}), nil, SurgeryContext{}).Wants)
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
	noDoctor := SurgeryParts([]SurgeryWant{{Reason: SurgeryNoDoctor, Options: []string{"InstallPegLeg"}, Items: []Resource{"PegLeg"}}})
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
			got, gap := SelectSurgeryPartBill(c.benches, parts)
			ok := gap == ""
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
			targets := roundsTradeTargets(CoreItemFacts(), n, rows, nil).Targets
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
	if got := RoundsTradeTargets(CoreItemFacts(), TradeNeed{SurgeryParts: parts}, rows, nil, domain.Known[int64](3)); got.Validate() != nil {
		t.Fatalf("invalid policy %+v: %v", got, got.Validate())
	}
}

// A part a bench can fabricate opens no caravan; one no bench can make does.
func TestSurgeryPartCaravanSkipsFabricable(t *testing.T) {
	f := stableRounds()
	f.Resources = domain.Known([]Amount{})
	f.Traders = domain.Known([]TraderFacts{{ID: "trader", CanTrade: true}})
	f.MedicalPawns = domain.Known([]CarePawn{surgeryPawn("a", 0, restoreOp("InstallBionicEye", "Eye", 5, 0.9, 1, false))})
	f.FabricableParts = map[Resource]bool{"BionicEye": true}
	if got := needs(t, f, RoundsLatches{}); assessedDeficit(got, TradeWithCaravan) {
		t.Fatal("a fabricable part opened a caravan", got)
	}
	f.FabricableParts = nil
	if got := needs(t, f, RoundsLatches{}); !assessedDeficit(got, TradeWithCaravan) {
		t.Fatal("a part no bench can make opened no caravan", got)
	}
}

func missingElective(recipe, part string, index int, stocked bool) SurgeryOperation {
	op := electiveOp(recipe, part, index, 0.97)
	op.IngredientsOnMap = domain.Known(stocked)
	return op
}

// The single chosen affordable elective creates part demand until it
// is stocked or installed; served demand is unchanged.
func TestChosenElectivePartDemand(t *testing.T) {
	eye := func(id PawnID, stocked bool) CarePawn {
		return wholePawn(id, 0, missingElective("InstallBionicEye", "Eye", 5, stocked))
	}
	fab := map[Resource]bool{"BionicEye": true, "BionicArm": true}
	ctx := func(remaining map[PawnID]float64) SurgeryContext {
		return SurgeryContext{HospitalBed: true, Elective: electiveShares(remaining)}
	}
	rich := map[PawnID]float64{"a": 2000, "b": 2000}
	demand := func(pawns []CarePawn, c SurgeryContext, fabricable map[Resource]bool) []SurgeryPart {
		want, chosen := ChosenElective(domain.Known(pawns), c)
		return ElectiveParts(want, chosen, fabricable)
	}
	t.Run("exactly one demand across colonists and parts", func(t *testing.T) {
		pawns := []CarePawn{
			wholePawn("a", 0, missingElective("InstallBionicEye", "Eye", 5, false), missingElective("InstallBionicArm", "Arm", 20, false)),
			wholePawn("b", 0, missingElective("InstallBionicArm", "Arm", 20, false)),
		}
		parts := demand(pawns, ctx(rich), fab)
		if len(parts) != 1 || parts[0].Pawn != "a" || parts[0].Part != 20 || parts[0].Items[0] != "BionicArm" {
			t.Fatalf("parts %+v", parts)
		}
		if d := SurgeryPartDemand(parts); len(d) != 1 || d[0].Key.Def != "BionicArm" || d[0].Count != 1 {
			t.Fatalf("demand %+v", d)
		}
	})
	t.Run("same gate as selection: unaffordable yields none", func(t *testing.T) {
		if parts := demand([]CarePawn{eye("a", false)}, ctx(map[PawnID]float64{"a": 100}), fab); len(parts) != 0 {
			t.Fatalf("poor: %+v", parts)
		}
		// within the slack: 1000/1.1 = 909 fits 910, not 900
		if parts := demand([]CarePawn{eye("a", false)}, ctx(map[PawnID]float64{"a": 910}), fab); len(parts) != 1 {
			t.Fatalf("slack: %+v", parts)
		}
		if parts := demand([]CarePawn{eye("a", false)}, ctx(map[PawnID]float64{"a": 900}), fab); len(parts) != 0 {
			t.Fatalf("below slack: %+v", parts)
		}
	})
	t.Run("a part nothing fabricates yields none", func(t *testing.T) {
		if parts := demand([]CarePawn{eye("a", false)}, ctx(rich), nil); len(parts) != 0 {
			t.Fatalf("parts %+v", parts)
		}
		if want, chosen := ChosenElective(domain.Known([]CarePawn{eye("a", false)}), ctx(rich)); !chosen || want.Items[0] != "BionicEye" {
			t.Fatalf("the purchase path still sees the choice: %+v %v", want, chosen)
		}
	})
	t.Run("clears when stocked, queued or blocked", func(t *testing.T) {
		if parts := demand([]CarePawn{eye("a", true)}, ctx(rich), fab); len(parts) != 0 {
			t.Fatalf("stocked: %+v", parts)
		}
		if parts := demand([]CarePawn{eye("a", false), wholePawn("b", 1)}, ctx(rich), fab); len(parts) != 0 {
			t.Fatalf("queued: %+v", parts)
		}
		if parts := demand([]CarePawn{eye("a", false)}, SurgeryContext{Elective: electiveShares(rich)}, fab); len(parts) != 0 {
			t.Fatalf("no hospital bed: %+v", parts)
		}
	})
	t.Run("served demand is unchanged and electives wait behind it", func(t *testing.T) {
		served := surgeryPawn("b", 0, restoreOp("InstallProstheticLeg", "Leg", 3, 0.9, 1, false))
		pawns := []CarePawn{eye("a", false), served}
		if parts := demand(pawns, ctx(rich), fab); len(parts) != 0 {
			t.Fatalf("elective demanded under a served one: %+v", parts)
		}
		if got := SurgeryParts(SelectSurgery(domain.Known(pawns), nil, ctx(rich)).Wants); len(got) != 1 || got[0].Part != 3 {
			t.Fatalf("served demand changed: %+v", got)
		}
	})
	t.Run("owed holds the goal while fabricable, not otherwise", func(t *testing.T) {
		pawns := domain.Known([]CarePawn{eye("a", false)})
		if owed, _ := ElectiveSurgeryOwed(pawns, ctx(rich), fab).Value(); !owed {
			t.Fatal("fabricable part must hold MaintainSurgery open")
		}
		if owed, _ := ElectiveSurgeryOwed(pawns, ctx(rich), nil).Value(); owed {
			t.Fatal("an unfabricable part must not hold it open here")
		}
		if owed, _ := ElectiveSurgeryOwed(pawns, ctx(map[PawnID]float64{"a": 100}), fab).Value(); owed {
			t.Fatal("unaffordable must recover")
		}
	})
}
