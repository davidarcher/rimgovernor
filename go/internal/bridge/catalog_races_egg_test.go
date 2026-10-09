package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// TestRecordedChickenEggFacts: the fertilized egg, the fertilization
// count, the hatch time and the mate interval come from the recorded Chicken
// defs; a fertilized egg with no hatcher comp is a contract error.
func TestRecordedChickenEggFacts(t *testing.T) {
	catalog := fullCatalog(t)
	races, err := catalog.AnimalRaces()
	if err != nil {
		t.Fatal(err)
	}
	chicken, ok := races.Race("Chicken")
	if !ok {
		t.Fatal("no Chicken race")
	}
	if v, ok := chicken.MateMtbHours.Value(); !ok || v != 8 {
		t.Fatalf("mate MTB %v %v", v, ok)
	}
	var eggs *policy.RaceProduct
	for i := range chicken.Products {
		if chicken.Products[i].Kind == "eggs" {
			eggs = &chicken.Products[i]
		}
	}
	if eggs == nil {
		t.Fatalf("no egg product %+v", chicken.Products)
	}
	if eggs.FertilizedDef != "EggChickenFertilized" || eggs.FertilizationCountMax != 1 || !eggs.FemaleOnly || eggs.HatchPawn != "Chicken" {
		t.Fatalf("egg facts %+v", *eggs)
	}
	if v, ok := eggs.HatchDays.Value(); !ok || v != 3.5 {
		t.Fatalf("hatch days %v %v", v, ok)
	}
	delete(catalog.ThingDefs, "EggChickenFertilized")
	if _, err := catalog.buildRaces(); err == nil {
		t.Fatal("a fertilized egg def the catalog lacks must fail loudly")
	}
}
