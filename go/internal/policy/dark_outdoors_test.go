package policy

import (
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TestPlanSiteTypeDarkBiomeNeverSowsOutdoors (#1712): in a permanently dark
// biome the season says sow, but no outdoor candidate is plantable; the sun
// lamp greenhouse is the plan. An unknown darkness plans no outdoor
// field; a lit biome plans the outdoor field as before.
func TestPlanSiteTypeDarkBiomeNeverSowsOutdoors(t *testing.T) {
	r := siteFixture(1.0)
	env := siteEnv(21)
	env.Networks = []PowerHeadroom{siteNetwork(4000, 1700, 600)}
	r.Environment = domain.Known(env)
	if plan, ok := PlanSiteType(r); !ok || plan.Kind != SiteOutdoor {
		t.Fatal("lit season did not plan outdoors", plan.Explain())
	}
	r.Field.Climate.OutdoorsDark = domain.Known(false)
	if plan, ok := PlanSiteType(r); !ok || plan.Kind != SiteOutdoor {
		t.Fatal("a lit biome did not plan outdoors", plan.Explain())
	}
	r.Field.Climate.OutdoorsDark = domain.Unknown[bool]()
	if plan, ok := PlanSiteType(r); ok && plan.Kind == SiteOutdoor {
		t.Fatal("unknown darkness planned outdoors", plan.Explain())
	}
	r.Field.Climate.OutdoorsDark = domain.Known(true)
	plan, ok := PlanSiteType(r)
	if !ok || plan.Kind != SiteGreenhouseNew || len(plan.Buildings) != 1 || plan.Buildings[0].Definition != "SunLamp" {
		t.Fatal(plan.Explain())
	}
	for _, c := range plan.Candidates {
		if c.Kind == SiteOutdoor && (c.Cells != 0 || c.Reason != "outdoors permanently dark") {
			t.Fatal(c)
		}
	}
	// Without a controlled site nothing is sown at all.
	r.Environment = domain.Unknown[ControlledEnvironment]()
	if plan, ok = PlanSiteType(r); ok {
		t.Fatal("sowed outdoors in the dark", plan.Explain())
	}
}

// TestDarkBiomeBlocksOutdoorHaySocialAndFieldPlans (#1712): every outdoor
// sowing planner reads the same fact.
func TestDarkBiomeBlocksOutdoorHaySocialAndFieldPlans(t *testing.T) {
	climate := CropClimate{Sowing: domain.Known(true), DaysRemaining: domain.Known(30.0), OutdoorsDark: domain.Known(true)}
	if got, _ := climate.SowingOutdoors().Value(); got {
		t.Fatal("dark biome sows outdoors")
	}
	climate.OutdoorsDark = domain.Known(false)
	if got, _ := climate.SowingOutdoors().Value(); !got {
		t.Fatal("lit biome blocked sowing")
	}
	hay := CropChoice{Name: "Plant_Haygrass", Harvests: domain.Known(HayResource), Available: domain.Known(true), HarvestNutrition: domain.Known(1.0), GrowDays: domain.Known(5.0)}
	if _, ok := PlanHayField(domain.Known(10.0), hay, climate); !ok {
		t.Fatal("lit hay field refused")
	}
	social := CropChoice{Name: "Plant_Hops", Harvests: domain.Known(Resource("Hops")), Available: domain.Known(true), GrowDays: domain.Known(5.0)}
	if PlanSocialCrop(social, climate, 0) == 0 {
		t.Fatal("lit social crop refused")
	}
	climate.OutdoorsDark = domain.Known(true)
	if _, ok := PlanHayField(domain.Known(10.0), hay, climate); ok {
		t.Fatal("hay field in the dark")
	}
	if PlanSocialCrop(social, climate, 0) != 0 {
		t.Fatal("social crop in the dark")
	}
	unknown := climate
	unknown.OutdoorsDark = domain.Unknown[bool]()
	if _, ok := unknown.SowingOutdoors().Value(); ok {
		t.Fatal("unknown darkness counted as a sowing answer")
	}
	if _, ok := PlanHayField(domain.Known(10.0), hay, unknown); ok {
		t.Fatal("hay field on unknown darkness")
	}
	if PlanSocialCrop(social, unknown, 0) != 0 {
		t.Fatal("social crop on unknown darkness")
	}
	req := fieldRequest(1.0)
	req.Climate.OutdoorsDark = domain.Unknown[bool]()
	if _, ok := PlanField(req); ok {
		t.Fatal("outdoor field on unknown darkness")
	}
	req.Climate.OutdoorsDark = domain.Known(true)
	if _, ok := PlanField(req); ok {
		t.Fatal("outdoor field in the dark")
	}
}

// TestSkyDarkLightsUnroofedBenchInDarkBiome (#1712): a permanently dark
// biome measures unroofed work cells like an eclipse, with no condition
// active; an unknown darkness is an error.
func TestSkyDarkLightsUnroofedBenchInDarkBiome(t *testing.T) {
	p := DefaultLightingPolicy()
	census := domain.Known(lightingCensus())
	none := domain.Known([]DisasterCondition{})
	if _, known := SkyDarkHold(none, domain.Unknown[bool]()).Value(); known {
		t.Fatal("unknown biome darkness counted as known")
	}
	if _, err := ReviewLighting(census, nil, p, SkyDarkHold(none, domain.Unknown[bool]())); !errors.Is(err, ErrOutdoorsDarkUnknown) {
		t.Fatal("lighting review assumed a lit sky", err)
	}
	if got, _ := SkyDarkHold(none, domain.Known(false)).Value(); got {
		t.Fatal("lit biome held the sky dark")
	}
	if got, _ := SkyDarkHold(none, domain.Known(true)).Value(); !got {
		t.Fatal("dark biome did not hold the sky dark")
	}
	lit, err := ReviewLighting(census, nil, p, SkyDarkHold(none, domain.Known(false)))
	if err != nil || len(lit.Dark) != 1 || lit.SkyDark {
		t.Fatal(lit, err)
	}
	dark, err := ReviewLighting(census, nil, p, SkyDarkHold(none, domain.Known(true)))
	if err != nil || !dark.SkyDark || len(dark.Dark) != len(lit.Dark)+1 {
		t.Fatal(dark, err)
	}
	flare := int64(1000)
	eclipse := domain.Known([]DisasterCondition{{ID: "e", Definition: ConditionEclipse, TicksLeft: &flare}})
	if got, _ := SkyDarkHold(eclipse, domain.Known(false)).Value(); !got {
		t.Fatal("eclipse no longer holds the sky dark")
	}
}
