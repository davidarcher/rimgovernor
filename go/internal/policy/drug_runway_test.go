package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestDrugRunwayRequiresResearch(t *testing.T) {
	for _, research := range []domain.Fact[ResearchFacts]{domain.Unknown[ResearchFacts](), domain.Known(ResearchFacts{}), domain.Known(ResearchFacts{Current: "Brewing"})} {
		if len(DrugRunwayReserves(research, map[Resource]int64{"Beer": 3})) != 0 {
			t.Fatal("runway before completed research")
		}
	}
	brewed := domain.Known(ResearchFacts{Finished: []ResearchProjectID{"Brewing"}})
	got := DrugRunwayReserves(brewed, map[Resource]int64{"Beer": 3, "SmokeleafJoint": 2})
	if len(got) != 2 || got["Beer"] != 3 || got["SmokeleafJoint"] != 2 {
		t.Fatal(got)
	}
	if got := DrugRunwayReserves(brewed, nil); len(got) != 2 || got["Beer"] != 0 {
		t.Fatal("no permitted colonist reserves nothing", got)
	}
}

func TestDrugUsersCountPermittedColonists(t *testing.T) {
	beer := domain.DrugPolicyEntry{Drug: "Beer", Joy: true, DaysFrequency: 1, OnlyIfMoodBelow: 1, OnlyIfJoyBelow: 1}
	got := DrugUsers([]DrugPolicyEntry{
		{ID: "a", Pawns: []PawnID{"p1", "p2"}, Entries: []domain.DrugPolicyEntry{beer, {Drug: "Yayo", Joy: true}}},
		{ID: "b", Pawns: []PawnID{"p3"}, Entries: []domain.DrugPolicyEntry{{Drug: "Beer"}}},
	})
	if len(got) != 1 || got["Beer"] != 2 {
		t.Fatal(got)
	}
}

func TestDrugReserveDemandsUnusedStock(t *testing.T) {
	quiet := domain.Known(ResourceConsumption{WindowDays: 15, Recurring: map[Resource]int64{}})
	row := ForecastResourceRunway("Beer", domain.Known(int64(1)), domain.Known(int64(0)), 3, 0, quiet)
	if row.Target != 3 {
		t.Fatal(row)
	}
}

func TestMedicineRunwayProjection(t *testing.T) {
	busy := domain.Known(ResourceConsumption{WindowDays: 15, Recurring: map[Resource]int64{"MedicineHerbal": 30}})
	row := ForecastResourceRunway("MedicineHerbal", domain.Known(int64(4)), domain.Known(int64(0)), 0, 0, busy)
	got, ok := PlanMedicineRunway(MedicineInputs{Runways: []ResourceRunway{row}, Items: CoreItemFacts()}).Value()
	if !ok || got.StockDays != 2 || got.ShortfallDays != RunwayShortfall(2, ProjectionHorizonDays) {
		t.Fatal(got, ok)
	}
	if shortfall, why := shadowShortfall(ForwardProjection{Medicine: domain.Known(got)}, ShadowMedicine); why != "" || shortfall <= 0 {
		t.Fatal(shortfall, why)
	}
	if _, why := shadowShortfall(ProjectForward(ForwardInputs{}), ShadowMedicine); why == "" {
		t.Fatal("no inputs must be unknown")
	}
	unread := ForecastResourceRunway("MedicineHerbal", domain.Known(int64(4)), domain.Known(int64(0)), 0, 0, domain.Unknown[ResourceConsumption]())
	if _, ok := PlanMedicineRunway(MedicineInputs{Runways: []ResourceRunway{unread}, Items: CoreItemFacts()}).Value(); ok {
		t.Fatal("unread consumption is unknown")
	}
	if _, ok := PlanMedicineRunway(MedicineInputs{Runways: []ResourceRunway{row}}).Value(); ok {
		t.Fatal("no catalog medicine is unknown")
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
