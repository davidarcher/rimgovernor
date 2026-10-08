package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestDrugRunwayRequiresResearch(t *testing.T) {
	for _, research := range []domain.Fact[ResearchFacts]{domain.Unknown[ResearchFacts](), domain.Known(ResearchFacts{}), domain.Known(ResearchFacts{Current: "Brewing"})} {
		if len(DrugRunwayReserves(research)) != 0 {
			t.Fatal("runway before completed research")
		}
	}
	got := DrugRunwayReserves(domain.Known(ResearchFacts{Finished: []ResearchProjectID{"Brewing"}}))
	if len(got) != 2 || got["Beer"] != 0 || got["SmokeleafJoint"] != 0 {
		t.Fatal(got)
	}
}

func drugRow(resource Resource, stock int64, consumption domain.Fact[ResourceConsumption]) ResourceRunway {
	return ForecastResourceRunway(resource, domain.Known(stock), domain.Known(int64(0)), 0, 0, consumption)
}

func TestDrugRunwayShortfallRaisesDemand(t *testing.T) {
	use := domain.Known(ResourceConsumption{WindowDays: 15, Recurring: map[Resource]int64{"Beer": 30, "SmokeleafJoint": 0}})
	beer := drugRow("Beer", 4, use) // 2 a day, 2 days of stock
	if beer.Target != 10 {
		t.Fatalf("target %d", beer.Target)
	}
	joints := drugRow("SmokeleafJoint", 5, use)
	projection, ok := PlanDrugRunway([]ResourceRunway{beer, joints}).Value()
	if !ok || projection.ShortfallDays != 3 || len(projection.Resources) != 2 {
		t.Fatal(projection, ok)
	}
	if ResourceRunwayTargets([]ResourceRunway{beer, joints})["Beer"] != 10 || len(ResourceRunwayTargets([]ResourceRunway{joints})) != 0 {
		t.Fatal("demand")
	}
}

func TestDrugRunwayUnknownInputs(t *testing.T) {
	if _, ok := PlanDrugRunway(nil).Value(); ok {
		t.Fatal("no drug row is unknown")
	}
	unread := drugRow("Beer", 4, domain.Unknown[ResourceConsumption]())
	if _, ok := PlanDrugRunway([]ResourceRunway{unread}).Value(); ok {
		t.Fatal("unread consumption is unknown")
	}
	if len(ResourceRunwayTargets([]ResourceRunway{unread})) != 0 {
		t.Fatal("unknown rate raised demand")
	}
}
