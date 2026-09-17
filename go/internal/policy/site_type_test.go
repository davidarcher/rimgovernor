package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// siteFixture is a 40x40 map with a roofed, indoor 12x12 room in the corner
// (x,z < 12) on normal soil and open soil elsewhere. Outdoor soil fertility
// is the caller's; the room is always fertility 1.
func siteFixture(outdoor float64) SiteTypeRequest {
	f := fieldRequest(outdoor)
	for i := range f.Site.Cells {
		c := &f.Site.Cells[i]
		indoor := c.Cell.X < 12 && c.Cell.Z < 12
		c.Roofed, c.Indoors = domain.Known(indoor), domain.Known(indoor)
		if indoor {
			c.Fertility = domain.Known(1.0)
		}
	}
	for i := range f.Choices {
		f.Choices[i].SowTags = domain.Known([]string{"Ground", "Hydroponic"})
		f.Choices[i].MinGlow = domain.Known(0.3)
	}
	lamp := Infrastructure{Name: "SunLamp", Available: domain.Known(true), PowerW: domain.Known(2900.0)}
	basin := Infrastructure{Name: "HydroponicsBasin", Available: domain.Known(true), PowerW: domain.Known(70.0), Fertility: domain.Known(2.8)}
	heater := Infrastructure{Name: "Heater", Available: domain.Known(true), PowerW: domain.Known(175.0)}
	return SiteTypeRequest{Field: f, Lamp: domain.Known(lamp), Basin: domain.Known(basin), Heater: domain.Known(heater), LampGrowthRadius: 5.8, BasinCrop: "Plant_Rice"}
}

func siteNetwork(generation, solar, consumption float64) PowerHeadroom {
	return PowerHeadroom{ID: "net", GenerationW: domain.Known(generation), SolarW: domain.Known(solar), WindW: domain.Known(0.0), ConsumptionW: domain.Known(consumption), StoredWattDays: domain.Known(100.0), CapacityWattDays: domain.Known(600.0), ActiveSource: domain.Known(true)}
}

func siteLamp(center domain.Cell, lit bool) GrowLight {
	l := GrowLight{ID: "lamp-1", Definition: "SunLamp", Cell: center, Room: domain.Known("room"), Network: domain.Known("net"), Powered: domain.Known(lit), PowerW: domain.Known(2900.0), LitNow: domain.Known(lit)}
	for x := int32(0); x < 12; x++ {
		for z := int32(0); z < 12; z++ {
			if c := (domain.Cell{X: x, Z: z}); c != center && siteWithin(c, center, 5.8) {
				l.GrowthCells = append(l.GrowthCells, c)
			}
		}
	}
	return l
}

func siteEnv(temperature float64, lamps ...GrowLight) ControlledEnvironment {
	return ControlledEnvironment{Lights: lamps, Rooms: []GrowRoom{{ID: "room", TemperatureC: domain.Known(temperature), Cells: domain.Known(144), Proper: domain.Known(true)}}, Networks: []PowerHeadroom{siteNetwork(3000, 1700, 600)}, OutdoorTemperatureC: domain.Known(temperature), Daylight: domain.Known(true)}
}

func siteCandidateOf(p SiteTypePlan, kind SiteKind, crop string) SiteTypeCandidate {
	for _, c := range p.Candidates {
		if c.Kind == kind && c.Crop.Name == crop {
			return c
		}
	}
	return SiteTypeCandidate{Reason: "missing"}
}

func TestPlanSiteTypeInSeasonOutdoorBeatsControlledSites(t *testing.T) {
	r := siteFixture(1.0)
	r.Environment = domain.Known(siteEnv(21, siteLamp(domain.Cell{X: 6, Z: 6}, true)))
	plan, ok := PlanSiteType(r)
	if !ok || plan.Kind != SiteOutdoor || plan.Crop.Name != "Plant_Corn" {
		t.Fatal(plan.Explain())
	}
	reuse := siteCandidateOf(plan, SiteGreenhouseReuse, "Plant_Corn")
	if reuse.Cells == 0 || reuse.Cells > 110 || reuse.Score >= plan.Candidates[0].Score {
		t.Fatal(reuse.Cells, reuse.Score, plan.Explain())
	}
	// Every reuse cell is under the lamp: lamp coverage bounds the site.
	env, _ := r.Environment.Value()
	lit := env.LitCells()
	for _, patch := range reuse.Sites.Patches {
		for _, c := range rectCells(patch) {
			if _, ok := lit[c]; !ok {
				t.Fatal("planted outside lamp coverage", c)
			}
		}
	}
	// Unknown environment keeps only outdoor candidates.
	r.Environment = domain.Unknown[ControlledEnvironment]()
	plan, ok = PlanSiteType(r)
	for _, c := range plan.Candidates {
		if !ok || c.Kind != SiteOutdoor {
			t.Fatal(plan.Explain())
		}
	}
}

