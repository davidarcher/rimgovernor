package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

func fuelComp(v *d.CompProperties_Refuelable) *d.Opt_CompPropertiesAny {
	return &d.Opt_CompPropertiesAny{Value: &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Refuelable{CompProperties_Refuelable: v}}}
}

func TestRefuelBurnReadsTheDefsComp(t *testing.T) {
	catalog := &DefinitionCatalog{ThingDefs: map[string]*d.ThingDef{
		"Generator": {DefName: "Generator", Comps: []*d.Opt_CompPropertiesAny{
			{Value: &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Flickable{CompProperties_Flickable: &d.CompProperties_Flickable{}}}},
			fuelComp(&d.CompProperties_Refuelable{FuelConsumptionRate: 22, FuelMultiplier: 1}),
		}},
		"Turret": {DefName: "Turret", Comps: []*d.Opt_CompPropertiesAny{
			fuelComp(&d.CompProperties_Refuelable{FuelConsumptionRate: 1, FuelMultiplier: .75, ConsumeFuelOnlyWhenUsed: true, FactorByDifficulty: true}),
		}},
		"Wall": {DefName: "Wall"},
	}}
	got, err := catalog.RefuelBurn("Generator")
	if burn, ok := got.Value(); err != nil || !ok || burn != (policy.FuelBurn{PerDay: 22, UnitsPerItem: 1, Flickable: true}) {
		t.Fatalf("generator %+v %v", burn, err)
	}
	got, err = catalog.RefuelBurn("Turret")
	if burn, ok := got.Value(); err != nil || !ok || burn != (policy.FuelBurn{PerDay: 1, UnitsPerItem: .75, UseDriven: true, Difficulty: true}) {
		t.Fatalf("turret %+v %v", burn, err)
	}
	if got, err = catalog.RefuelBurn("Wall"); err != nil {
		t.Fatal(err)
	} else if _, ok := got.Value(); ok {
		t.Fatal("a def without the comp is no consumer")
	}
	if _, err = catalog.RefuelBurn("Missing"); err == nil {
		t.Fatal("a def missing from the catalog is an error")
	}
}
