package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// iceUnitFixture is the soil-free start: a roofed indoor room with no
// fertility, a freezing outdoors, no sun lamp, and a wind network that carries
// the whole unit.
func iceUnitFixture() SiteTypeRequest {
	r := siteFixture(0)
	for i := range r.Field.Site.Cells {
		c := &r.Field.Site.Cells[i]
		c.Fertility = domain.Known(0.0)
	}
	r.Field.Climate = CropClimate{Sowing: domain.Known(false), DaysRemaining: domain.Unknown[float64](), OutdoorsDark: domain.Known(false)}
	r.Field.Colonists = domain.Known(int64(1))
	r.Field.Site.Anchor = domain.Cell{X: 6, Z: 6}
	env := siteEnv(-30)
	env.Networks = []PowerHeadroom{{ID: "net", GenerationW: domain.Known(5000.0), SolarW: domain.Known(0.0), WindW: domain.Known(5000.0), ConsumptionW: domain.Known(0.0), StoredWattDays: domain.Known(100.0), CapacityWattDays: domain.Known(600.0), ActiveSource: domain.Known(true)}}
	r.Environment = domain.Known(env)
	r.Stock = domain.Known(map[Resource]int64{"Steel": 1000, "ComponentIndustrial": 30})
	return r
}

func unitDefinitions(c SiteTypeCandidate) map[string]int {
	out := map[string]int{}
	for _, b := range c.Buildings {
		out[b.Definition]++
	}
	return out
}

func TestPlanSiteTypeSoilFreeHydroponicsUnit(t *testing.T) {
	r := iceUnitFixture()
	plan, ok := PlanSiteType(r)
	if !ok || plan.Kind != SiteHydroponics || plan.Crop.Name != "Plant_Rice" {
		t.Fatal(plan.Explain())
	}
	got := unitDefinitions(siteCandidateOf(plan, SiteHydroponics, "Plant_Rice"))
	// 57 soil cells at fertility 2.8 are 21 cells: six 1x4 basins.
	if got["SunLamp"] != 1 || got["Heater"] != 1 || got["HydroponicsBasin"] != 6 {
		t.Fatal(got, plan.Explain())
	}
	// The lamp comes first and lights every basin cell.
	center := plan.Buildings[0].Cell
	if plan.Buildings[0].Definition != "SunLamp" {
		t.Fatal(plan.Buildings)
	}
	for _, b := range plan.Buildings {
		if b.Definition != "HydroponicsBasin" {
			continue
		}
		for _, f := range siteBasinFootprint(b.Cell) {
			if !siteWithin(f, center, r.LampGrowthRadius) || f == center {
				t.Fatal("basin cell outside the lamp's disc", b, center)
			}
		}
	}
}

func TestPlanSiteTypeSoilFreeUnitWarmRoomNeedsNoHeater(t *testing.T) {
	r := iceUnitFixture()
	env, _ := r.Environment.Value()
	env.OutdoorTemperatureC = domain.Known(21.0)
	env.Rooms[0].TemperatureC = domain.Known(21.0)
	r.Environment = domain.Known(env)
	c := siteCandidateOf(mustSitePlan(t, r), SiteHydroponics, "Plant_Rice")
	if got := unitDefinitions(c); got["Heater"] != 0 || got["SunLamp"] != 1 || got["HydroponicsBasin"] == 0 {
		t.Fatal(got, c.Reason)
	}
}

func TestPlanSiteTypeSoilFreeUnitHoldsWithReason(t *testing.T) {
	// Steel pays for the lamp and heater (90) plus one basin (100) only.
	r := iceUnitFixture()
	r.Stock = domain.Known(map[Resource]int64{"Steel": 200, "ComponentIndustrial": 30})
	c := siteCandidateOf(mustSitePlan(t, r), SiteHydroponics, "Plant_Rice")
	if got := unitDefinitions(c); got["HydroponicsBasin"] != 1 || got["SunLamp"] != 1 || got["Heater"] != 1 {
		t.Fatal(got, c.Reason)
	}
	// Components are the scarce term: a lamp, heater and basin need two.
	r.Stock = domain.Known(map[Resource]int64{"Steel": 1000, "ComponentIndustrial": 1})
	plan, ok := PlanSiteType(r)
	c = siteCandidateOf(plan, SiteHydroponics, "Plant_Rice")
	if ok || c.Cells != 0 || len(c.Buildings) != 0 || !strings.Contains(c.Reason, "not enough ComponentIndustrial") {
		t.Fatal(ok, c.Reason)
	}
	r.Stock = domain.Known(map[Resource]int64{"Steel": 50, "ComponentIndustrial": 30})
	c = siteCandidateOf(mustSitePlan(t, r), SiteHydroponics, "Plant_Rice")
	if c.Cells != 0 || !strings.Contains(c.Reason, "not enough Steel") {
		t.Fatal(c.Reason)
	}
}

func TestPlanSiteTypeSoilFreeUnitHoldsWithoutPower(t *testing.T) {
	r := iceUnitFixture()
	env, _ := r.Environment.Value()
	// Daytime surplus short of the 2900 W lamp.
	env.Networks[0].GenerationW = domain.Known(2000.0)
	r.Environment = domain.Known(env)
	c := siteCandidateOf(mustSitePlan(t, r), SiteHydroponics, "Plant_Rice")
	if c.Cells != 0 || c.Reason != "no power headroom for a hydroponics unit" {
		t.Fatal(c.Cells, c.Reason)
	}
}

func TestPlanSiteTypeUnlitLampBlocksSecondUnit(t *testing.T) {
	r := iceUnitFixture()
	env, _ := r.Environment.Value()
	env.Lights = []GrowLight{siteLamp(domain.Cell{X: 6, Z: 6}, false)}
	r.Environment = domain.Known(env)
	c := siteCandidateOf(mustSitePlan(t, r), SiteHydroponics, "Plant_Rice")
	if c.Cells != 0 || c.Reason != "no running sun lamp" {
		t.Fatal(c.Cells, c.Reason)
	}
}