func TestPlanSiteTypeWinterReusesGreenhouseBeforeBuilding(t *testing.T) {
	r := siteFixture(1.0)
	r.Field.Climate = CropClimate{Sowing: domain.Known(false), DaysRemaining: domain.Unknown[float64]()}
	// Basins are not researched, so the lit soil is the only controlled site.
	r.Basin = domain.Unknown[Infrastructure]()
	r.Environment = domain.Known(siteEnv(21, siteLamp(domain.Cell{X: 6, Z: 6}, true)))
	plan, ok := PlanSiteType(r)
	if !ok || plan.Kind != SiteGreenhouseReuse || len(plan.Buildings) != 0 {
		t.Fatal(plan.Explain())
	}
	if c := siteCandidateOf(plan, SiteOutdoor, "Plant_Corn"); c.Cells != 0 || c.Reason != "outdoor sowing not possible" {
		t.Fatal(c)
	}
	// Without a running lamp the plan builds one on the roofed soil and
	// charges construction and daytime power against the day headroom.
	env := siteEnv(21)
	env.Networks = []PowerHeadroom{siteNetwork(4000, 1700, 600)}
	r.Environment = domain.Known(env)
	plan, ok = PlanSiteType(r)
	if !ok || plan.Kind != SiteGreenhouseNew || len(plan.Buildings) != 1 || plan.Buildings[0].Definition != "SunLamp" {
		t.Fatal(plan.Explain())
	}
	center := plan.Buildings[0].Cell
	for _, patch := range plan.Sites.Patches {
		for _, c := range rectCells(patch) {
			if c == center || !siteWithin(c, center, 5.8) || c.X >= 12 || c.Z >= 12 {
				t.Fatal("patch outside the new lamp's disc", c, center)
			}
		}
	}
	terms := strings.Join([]string{plan.Candidates[0].Terms[len(plan.Candidates[0].Terms)-2].Name, plan.Candidates[0].Terms[len(plan.Candidates[0].Terms)-1].Name}, ",")
	if terms != "construction,power" {
		t.Fatal(plan.Explain())
	}
	// A daytime shortfall (2500W free by day) refuses the 2900W lamp.
	env = siteEnv(21)
	env.Networks = []PowerHeadroom{siteNetwork(3000, 1700, 500)}
	r.Environment = domain.Known(env)
	if plan, ok = PlanSiteType(r); ok {
		t.Fatal("lamp built without daytime headroom", plan.Explain())
	}
	if c := siteCandidateOf(plan, SiteGreenhouseNew, "Plant_Rice"); c.Reason != "no daytime power headroom for a sun lamp" {
		t.Fatal(c.Reason)
	}
}

func TestPlanSiteTypeWinterHeatingAndOutage(t *testing.T) {
	r := siteFixture(1.0)
	r.Field.Climate = CropClimate{Sowing: domain.Known(false), DaysRemaining: domain.Unknown[float64]()}
	r.Basin = domain.Unknown[Infrastructure]()
	// A cold greenhouse is heated on the night headroom.
	r.Environment = domain.Known(siteEnv(-5, siteLamp(domain.Cell{X: 6, Z: 6}, true)))
	plan, ok := PlanSiteType(r)
	if !ok || plan.Kind != SiteGreenhouseReuse {
		t.Fatal(plan.Explain())
	}
	heated := false
	for _, term := range plan.Candidates[0].Terms {
		heated = heated || term.Name == "heating" && term.Value < 0
	}
	if !heated {
		t.Fatal(plan.Explain())
	}
	// No heater available: the cold room is refused.
	r.Heater = domain.Unknown[Infrastructure]()
	if plan, ok = PlanSiteType(r); ok || siteCandidateOf(plan, SiteGreenhouseReuse, "Plant_Rice").Reason != "room too cold and no heater available" {
		t.Fatal(plan.Explain())
	}
	// Night headroom too small for the heater (all generation is solar).
	r = siteFixture(1.0)
	r.Field.Climate = CropClimate{Sowing: domain.Known(false), DaysRemaining: domain.Unknown[float64]()}
	r.Basin = domain.Unknown[Infrastructure]()
	env := siteEnv(-5, siteLamp(domain.Cell{X: 6, Z: 6}, true))
	env.Networks = []PowerHeadroom{siteNetwork(1700, 1700, 100)}
	r.Environment = domain.Known(env)
	if plan, ok = PlanSiteType(r); ok || siteCandidateOf(plan, SiteGreenhouseReuse, "Plant_Rice").Reason != "no night power headroom for heating" {
		t.Fatal(plan.Explain())
	}
	// An outage (no active source) refuses every powered kind; the lamp is
	// off so nothing is lit.
	env = siteEnv(21, siteLamp(domain.Cell{X: 6, Z: 6}, false))
	env.Networks[0].ActiveSource = domain.Known(false)
	r.Environment = domain.Known(env)
	if plan, ok = PlanSiteType(r); ok {
		t.Fatal(plan.Explain())
	}
	if c := siteCandidateOf(plan, SiteGreenhouseNew, "Plant_Rice"); c.Reason != "no daytime power headroom for a sun lamp" {
		t.Fatal(c.Reason)
	}
	if c := siteCandidateOf(plan, SiteGreenhouseReuse, "Plant_Rice"); c.Reason != "no running sun lamp" {
		t.Fatal(c.Reason)
	}
}

