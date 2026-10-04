package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Snapshot tests over recorded Biotech facts: the gestation goal stays
// inside the mechanitor's free bandwidth (Total - Used - Gestation) and holds
// while waste is uncleared (#1686).

func gestCatalog() MechCatalog {
	return MechCatalog{Kinds: map[string]MechKind{
		"Lifter":   {Name: "Lifter", WorkMech: true, WorkTypes: []WorkType{"Hauling"}, BandwidthCost: 1},
		"Fabri":    {Name: "Fabri", WorkMech: true, WorkTypes: []WorkType{"Crafting", "Hauling"}, BandwidthCost: 2},
		"Militor":  {Name: "Militor", BandwidthCost: 1, CombatPower: 40},
		"Tunneler": {Name: "Tunneler", BandwidthCost: 2, CombatPower: 100},
	}}
}

func gestMechanitor(total, used, gestation int) MechanitorInput {
	return MechanitorInput{ID: "M", PawnMechanitor: PawnMechanitor{
		TotalBandwidth: domain.Known(total), UsedBandwidth: domain.Known(used), GestationBandwidth: domain.Known(gestation), ControlGroups: domain.Known(2)}}
}

func gestation(m MechanitorInput, coverage ...WorkCoverage) MechGestation {
	return MechGestation{
		Catalog:     gestCatalog(),
		Mechanitors: []MechanitorInput{m},
		Coverage:    coverage,
		Gestators:   []GestatorFact{{ID: "G", Active: domain.Known(false), WasteCount: domain.Known(int32(0))}},
		Chargers:    []MechCharger{{Powered: domain.Known(true), FullOfWaste: domain.Known(false), Charging: domain.Known(false)}},
	}
}

func gestBench(bills ...ExistingProductionBill) ProductionBench {
	recipe := func(name, kind string) ProductionRecipe {
		return ProductionRecipe{Name: name, MechKind: kind, Available: domain.Known(true)}
	}
	return ProductionBench{ID: "G", Token: domain.Known("bill-1"), Usable: domain.Known(true), Bills: bills,
		Recipes: []ProductionRecipe{recipe("MakeLifter", "Lifter"), recipe("MakeFabri", "Fabri"), recipe("MakeMilitor", "Militor"), recipe("MakeTunneler", "Tunneler"),
			{Name: "Make_Steel", Available: domain.Known(true)}}}
}

var shortHauling = WorkCoverage{Work: "Hauling", Demand: 2, Owners: 1}

func TestGestationStaysInsideBandwidth(t *testing.T) {
	for total := 0; total <= 6; total++ {
		for used := 0; used <= total; used++ {
			for gestating := 0; gestating <= total-used; gestating++ {
				for _, coverage := range [][]WorkCoverage{nil, {shortHauling}} {
					g := gestation(gestMechanitor(total, used, gestating), coverage...)
					free := total - used - gestating
					sel, ok, err := selectGestation([]ProductionBench{gestBench()}, g)
					if err != nil {
						t.Fatal(err)
					}
					owed, _ := MechGestationOwed(g)
					if o, _ := owed.Value(); o != ok {
						t.Fatalf("owed %v but selected %v at free %d", o, ok, free)
					}
					if !ok {
						continue
					}
					kind := map[string]string{"MakeLifter": "Lifter", "MakeFabri": "Fabri", "MakeMilitor": "Militor", "MakeTunneler": "Tunneler"}[sel.Recipe]
					if cost := gestCatalog().Kinds[kind].BandwidthCost; cost > float64(free) {
						t.Fatalf("free %d: queued %s costing %v", free, sel.Recipe, cost)
					}
					if sel.Mode != domain.GearBatch || sel.Target != 1 {
						t.Fatalf("selection %+v is not a single gestation", sel)
					}
				}
			}
		}
	}
}

func TestGestationNeedPicksTheRole(t *testing.T) {
	m := gestMechanitor(8, 2, 0)
	// Hauling is short and no mech covers it: a worker covering it, the
	// one covering the most short work first.
	sel, ok, _ := selectGestation([]ProductionBench{gestBench()}, gestation(m, shortHauling, WorkCoverage{Work: "Crafting", Demand: 1, Owners: 0}))
	if !ok || sel.Recipe != "MakeFabri" {
		t.Fatalf("short hauling and crafting: %+v %v", sel, ok)
	}
	// Covered by a Lifter already under this mechanitor: a guard, the best
	// combat power per bandwidth.
	g := gestation(m, shortHauling)
	g.Mechs = []MechInput{{ID: "L", Kind: "Lifter", PawnMech: PawnMech{Overseer: "M"}}}
	g.Mechs = append(g.Mechs, MechInput{ID: "F", Kind: "Fabri", PawnMech: PawnMech{Overseer: "M"}})
	if sel, ok, _ = selectGestation([]ProductionBench{gestBench()}, g); !ok || sel.Recipe != "MakeTunneler" {
		t.Fatalf("covered work: %+v %v", sel, ok)
	}
	// No bandwidth for the role wanted: nothing is queued, never the other role.
	g = gestation(gestMechanitor(3, 2, 0), WorkCoverage{Work: "Crafting", Demand: 1, Owners: 0})
	if _, ok, _ = selectGestation([]ProductionBench{gestBench()}, g); ok {
		t.Fatal("a guard bought where a worker was needed")
	}
}

