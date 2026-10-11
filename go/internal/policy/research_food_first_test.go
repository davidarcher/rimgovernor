package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func soilCell(x int32, fertility domain.Fact[float64]) SiteCell {
	return SiteCell{Cell: domain.Cell{X: x}, Fertility: fertility}
}

func TestNoSoil(t *testing.T) {
	ice := []SiteCell{soilCell(0, domain.Known(0.0)), soilCell(1, domain.Known(0.0))}
	if v, known := NoSoil(ice).Value(); !known || !v {
		t.Fatalf("ice: %v %v", v, known)
	}
	if v, known := NoSoil(append(ice, soilCell(2, domain.Known(1.0)))).Value(); !known || v {
		t.Fatalf("soil present: %v %v", v, known)
	}
	if _, known := NoSoil(append(ice, soilCell(2, domain.Unknown[float64]()))).Value(); known {
		t.Fatal("unknown fertility with no soil seen must stay unknown")
	}
	if _, known := NoSoil(nil).Value(); known {
		t.Fatal("empty window must stay unknown")
	}
}

func TestFoodFirstOwed(t *testing.T) {
	f := func(noSoil domain.Fact[bool], days domain.Fact[float64]) RoundsFacts {
		return RoundsFacts{NoSoil: noSoil, FoodDays: days}
	}
	cases := []struct {
		name string
		f    RoundsFacts
		want bool
	}{
		{"no soil, short runway", f(domain.Known(true), domain.Known(32.0)), true},
		{"no soil, long runway", f(domain.Known(true), domain.Known(FoodFirstRunwayDays)), false},
		{"soil, short runway", f(domain.Known(false), domain.Known(5.0)), false},
		{"soil unknown", f(domain.Unknown[bool](), domain.Known(5.0)), false},
		{"runway unknown", f(domain.Known(true), domain.Unknown[float64]()), false},
	}
	for _, c := range cases {
		if got := FoodFirstOwed(c.f); got != c.want {
			t.Errorf("%s: got %v", c.name, got)
		}
	}
}

func TestFoodFirstLadderHead(t *testing.T) {
	ladder := DefaultResearchLadder()
	if got := FoodFirstLadder(ladder, false); !reflect.DeepEqual(got, ladder) {
		t.Fatalf("soil map must not change the ladder: %v", got)
	}
	got := FoodFirstLadder(ladder, true)
	if !reflect.DeepEqual(got[:2], FoodFirstResearchRungs) || len(got) != len(ladder)+1 {
		t.Fatalf("food chain not at head: %v", got)
	}
	if got := FoodFirstLadder(nil, true); len(got) != 0 {
		t.Fatalf("disabled roadmap stays empty: %v", got)
	}
	// Armor rungs follow Electricity in the ladder; food-first still leads.
	p := RoundsPolicy{ResearchLadder: ladder}
	armored := ResearchPolicy(p, true, true).ResearchLadder
	if !reflect.DeepEqual(armored[:2], FoodFirstResearchRungs) || armored[2] != "Stonecutting" {
		t.Fatalf("armor and food-first: %v", armored)
	}
	facts := domain.Known(ResearchFacts{Projects: []ResearchProjectID{"Stonecutting", "Electricity", "Hydroponics", "Smithing"}})
	if target, _ := ResearchConcern(ResearchPolicy(p, false, true), nil, facts); target != "Electricity" {
		t.Fatalf("first rung %q", target)
	}
	facts = domain.Known(ResearchFacts{Projects: []ResearchProjectID{"Stonecutting", "Electricity", "Hydroponics"}, Finished: []ResearchProjectID{"Electricity"}})
	if target, _ := ResearchConcern(ResearchPolicy(p, false, true), nil, facts); target != "Hydroponics" {
		t.Fatalf("after Electricity %q", target)
	}
}