func TestPlanSiteTypeHydroponicsOnlyForBasinCrop(t *testing.T) {
	r := siteFixture(0.1)
	r.Field.Climate = CropClimate{Sowing: domain.Known(false), DaysRemaining: domain.Unknown[float64]()}
	// The room floor has no soil: only basins can use the lit cells.
	for i := range r.Field.Site.Cells {
		if c := &r.Field.Site.Cells[i]; c.Cell.X < 12 && c.Cell.Z < 12 {
			c.Fertility = domain.Known(0.0)
		}
	}
	r.Environment = domain.Known(siteEnv(21, siteLamp(domain.Cell{X: 6, Z: 6}, true)))
	plan, ok := PlanSiteType(r)
	if !ok || plan.Kind != SiteHydroponics || plan.Crop.Name != "Plant_Rice" || len(plan.Buildings) == 0 {
		t.Fatal(plan.Explain())
	}
	env, _ := r.Environment.Value()
	lit := env.LitCells()
	for _, b := range plan.Buildings {
		// The native occupied rect of a north-facing 1x4 basin is z-1..z+2
		// around its placement cell; every cell of it must be lit.
		footprint := siteBasinFootprint(b.Cell)
		if len(footprint) != 4 || footprint[0].Z != b.Cell.Z-1 || footprint[3].Z != b.Cell.Z+2 {
			t.Fatal("basin footprint is not the native centred rect", b, footprint)
		}
		for _, c := range footprint {
			if _, ok := lit[c]; !ok || b.Definition != "HydroponicsBasin" {
				t.Fatal("basin outside lamp coverage", b)
			}
		}
	}
	if c := siteCandidateOf(plan, SiteHydroponics, "Plant_Corn"); c.Reason != "crop is not the basin's native crop" {
		t.Fatal(c.Reason)
	}
	// Night headroom caps the basin count: 3000-1700-600 = 700W night, 10 basins.
	if len(plan.Buildings) > 10 {
		t.Fatal(len(plan.Buildings))
	}
	// A crop without the Hydroponic sow tag is incompatible even as the default.
	for i := range r.Field.Choices {
		r.Field.Choices[i].SowTags = domain.Known([]string{"Ground"})
	}
	if plan, ok = PlanSiteType(r); ok || siteCandidateOf(plan, SiteHydroponics, "Plant_Rice").Reason != "crop is not the basin's native crop" {
		t.Fatal(plan.Explain())
	}
}

func TestPlanSiteTypeDarkRoomOnlyForDarkCrops(t *testing.T) {
	r := siteFixture(0.1)
	r.Field.Climate = CropClimate{Sowing: domain.Known(false), DaysRemaining: domain.Unknown[float64]()}
	env := siteEnv(21)
	env.Networks = nil
	r.Environment = domain.Known(env)
	// No lamp, no power: nothing lit and no lamp to build.
	plan, ok := PlanSiteType(r)
	if ok || siteCandidateOf(plan, SiteDarkRoom, "Plant_Rice").Reason != "crop needs light" {
		t.Fatal(plan.Explain())
	}
	fungus := fieldCrop("Plant_Fungus", 6, 0.5, 0.5, 0.4)
	fungus.SowTags = domain.Known([]string{"Ground"})
	fungus.MinGlow = domain.Known(0.0)
	r.Field.Choices = append(r.Field.Choices, fungus)
	plan, ok = PlanSiteType(r)
	if !ok || plan.Kind != SiteDarkRoom || plan.Crop.Name != "Plant_Fungus" || len(plan.Buildings) != 0 {
		t.Fatal(plan.Explain())
	}
	for _, patch := range plan.Sites.Patches {
		for _, c := range rectCells(patch) {
			if c.X >= 12 || c.Z >= 12 {
				t.Fatal("dark crop planted outdoors", c)
			}
		}
	}
	// A lit room is not dark: the lamp's cells are excluded.
	env = siteEnv(21, siteLamp(domain.Cell{X: 6, Z: 6}, true))
	env.Networks = nil
	r.Environment = domain.Known(env)
	plan, _ = PlanSiteType(r)
	lit := env.LitCells()
	for _, c := range siteCandidateOf(plan, SiteDarkRoom, "Plant_Fungus").Sites.Patches {
		for _, cell := range rectCells(c) {
			if _, ok := lit[cell]; ok {
				t.Fatal("dark crop planted under a lamp", cell)
			}
		}
	}
}