func TestGestationHoldsWhileWasteIsUncleared(t *testing.T) {
	pack := func(count int32, frozen, atomizer domain.Fact[bool]) WastepackFact {
		return WastepackFact{Count: count, Frozen: frozen, InAtomizer: atomizer}
	}
	yes, no := domain.Known(true), domain.Known(false)
	cases := []struct {
		name string
		edit func(*MechGestation)
		hold bool
	}{
		{"clear", func(*MechGestation) {}, false},
		{"frozen pack", func(g *MechGestation) { g.Wastepacks = []WastepackFact{pack(5, yes, no)} }, false},
		{"atomized pack", func(g *MechGestation) { g.Wastepacks = []WastepackFact{pack(5, no, yes)} }, false},
		{"dissolved pack", func(g *MechGestation) { g.Wastepacks = []WastepackFact{pack(0, no, no)} }, false},
		{"loose pack", func(g *MechGestation) { g.Wastepacks = []WastepackFact{pack(5, yes, no), pack(1, no, no)} }, true},
		{"unread frozen flag", func(g *MechGestation) { g.Wastepacks = []WastepackFact{pack(5, domain.Fact[bool]{}, no)} }, true},
		{"unread atomizer flag", func(g *MechGestation) { g.Wastepacks = []WastepackFact{pack(5, no, domain.Fact[bool]{})} }, true},
		{"gestator waste", func(g *MechGestation) { g.Gestators[0].WasteCount = domain.Known(int32(2)) }, true},
		{"unread gestator waste", func(g *MechGestation) { g.Gestators[0].WasteCount = domain.Fact[int32]{} }, true},
	}
	for _, c := range cases {
		g := gestation(gestMechanitor(6, 1, 0))
		c.edit(&g)
		_, ok, err := selectGestation([]ProductionBench{gestBench()}, g)
		owed, _ := MechGestationOwed(g)
		if o, _ := owed.Value(); err != nil || ok == c.hold || o == c.hold {
			t.Errorf("%s: selected %v owed %v, want hold=%v", c.name, ok, o, c.hold)
		}
	}
}

func TestGestationIsOneAtATime(t *testing.T) {
	g := gestation(gestMechanitor(6, 1, 0))
	if _, ok, _ := selectGestation([]ProductionBench{gestBench(ExistingProductionBill{ID: "b", Recipe: "MakeLifter", Active: domain.Known(false)})}, g); ok {
		t.Fatal("a second gestation bill beside a standing one")
	}
	// Another recipe's bill is no gestation.
	if _, ok, _ := selectGestation([]ProductionBench{gestBench(ExistingProductionBill{ID: "b", Recipe: "Make_Steel"})}, g); !ok {
		t.Fatal("an ordinary bill blocked the gestator")
	}
	g.Gestators[0].Active = domain.Known(true)
	if owed, _ := MechGestationOwed(g); owed != domain.Known(false) {
		t.Fatal("goal owed while a gestator is forming a mech")
	}
	if owed, _ := MechGestationOwed(MechGestation{Catalog: gestCatalog(), Mechanitors: g.Mechanitors}); owed != domain.Known(false) {
		t.Fatal("goal owed without a gestator")
	}
}

func TestGestationPrefersBulkRecipe(t *testing.T) {
	bench := gestBench()
	bench.Recipes = []ProductionRecipe{
		{Name: "MakeLifter", MechKind: "Lifter", Available: domain.Known(true)},
		{Name: "MakeLifterBulk", Bulk: true, MechKind: "Lifter", Available: domain.Known(true)},
	}
	g := gestation(gestMechanitor(4, 0, 0), shortHauling)
	if sel, ok, _ := selectGestation([]ProductionBench{bench}, g); !ok || sel.Recipe != "MakeLifterBulk" {
		t.Fatalf("bulk rule: %+v %v", sel, ok)
	}
}

func TestGestationUnknownKindFailsLoudly(t *testing.T) {
	bench := gestBench()
	bench.Recipes = append(bench.Recipes, ProductionRecipe{Name: "MakeModMech", MechKind: "Mod_Mech", Available: domain.Known(true)})
	if _, _, err := selectGestation([]ProductionBench{bench}, gestation(gestMechanitor(4, 0, 0))); err == nil {
		t.Fatal("a recipe whose mech kind is not in the catalog was accepted")
	}
}

func TestGestationGoalRaisedOnlyWhenOwed(t *testing.T) {
	owed, _ := MechGestationOwed(gestation(gestMechanitor(4, 0, 0)))
	if o, _ := owed.Value(); !o {
		t.Fatal("idle gestator, free bandwidth and clear waste is owed")
	}
	if got := GoalConcept(MaintainMechs); got != ConceptStandard {
		t.Fatalf("concept %v", got)
	}
}

func TestGestationWaitsForAReadyCharger(t *testing.T) {
	yes, no := domain.Known(true), domain.Known(false)
	charger := func(powered, full domain.Fact[bool]) []MechCharger {
		return []MechCharger{{Powered: powered, FullOfWaste: full, Charging: no}}
	}
	cases := []struct {
		name     string
		chargers []MechCharger
		hold     bool
	}{
		{"ready", charger(yes, no), false},
		{"none", nil, true},
		{"unpowered", charger(no, no), true},
		{"full of waste", charger(yes, yes), true},
		{"unread power", charger(domain.Fact[bool]{}, no), true},
		{"one of two ready", append(charger(no, no), charger(yes, no)...), false},
	}
	for _, c := range cases {
		g := gestation(gestMechanitor(6, 1, 0))
		g.Chargers = c.chargers
		_, ok, err := selectGestation([]ProductionBench{gestBench()}, g)
		owed, _ := MechGestationOwed(g)
		if o, _ := owed.Value(); err != nil || ok == c.hold || o == c.hold {
			t.Errorf("%s: selected %v owed %v, want hold=%v", c.name, ok, o, c.hold)
		}
	}
}
