package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestSurfaceOreOnlySafeMineables(t *testing.T) {
	sources := []ResourceSource{{ThingID: "a", Method: ResourceSourceMine, Safety: "open_surface", Yield: 20, Designated: true}, {ThingID: "b", Method: ResourceSourceMine, Safety: "roofed", Yield: 100}, {ThingID: "c", Method: "haul", Yield: 40}, {ThingID: "d", Method: ResourceSourceMine, Safety: MineSafetySupportedRoof, Yield: 30}}
	if n, k := SurfaceOre(sources).Value(); !k || n != 50 {
		t.Fatal(n, k)
	}
	if _, k := SurfaceOre(append(sources, sources[0])).Value(); k {
		t.Fatal("duplicate ore")
	}
}

func TestMedicineRunwayFromTendRate(t *testing.T) {
	p := DefaultRoundsPolicy()
	p.ResourceTargets = DefaultResourceTargets()
	reserves := p.RunwayReserves(CoreItemFacts(), domain.Known(int64(8)))
	if reserve, set := reserves["MedicineHerbal"]; !set || reserve != 8*p.MedicalReserve.TargetPerColonist || reserves["Steel"] == 0 {
		t.Fatal(reserves)
	}
	if got := p.RunwayReserves(ItemFacts{}, domain.Known(int64(8))); len(got) != len(p.ResourceTargets) {
		t.Fatal("no catalog medicine must add no runway key", got)
	}
	if !p.TracksResource(CoreItemFacts(), "MedicineHerbal") {
		t.Fatal("medicine workshop research must remain tracked")
	}
	busy := domain.Known(ResourceConsumption{WindowDays: 15, Recurring: map[Resource]int64{"MedicineHerbal": 30}})
	short := ForecastResourceRunway("MedicineHerbal", domain.Known(int64(4)), domain.Known(int64(0)), 0, 0, busy)
	if deficit, _ := short.Deficit.Value(); !deficit || short.Target != 10 {
		t.Fatal(short)
	}
	if days, _ := short.DaysLeft.Value(); days != 2 {
		t.Fatal(short)
	}
	quiet := domain.Known(ResourceConsumption{WindowDays: 15, Recurring: map[Resource]int64{}})
	if q := ForecastResourceRunway("MedicineHerbal", domain.Known(int64(0)), domain.Known(int64(0)), 0, 0, quiet); q.Target != 0 {
		t.Fatal(q)
	}
	unknown := ForecastResourceRunway("MedicineHerbal", domain.Known(int64(4)), domain.Known(int64(0)), 0, 0, domain.Unknown[ResourceConsumption]())
	if _, known := unknown.Deficit.Value(); known || unknown.Target != 0 {
		t.Fatal(unknown)
	}
}

func TestRunwayWindowShorterThanADay(t *testing.T) {
	stock, ore := domain.Known(int64(4)), domain.Known(int64(0))
	hours := domain.Known(ResourceConsumption{WindowDays: 0.5, Recurring: map[Resource]int64{"Steel": 5}})
	r := ForecastResourceRunway("Steel", stock, ore, 0, 0, hours)
	if rate, known := r.ConsumptionPerDay.Value(); !known || rate != 5 {
		t.Fatal("a sub-day window is read as one day", r)
	}
	none := domain.Known(ResourceConsumption{})
	z := ForecastResourceRunway("Steel", domain.Known(int64(3)), ore, 10, 0, none)
	if deficit, known := z.Deficit.Value(); !known || !deficit || z.Target != 10 {
		t.Fatal("zero window must demand the reserve", z)
	}
	if rate, known := z.ConsumptionPerDay.Value(); !known || rate != 0 {
		t.Fatal(z)
	}
	u := ForecastResourceRunway("Steel", stock, ore, 10, 0, domain.Unknown[ResourceConsumption]())
	if _, known := u.Deficit.Value(); known || u.Target != 0 {
		t.Fatal("unknown read stays unknown", u)
	}
}

func TestRunwaySurfacesMaintainResourceDeficit(t *testing.T) {
	p := DefaultRoundsPolicy()
	f := RoundsFacts{Resources: domain.Known([]Amount{{Resource: "Steel", Count: 100}}), ResourceRunways: []ResourceRunway{{Resource: "Steel", DaysLeft: domain.Known(2.0), Deficit: domain.Known(true)}}}
	r, err := InspectRounds(f, RoundsLatches{}, p)
	if err != nil {
		t.Fatal(err)
	}
	for _, a := range r.Assessments {
		if a.ID == MaintainResource {
			if a.Finding != domain.FindingUnmet {
				t.Fatal(a)
			}
			return
		}
	}
	t.Fatal("missing MaintainResource")
}

func TestUnknownRunwayCannotRecoverMaintenance(t *testing.T) {
	f := RoundsFacts{Resources: domain.Known([]Amount{}), ResourceRunways: []ResourceRunway{{Resource: "Steel", WindowDays: 2}}}
	r, err := InspectRounds(f, RoundsLatches{}, DefaultRoundsPolicy())
	if err != nil {
		t.Fatal(err)
	}
	for _, a := range r.Assessments {
		if a.ID == MaintainResource {
			if a.Finding != domain.FindingUnclear {
				t.Fatal(a)
			}
			return
		}
	}
	t.Fatal("missing resource assessment")
}

func TestFirstHoursBurstIsNotAHugeDailyRate(t *testing.T) {
	// 60 steel spent in the first hour of a 1/24-day ledger reads as 60 a day.
	burst := domain.Known(ResourceConsumption{WindowDays: 1.0 / 24, Recurring: map[Resource]int64{"Steel": 60}})
	r := ForecastResourceRunway("Steel", domain.Known(int64(300)), domain.Known(int64(0)), 0, 0, burst)
	if rate, _ := r.ConsumptionPerDay.Value(); rate != 60 {
		t.Fatal(r)
	}
	if deficit, known := r.Deficit.Value(); !known || deficit {
		t.Fatal("5 days of 60 a day is held", r)
	}
}

func TestUnknownOreIsNoProspectiveSupply(t *testing.T) {
	busy := domain.Known(ResourceConsumption{WindowDays: 15, Recurring: map[Resource]int64{"Steel": 30}})
	r := ForecastResourceRunway("Steel", domain.Known(int64(4)), domain.Unknown[int64](), 0, 0, busy)
	if days, known := r.DaysLeft.Value(); !known || days != 2 {
		t.Fatal(r)
	}
	if deficit, _ := r.Deficit.Value(); !deficit || r.Target != 10 {
		t.Fatal(r)
	}
}
