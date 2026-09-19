package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// noConditions is a known, empty condition census: no growth pause.
var noConditions = domain.Known([]DisasterCondition{})

func timedCondition(definition string, ticks int64) DisasterCondition {
	return DisasterCondition{ID: definition, Definition: definition, TicksLeft: &ticks}
}

func TestHarvestGapDaysBridgesTheNonGrowingYear(t *testing.T) {
	cases := []struct {
		name     string
		calendar Calendar
		want     float64
	}{
		{"the first frost day budgets the whole winter", Calendar{Season: "Fall", GrowingDays: 40, GrowingDaysRemaining: 0, Sowing: true}, 23},
		{"midsummer phases the winter in over one field cycle", Calendar{Season: "Summer", GrowingDays: 40, GrowingDaysRemaining: 15.25, Sowing: true}, 11.5},
		{"early summer holds no winter stock yet", Calendar{Season: "Summer", GrowingDays: 40, GrowingDaysRemaining: 35, Sowing: true}, 0},
		{"winter budgets the wait until growth resumes", Calendar{Season: "Winter", GrowingDays: 40, GrowingDaysRemaining: 0, GrowingDaysUntil: 12}, 15},
		{"a year-round tile has no gap", Calendar{Season: "PermanentSummer", GrowingDays: 60, GrowingDaysRemaining: 60, Sowing: true}, 0},
		{"a tile that never grows is capped at one year", Calendar{Season: "PermanentWinter", GrowingDays: 0, GrowingDaysRemaining: 0, GrowingDaysUntil: 60}, 60},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got, known := HarvestGapDays(domain.Known(tc.calendar), noConditions).Value()
			if !known || math.Abs(got-tc.want) > 1e-9 {
				t.Fatalf("gap=%v known=%v want %v", got, known, tc.want)
			}
		})
	}
	if _, known := HarvestGapDays(domain.Unknown[Calendar](), noConditions).Value(); known {
		t.Fatal("unknown calendar produced a gap")
	}
	for _, bad := range []Calendar{{GrowingDays: 61}, {GrowingDaysUntil: -1}, {GrowingDaysRemaining: math.NaN()}, {DayOfYear: 60}} {
		if bad.Valid() {
			t.Fatal("invalid calendar accepted", bad)
		}
		if _, known := HarvestGapDays(domain.Known(bad), noConditions).Value(); known {
			t.Fatal("invalid calendar produced a gap", bad)
		}
	}
}

func TestSeasonalPolicyWidensFoodAndWoodTargetsTogether(t *testing.T) {
	base := DefaultRoutinePolicy()
	winter := domain.Known(Calendar{Season: "Winter", GrowingDays: 40, GrowingDaysUntil: 12})
	p := base.Seasonal(winter, noConditions)
	if p.FoodTargetDays != base.FoodTargetDays+15 || p.FoodMinDays != base.FoodMinDays+15 || p.FootholdFoodDays != base.FootholdFoodDays {
		t.Fatal(p)
	}
	scale := func(n int64) int64 { return int64(math.Ceil(float64(n) * p.FoodTargetDays / base.FoodTargetDays)) }
	if p.WoodMin != scale(base.WoodMin) || p.WoodTarget != scale(base.WoodTarget) || p.WoodMax != scale(base.WoodMax) {
		t.Fatal(p)
	}
	if err := p.Validate(); err != nil {
		t.Fatal(err)
	}
	if got := base.Seasonal(domain.Unknown[Calendar](), noConditions); got.FoodTargetDays != base.FoodTargetDays || got.WoodTarget != base.WoodTarget {
		t.Fatal("unknown calendar changed the targets", got)
	}
	if got := base.Seasonal(domain.Known(Calendar{GrowingDays: 60, GrowingDaysRemaining: 60, Sowing: true}), noConditions); got.FoodTargetDays != base.FoodTargetDays || got.WoodTarget != base.WoodTarget || got.WoodMax != base.WoodMax {
		t.Fatal("a year-round tile changed the targets")
	}
	capped := base.Seasonal(domain.Known(Calendar{GrowingDaysUntil: 60}), noConditions)
	if capped.FoodTargetDays != YearDays || capped.FoodMinDays != YearDays-(base.FoodTargetDays-base.FoodMinDays) {
		t.Fatal("food thresholds exceed one year", capped)
	}
	if err := capped.Validate(); err != nil {
		t.Fatal(err)
	}
}

