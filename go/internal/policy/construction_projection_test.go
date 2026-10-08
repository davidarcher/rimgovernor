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
	in.Dependencies = []DevelopmentDependency{{Dependent: MaintainShelter, Prerequisite: MaintainResource, Resource: "WoodLog",
		Costs: []DependencyCost{{Action: "a", Count: 15}, {Action: "b", Count: 15}}, Available: domain.Known(int64(10))}}
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

// Every construction-bearing concern is scored from the construction
// projection; an unknown projection leaves it unranked, not defaulted.
func TestShadowRankConstructionConcerns(t *testing.T) {
	short := ConstructionProjection{ShortfallDays: 3}
	for _, c := range []ConcernID{MaintainStoneShell, MaintainHousing, MaintainShelter, MaintainHomeCoverage, MaintainFlooring, MaintainLighting, MaintainFirebreak, EnsureBasicPower, EnsureMechCharger} {
		state := DevelopmentState{Rows: []DevelopmentRow{shadowRow(c, "")}}
		p := shadowProjection()
		p.Construction = domain.Known(short)
		got := ShadowRankOf(state, p, map[ConcernID]int{c: 1})
		if len(got.Ranked) != 1 || got.Ranked[0].Domain != ShadowConstruction || got.Ranked[0].ShortfallDays != 3 {
			t.Fatal(c, got)
		}
		p.Construction = domain.Unknown[ConstructionProjection]()
		if got = ShadowRankOf(state, p, map[ConcernID]int{c: 1}); len(got.Ranked) != 0 || len(got.Unranked) != 1 {
			t.Fatal(c, got)
		}
	}
}

// A wood-short shelter and its MaintainResource prerequisite are both scored.
func TestShadowRankWoodShortShelterAndPrerequisite(t *testing.T) {
	in := constructionInputs(map[Resource]int64{"WoodLog": 50}, Amount{"WoodLog", 25})
	p := shadowProjection()
	p.Food = domain.Unknown[FoodProjection]()
	p.Construction = projectConstruction(in)
	state := DevelopmentState{Rows: []DevelopmentRow{shadowRow(MaintainShelter, DevelopmentCapacity), shadowRow(MaintainResource, DevelopmentCapacity)}}
	got := ShadowRankOf(state, p, map[ConcernID]int{MaintainShelter: 5, MaintainResource: 1})
	if len(got.Ranked) != 2 || got.Ranked[0].Concern != MaintainResource || got.Ranked[0].Domain != ShadowConstruction || got.Ranked[0].ShortfallDays != 2.5 || got.Ranked[1].Concern != MaintainShelter {
		t.Fatal(got)
	}
}

// MaintainResource spans food and construction: the larger known shortfall
// scores, and one unknown domain does not hide the other.
func TestShadowRankMaintainResourceTwoDomains(t *testing.T) {
	state := DevelopmentState{Rows: []DevelopmentRow{shadowRow(MaintainResource, "")}}
	p := shadowProjection() // food shortfall 4
	p.Construction = domain.Known(ConstructionProjection{ShortfallDays: 5})
	if got := ShadowRankOf(state, p, map[ConcernID]int{MaintainResource: 1}); got.Ranked[0].Domain != ShadowConstruction || got.Ranked[0].ShortfallDays != 5 {
		t.Fatal(got)
	}
	p.Construction = domain.Known(ConstructionProjection{ShortfallDays: 1})
	if got := ShadowRankOf(state, p, map[ConcernID]int{MaintainResource: 1}); got.Ranked[0].Domain != ShadowFood {
		t.Fatal(got)
	}
	p.Construction = domain.Unknown[ConstructionProjection]()
	if got := ShadowRankOf(state, p, map[ConcernID]int{MaintainResource: 1}); got.Ranked[0].Domain != ShadowFood {
		t.Fatal(got)
	}
	p.Food = domain.Unknown[FoodProjection]()
	if got := ShadowRankOf(state, p, map[ConcernID]int{MaintainResource: 1}); len(got.Ranked) != 0 || len(got.Unranked) != 1 {
		t.Fatal(got)
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
