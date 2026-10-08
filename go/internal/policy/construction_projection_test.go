package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func constructionInputs(deficit map[Resource]int64, stock ...Amount) ConstructionInputs {
	return ConstructionInputs{Deficit: domain.Known(deficit), Stock: domain.Known(stock), Items: CoreItemFacts()}
}

func TestProjectConstructionShortfall(t *testing.T) {
	in := constructionInputs(map[Resource]int64{"WoodLog": 100, "BlocksGranite": 20, "Steel": 10, "Silver": 50},
		Amount{"WoodLog", 40}, Amount{"BlocksSlate", 20}, Amount{"Steel", 0})
	got, ok := ProjectForward(ForwardInputs{Construction: in}).Construction.Value()
	if !ok || len(got.Classes) != 3 {
		t.Fatal(got, ok)
	}
	byClass := map[ConstructionClass]ConstructionClassProjection{}
	for _, c := range got.Classes {
		byClass[c.Class] = c
	}
	if w := byClass[ConstructionWood]; w.ShortfallUnits != 60 || math.Abs(w.ShortfallDays-3) > 1e-9 {
		t.Fatal(w)
	}
	if s := byClass[ConstructionStone]; s.ShortfallUnits != 0 || s.ShortfallDays != 0 { // any stone block def covers stone
		t.Fatal(s)
	}
	if s := byClass[ConstructionSteel]; s.ShortfallDays != ProjectionHorizonDays {
		t.Fatal(s)
	}
	if got.ShortfallDays != ProjectionHorizonDays {
		t.Fatal(got.ShortfallDays)
	}
}

func TestProjectConstructionAdmittedMethodCosts(t *testing.T) {
	in := constructionInputs(map[Resource]int64{}, Amount{"WoodLog", 10})
	in.Admitted = []AdmittedCost{{"WoodLog", "a", 15}, {"WoodLog", "b", 15}}
	got, ok := projectConstruction(in).Value()
	if !ok || len(got.Classes) != 1 || got.Classes[0].Need != 30 || got.Classes[0].ShortfallUnits != 20 {
		t.Fatal(got, ok)
	}
	// The standing deficit and the previewed costs overlap: the larger counts once.
	in.Deficit = domain.Known(map[Resource]int64{"WoodLog": 40})
	if got, _ = projectConstruction(in).Value(); got.Classes[0].Need != 40 {
		t.Fatal(got)
	}
}

func TestProjectConstructionNothingOwed(t *testing.T) {
	got, ok := projectConstruction(constructionInputs(map[Resource]int64{})).Value()
	if !ok || got.ShortfallDays != 0 || len(got.Classes) != 0 {
		t.Fatal(got, ok)
	}
}

func TestProjectConstructionUnknownInputs(t *testing.T) {
	in := constructionInputs(map[Resource]int64{"WoodLog": 5}, Amount{"WoodLog", 0})
	in.Stock = domain.Unknown[[]Amount]()
	if _, ok := projectConstruction(in).Value(); ok {
		t.Fatal("unknown stock must project unknown")
	}
	in = constructionInputs(nil)
	in.Deficit = domain.Unknown[map[Resource]int64]()
	if _, ok := projectConstruction(in).Value(); ok {
		t.Fatal("unknown deficit must project unknown")
	}
}

// Food has one runway-to-shortfall formula: the projector and NutritionDemand agree.
func TestFoodShortfallSharesOneFormula(t *testing.T) {
	if RunwayShortfall(2, 5) != 3 || RunwayShortfall(9, 5) != 0 {
		t.Fatal("RunwayShortfall")
	}
	fp, ok := projectFood(foodFixture()).Value()
	if !ok || fp.ShortfallDays != RunwayShortfall(fp.RunwayDays, ProjectionHorizonDays) {
		t.Fatal(fp, ok)
	}
	f, err := ForecastFood(foodFixture(), nil)
	if err != nil {
		t.Fatal(err)
	}
	d, err := NutritionDemand(NutritionDemandInput{Forecast: f, MinDays: 1, TargetDays: ProjectionHorizonDays})
	if err != nil {
		t.Fatal(err)
	}
	if want := 1 + fp.ShortfallDays/ProjectionHorizonDays; math.Abs(d.Cover-want) > 1e-9 {
		t.Fatal(d.Cover, want)
	}
}
