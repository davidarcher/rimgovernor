package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func conditionsOf(def string, days float64) domain.Fact[[]DisasterCondition] {
	ticks := int64(days * domain.TicksPerDay)
	return domain.Known([]DisasterCondition{{ID: def, Definition: def, TicksLeft: &ticks}})
}

func exposure(cells, unroofed int64) FieldExposure {
	return FieldExposure{ZoneCells: domain.Known(cells), UnroofedCells: domain.Known(unroofed), Planted: domain.Known(cells), Blighted: domain.Known(int64(0))}
}

func weight(t *testing.T, f domain.Fact[float64]) float64 {
	t.Helper()
	w, known := f.Value()
	if !known {
		t.Fatal("weight unknown")
	}
	return w
}

func TestFalloutRiskIsTheUnroofedShareWhileTheConditionIsOn(t *testing.T) {
	fallout := conditionsOf(ConditionToxicFallout, 10)
	if w := weight(t, FalloutRisk(fallout, exposure(20, 5))); w != 0.25 {
		t.Fatalf("a quarter open: %v", w)
	}
	if w := weight(t, FalloutRisk(fallout, exposure(20, 20))); w != 1 {
		t.Fatalf("all open: %v", w)
	}
	if w := weight(t, FalloutRisk(conditionsOf(ConditionEclipse, 1), exposure(20, 20))); w != 0 {
		t.Fatalf("another condition: %v", w)
	}
	if w := weight(t, FalloutRisk(domain.Known([]DisasterCondition{}), exposure(20, 20))); w != 0 {
		t.Fatalf("no condition: %v", w)
	}
	for name, f := range map[string]domain.Fact[float64]{
		"census unknown": FalloutRisk(domain.Unknown[[]DisasterCondition](), exposure(20, 20)),
		"roofs unknown":  FalloutRisk(fallout, FieldExposure{}),
		"no cells":       FalloutRisk(fallout, exposure(0, 0)),
		"more than held": FalloutRisk(fallout, exposure(4, 5)),
	} {
		if _, known := f.Value(); known {
			t.Errorf("%s: want unknown", name)
		}
	}
	// A census that is unknown still prices a field without fallout as unknown
	// even when the roof data is held: absence of the condition is not assumed.
	if _, known := FalloutRisk(domain.Unknown[[]DisasterCondition](), FieldExposure{}).Value(); known {
		t.Error("nothing known must stay unknown")
	}
}

func TestBlightRiskIsTheBlightedShareOfPlantedCells(t *testing.T) {
	e := FieldExposure{Planted: domain.Known(int64(40)), Blighted: domain.Known(int64(10))}
	if w := weight(t, BlightRisk(e)); w != 0.25 {
		t.Fatalf("a quarter blighted: %v", w)
	}
	e.Blighted = domain.Known(int64(0))
	if w := weight(t, BlightRisk(e)); w != 0 {
		t.Fatalf("clean field: %v", w)
	}
	for name, e := range map[string]FieldExposure{
		"planted unknown":  {Blighted: domain.Known(int64(1))},
		"blighted unknown": {Planted: domain.Known(int64(4))},
		"nothing planted":  {Planted: domain.Known(int64(0)), Blighted: domain.Known(int64(0))},
		"above planted":    {Planted: domain.Known(int64(2)), Blighted: domain.Known(int64(3))},
	} {
		if _, known := BlightRisk(e).Value(); known {
			t.Errorf("%s: want unknown", name)
		}
	}
}

func TestFrostRiskIsTheFrozenShareOfTheLeadWindow(t *testing.T) {
	cal := domain.Known(Calendar{GrowingDaysRemaining: 4, NonGrowingDays: 10, GrowingDays: 30})
	if w := weight(t, FrostRisk(domain.Known(20.0), cal, exposure(10, 10))); w != 0.5 {
		t.Fatalf("10 of 20 days frozen, all open: %v", w)
	}
	if w := weight(t, FrostRisk(domain.Known(20.0), cal, exposure(10, 5))); w != 0.25 {
		t.Fatalf("half roofed: %v", w)
	}
	if w := weight(t, FrostRisk(domain.Known(4.0), cal, exposure(10, 10))); w != 0 {
		t.Fatalf("harvest lands before the frost: %v", w)
	}
	if w := weight(t, FrostRisk(domain.Known(5.0), domain.Known(Calendar{GrowingDaysRemaining: 1, NonGrowingDays: 40}), exposure(10, 10))); w != 1 {
		t.Fatalf("clamped at 1: %v", w)
	}
	for name, f := range map[string]domain.Fact[float64]{
		"lead unknown":     FrostRisk(domain.Unknown[float64](), cal, exposure(10, 10)),
		"calendar unknown": FrostRisk(domain.Known(20.0), domain.Unknown[Calendar](), exposure(10, 10)),
		"calendar invalid": FrostRisk(domain.Known(20.0), domain.Known(Calendar{GrowingDaysRemaining: math.NaN()}), exposure(10, 10)),
		"roofs unknown":    FrostRisk(domain.Known(20.0), cal, FieldExposure{}),
	} {
		if _, known := f.Value(); known {
			t.Errorf("%s: want unknown", name)
		}
	}
}

