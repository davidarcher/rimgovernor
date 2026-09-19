package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func pestRow(id, token string, x int32) AcquisitionSource {
	return AcquisitionSource{ID: id, Resource: "Corpse_Alphabeaver", Token: token, Definition: "Alphabeaver", Cell: domain.Cell{X: x, Z: 4}, Hunt: true, Yield: 1}
}

func TestPestCensusCountsRecognisedDefinitionsOnly(t *testing.T) {
	if _, known := PestCensus(domain.Unknown[[]UpkeepAnimal]()).Value(); known {
		t.Fatal("unknown wild census became a count")
	}
	wild := []UpkeepAnimal{{ID: "b1", Definition: "Alphabeaver"}, {ID: "d1", Definition: "Deer"}, {ID: "b2", Definition: "Alphabeaver"}}
	if n, known := PestCensus(domain.Known(wild)).Value(); !known || n != 2 {
		t.Fatal(n, known)
	}
	if n, known := PestCensus(domain.Known([]UpkeepAnimal{})).Value(); !known || n != 0 {
		t.Fatal(n, known)
	}
}

func TestPestHuntRowsPassFoodAndWoodSelectionsOver(t *testing.T) {
	rows := []AcquisitionSource{pestRow("b1", "t1", 1), {ID: "deer", Resource: "Corpse_Deer", Token: "d", Definition: "Deer", Food: true, Hunt: true, Yield: 1, NutritionYield: 2}, {ID: "tree", Resource: "WoodLog", Token: "w", Definition: "Plant_TreeOak", Tree: true, Yield: 20}}
	food, err := SelectAcquisition(domain.Known(rows), domain.Known(5.0), domain.Known(0.0), true, nil, domain.Known(2))
	if err != nil || len(food) != 1 || food[0].ID != "deer" {
		t.Fatal(food, err)
	}
	wood, err := SelectAcquisition(domain.Known(rows), domain.Known(5.0), domain.Known(0.0), false, nil, domain.Known(2))
	if err != nil || len(wood) != 1 || wood[0].ID != "tree" {
		t.Fatal(wood, err)
	}
	// A hunt row that is neither food nor a recognised pest is still invalid.
	rows[0].Definition = "Muffalo"
	if _, err = SelectAcquisition(domain.Known(rows), domain.Known(5.0), domain.Known(0.0), true, nil); err == nil {
		t.Fatal("inedible non-pest hunt row accepted")
	}
}

func TestSelectPestAcquisitionHonoursBudgetHeldAndCensus(t *testing.T) {
	rows := []AcquisitionSource{pestRow("b1", "t1", 1), pestRow("b2", "t2", 2), pestRow("b3", "t3", 3), {ID: "deer", Resource: "Corpse_Deer", Token: "d", Definition: "Deer", Food: true, Hunt: true, Yield: 1, NutritionYield: 2}}
	selected, err := SelectPestAcquisition(domain.Known(rows), domain.Known(3), nil, domain.Known(2))
	if err != nil || len(selected) != 2 || selected[0].ID != "b1" || selected[1].ID != "b2" {
		t.Fatal(selected, err)
	}
	selected, err = SelectPestAcquisition(domain.Known(rows), domain.Known(3), map[string]bool{"b1": true}, domain.Known(2))
	if err != nil || len(selected) != 2 || selected[0].ID != "b2" || selected[1].ID != "b3" {
		t.Fatal(selected, err)
	}
	rows[1].Designated = true
	selected, err = SelectPestAcquisition(domain.Known(rows), domain.Known(3), nil, domain.Known(1))
	if err != nil || len(selected) != 1 || selected[0].ID != "b1" {
		t.Fatal(selected, err)
	}
	// The census bounds the selection: one pest counted admits one hunt.
	selected, err = SelectPestAcquisition(domain.Known(rows), domain.Known(1), nil, domain.Known(2))
	if err != nil || len(selected) != 1 {
		t.Fatal(selected, err)
	}
	if selected, err = SelectPestAcquisition(domain.Known(rows), domain.Known(3), nil, domain.Unknown[int]()); err != nil || len(selected) != 0 {
		t.Fatal("unknown hunting budget admitted a hunt", selected, err)
	}
	if _, err = SelectPestAcquisition(domain.Known(rows), domain.Unknown[int](), nil, domain.Known(2)); err == nil {
		t.Fatal("unknown pest census selected hunts")
	}
	if selected, err = SelectPestAcquisition(domain.Known(rows), domain.Known(0), nil, domain.Known(2)); err != nil || len(selected) != 0 {
		t.Fatal(selected, err)
	}
}

func TestClearPestsOpensOnlyOnAKnownPest(t *testing.T) {
	f := stableRoutine()
	r := needs(t, f, RoutineLatches{})
	if hasNeed(r, ClearPests) || assessment(t, r, ClearPests) != domain.NeedRecovered {
		t.Fatal(r.Goals)
	}
	f.AnimalUpkeep.WildAnimals = domain.Unknown[[]UpkeepAnimal]()
	r = needs(t, f, RoutineLatches{})
	if hasNeed(r, ClearPests) || assessment(t, r, ClearPests) != domain.NeedUnknown {
		t.Fatal("unknown wild census opened or recovered the goal", r.Goals)
	}
	f.AnimalUpkeep.WildAnimals = domain.Known([]UpkeepAnimal{{ID: "d", Definition: "Deer"}})
	r = needs(t, f, RoutineLatches{})
	if hasNeed(r, ClearPests) || assessment(t, r, ClearPests) != domain.NeedRecovered {
		t.Fatal("a deer opened the goal", r.Goals)
	}
	f.AnimalUpkeep.WildAnimals = domain.Known([]UpkeepAnimal{{ID: "d", Definition: "Deer"}, {ID: "b", Definition: "Alphabeaver"}})
	r = needs(t, f, RoutineLatches{})
	if !hasNeed(r, ClearPests) || assessment(t, r, ClearPests) != domain.NeedDeficit {
		t.Fatal(r.Goals)
	}
	for _, g := range r.Goals {
		if g.ID == ClearPests {
			if deficit, known := g.Deficit.Value(); g.Priority != 2 || !known || deficit != 1 || g.MethodUnavailable || g.Blocked {
				t.Fatal(g)
			}
		}
	}
}