func TestRoutineFoodAndWoodLatchesHoldThroughTheHarvestGap(t *testing.T) {
	f := stableRoutine()
	// 8 days of food and 400 wood satisfy the flat targets (7 days, 350).
	if r := needs(t, f, RoutineLatches{}); r.Latches.Food || r.Latches.Wood || hasNeed(r, EnsureFoodSupply) {
		t.Fatal(r)
	}
	// The first frost day with a 20-day winter ahead: the same stock is a
	// deficit against the seasonal thresholds (26/30 days, 515/1500 wood)
	// and stays one until it covers the gap.
	f.Calendar = domain.Known(Calendar{Season: "Fall", DayOfYear: 40, GrowingDays: 40, GrowingDaysRemaining: 0, Sowing: true})
	r := needs(t, f, RoutineLatches{})
	if !r.Latches.Food || !r.Latches.Wood || !hasNeed(r, EnsureFoodSupply) || !hasNeed(r, MaintainWood) {
		t.Fatal(r)
	}
	f.FoodDays = domain.Known(29.0)
	f.Wood = domain.Known(int64(1500))
	r = needs(t, f, r.Latches)
	if !r.Latches.Food || !r.Latches.Wood {
		t.Fatal("just under the seasonal targets released the latches", r)
	}
	f.FoodDays = domain.Known(30.5)
	f.Wood = domain.Known(int64(1501))
	r = needs(t, f, r.Latches)
	if r.Latches.Food || r.Latches.Wood {
		t.Fatal(r)
	}
	// The foothold food gate keeps its flat minimum: a stocked larder short
	// of the winter target is a development deficit, not a foothold failure.
	if !positive(r.Gates.Food) {
		t.Fatal(r.Gates)
	}
	f.Calendar = domain.Known(Calendar{GrowingDays: 61})
	if _, err := DetectRoutine(f, r.Latches, DefaultRoutinePolicy()); err == nil {
		t.Fatal("invalid calendar accepted")
	}
}

func TestHarvestGapDaysExtendsByAnObservedGrowthPause(t *testing.T) {
	summer := domain.Known(Calendar{Season: "Summer", GrowingDays: 40, GrowingDaysRemaining: 35, Sowing: true})
	winter := domain.Known(Calendar{Season: "Winter", GrowingDays: 40, GrowingDaysUntil: 12})
	cases := []struct {
		name       string
		calendar   domain.Fact[Calendar]
		conditions domain.Fact[[]DisasterCondition]
		want       float64
	}{
		{"a cold snap in summer delays the next harvest by its remaining days", summer, domain.Known([]DisasterCondition{timedCondition(ConditionColdSnap, 6*60000)}), 6},
		{"a volcanic winter in summer does the same", summer, domain.Known([]DisasterCondition{timedCondition(ConditionVolcanicWinter, 20*60000)}), 20},
		{"the longest pause counts once, not the sum", summer, domain.Known([]DisasterCondition{timedCondition(ConditionColdSnap, 6*60000), timedCondition(ConditionVolcanicWinter, 20*60000)}), 20},
		{"a pause shorter than the seasonal wait changes nothing", winter, domain.Known([]DisasterCondition{timedCondition(ConditionColdSnap, 5*60000)}), 15},
		{"a pause outlasting the seasonal wait replaces it", winter, domain.Known([]DisasterCondition{timedCondition(ConditionVolcanicWinter, 30*60000)}), 33},
		{"a pause on an unknown calendar is the whole gap", domain.Unknown[Calendar](), domain.Known([]DisasterCondition{timedCondition(ConditionVolcanicWinter, 20*60000)}), 23},
		{"a condition without a remaining-duration read contributes nothing", summer, domain.Known([]DisasterCondition{{ID: "cold", Definition: ConditionColdSnap}}), 0},
		{"a solar flare is not a growth pause", summer, domain.Known([]DisasterCondition{timedCondition(ConditionSolarFlare, 6*60000)}), 0},
		{"an unknown condition census is no pause", summer, domain.Unknown[[]DisasterCondition](), 0},
		{"the whole is capped at one year", winter, domain.Known([]DisasterCondition{timedCondition(ConditionVolcanicWinter, 90*60000)}), 60},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got, known := HarvestGapDays(tc.calendar, tc.conditions).Value()
			if !known || math.Abs(got-tc.want) > 1e-9 {
				t.Fatalf("gap=%v known=%v want %v", got, known, tc.want)
			}
		})
	}
	if _, known := HarvestGapDays(domain.Unknown[Calendar](), domain.Unknown[[]DisasterCondition]()).Value(); known {
		t.Fatal("no calendar and no census produced a gap")
	}
	if _, known := HarvestGapDays(domain.Unknown[Calendar](), domain.Known([]DisasterCondition{{ID: "cold", Definition: ConditionColdSnap}})).Value(); known {
		t.Fatal("an untimed condition on an unknown calendar produced a gap")
	}
	// The seasonal policy widens both thresholds by the pause, so a stocked
	// summer larder reads as a deficit while a volcanic winter is observed.
	base := DefaultRoutinePolicy()
	p := base.Seasonal(summer, domain.Known([]DisasterCondition{timedCondition(ConditionVolcanicWinter, 20*60000)}))
	if p.FoodTargetDays != base.FoodTargetDays+20 || p.FoodMinDays != base.FoodMinDays+20 || p.WoodTarget <= base.WoodTarget {
		t.Fatal(p)
	}
	if err := p.Validate(); err != nil {
		t.Fatal(err)
	}
}