func TestCropPauseDaysSumsNoConditionsButTheLongest(t *testing.T) {
	two := func(d1, d2 string, n1, n2 float64) domain.Fact[[]DisasterCondition] {
		a, _ := conditionsOf(d1, n1).Value()
		b, _ := conditionsOf(d2, n2).Value()
		return domain.Known(append(a, b...))
	}
	if d := CropPauseDays(conditionsOf(ConditionVolcanicWinter, 20)); d != 20 {
		t.Fatalf("volcanic winter: %v", d)
	}
	if d := CropPauseDays(conditionsOf(ConditionEclipse, 0.5)); d != 0.5 {
		t.Fatalf("eclipse: %v", d)
	}
	if d := CropPauseDays(two(ConditionEclipse, ConditionColdSnap, 1, 6)); d != 6 {
		t.Fatalf("longest of two: %v", d)
	}
	for name, c := range map[string]domain.Fact[[]DisasterCondition]{
		"fallout does not pause": conditionsOf(ConditionToxicFallout, 9),
		"unknown census":         domain.Unknown[[]DisasterCondition](),
		"no remaining read":      domain.Known([]DisasterCondition{{Definition: ConditionColdSnap}}),
	} {
		if d := CropPauseDays(c); d != 0 {
			t.Errorf("%s: %v", name, d)
		}
	}
}

func riskField() FoodField {
	f := cropField()
	f.Plan.Sites.Cells = 10
	f.RemainingGrowDays = domain.Known(5.0)
	f.Exposure = FieldExposure{ZoneCells: domain.Known(int64(10)), UnroofedCells: domain.Known(int64(10)), Planted: domain.Known(int64(10)), Blighted: domain.Known(int64(2))}
	return f
}

func TestCropChannelsCarryLeadPauseAndRisks(t *testing.T) {
	season := CropSeason{Calendar: domain.Known(Calendar{GrowingDaysRemaining: 30, GrowingDays: 40}), Conditions: conditionsOf(ConditionVolcanicWinter, 20)}
	c := CropChannels([]FoodField{riskField()}, CropKitchen{}, season)[0]
	if lead, _ := c.LeadDays.Value(); lead != 25 {
		t.Fatalf("lead %v: want the 5 growth days plus the 20 day pause", lead)
	}
	if len(c.Risk) != 1 || c.Risk[0].Kind != CandidateBlight || c.Risk[0].Weight != 0.2 {
		t.Fatalf("risks %+v: want blight 0.2 (no fallout, harvest before frost beyond the pause)", c.Risk)
	}
	season.Conditions = conditionsOf(ConditionToxicFallout, 9)
	c = CropChannels([]FoodField{riskField()}, CropKitchen{}, season)[0]
	kinds := map[CandidateRiskKind]float64{}
	for _, r := range c.Risk {
		kinds[r.Kind] = r.Weight
	}
	if kinds[CandidateFallout] != 1 || kinds[CandidateBlight] != 0.2 || len(kinds) != 2 {
		t.Fatalf("risks %+v: want fallout 1 and blight 0.2", c.Risk)
	}
	if _, err := SupplyFoodPlan(foodPlanRequest(c)); err != nil {
		t.Fatalf("risks must validate: %v", err)
	}
}

func TestCropChannelsPriceNoRiskFromUnknownFacts(t *testing.T) {
	f := riskField()
	f.Exposure = FieldExposure{}
	c := CropChannels([]FoodField{f}, CropKitchen{}, CropSeason{Conditions: conditionsOf(ConditionToxicFallout, 9)})[0]
	if len(c.Risk) != 0 {
		t.Fatalf("unknown exposure priced risks %+v", c.Risk)
	}
	f.RemainingGrowDays = domain.Unknown[float64]()
	c = CropChannels([]FoodField{f}, CropKitchen{}, CropSeason{Conditions: conditionsOf(ConditionEclipse, 1)})[0]
	if _, known := c.LeadDays.Value(); known {
		t.Fatal("an unknown lead stays unknown under a pause")
	}
}

func newFieldRequest(t *testing.T) FieldRequest {
	t.Helper()
	crop := CropChoice{Name: "Plant_Rice", Available: domain.Known(true), Edible: domain.Known(true), DietAllowed: domain.Known(true), GrowDays: domain.Known(5.0),
		HarvestNutrition: domain.Known(1.0), Demand: domain.Known(2.0), HarvestWork: domain.Known(100.0), RotDays: domain.Known(10.0), Perishable: domain.Known(true)}
	return FieldRequest{Choices: []CropChoice{crop}, Colonists: domain.Known(int64(3)), ReserveDays: 10, Coverage: domain.Known(0.0),
		Climate:  CropClimate{Sowing: domain.Known(true), DaysRemaining: domain.Known(60.0), OutdoorsDark: domain.Known(false)},
		Calendar: domain.Known(Calendar{GrowingDays: 60, GrowingDaysRemaining: 60, Sowing: true})}
}

func TestNewFieldChannelsLieUnderOpenSkyAndWaitOutThePause(t *testing.T) {
	r := newFieldRequest(t)
	base := NewFieldChannels(r, CropKitchen{})
	if len(base) == 0 {
		t.Fatal("no new field offered")
	}
	baseLead, _ := base[0].LeadDays.Value()
	r.Conditions = conditionsOf(ConditionToxicFallout, 9)
	fallout := NewFieldChannels(r, CropKitchen{})[0]
	if len(fallout.Risk) != 1 || fallout.Risk[0] != (CandidateRisk{CandidateFallout, 1}) {
		t.Fatalf("a field sown in fallout: %+v", fallout.Risk)
	}
	r.Conditions = conditionsOf(ConditionColdSnap, 7)
	paused := NewFieldChannels(r, CropKitchen{})[0]
	if lead, _ := paused.LeadDays.Value(); lead != math.Min(YearDays, baseLead+7) {
		t.Fatalf("lead %v: want %v plus the 7 day pause", lead, baseLead)
	}
}
