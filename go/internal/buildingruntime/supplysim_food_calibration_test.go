package buildingruntime

// Calibration (epic #2140, child 3): one test per ground-truth acceptance case
// asserting that the supplysim world reproduces the direction and ordering the
// case asserts. Tolerances: rates within 15% of the fact-derived expectation,
// day counts within 2 days, ordering exact. The cases run against a real game
// (go/internal/nativeaccept/cases/{food,farm,trade}); these run offline.

import (
	"math"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/supplysim"
)

const (
	calibrationRateTolerance = 0.15
	calibrationDayTolerance  = 2
)

func withinRate(t *testing.T, name string, got, want float64) {
	t.Helper()
	if math.Abs(got-want) > calibrationRateTolerance*want {
		t.Errorf("%s = %.4f, want %.4f within %.0f%%", name, got, want, calibrationRateTolerance*100)
	}
}

func withinDays(t *testing.T, name string, got, want int) {
	t.Helper()
	if d := got - want; d < -calibrationDayTolerance || d > calibrationDayTolerance {
		t.Errorf("%s = %d, want %d within %d days", name, got, want, calibrationDayTolerance)
	}
}

// meanDelivered is the mean nutrition per day a source delivered over days
// [from, to).
func meanDelivered(rep supplysim.Report, id string, nutr float64, from, to int) float64 {
	var sum float64
	for _, d := range rep.Days[from:to] {
		sum += d.Delivered[id] * nutr
	}
	return sum / float64(to-from)
}

// food/reserve: a colony holding the default reserve of unforbidden pemmican
// and no other food eats it; the runway the reserve buys is the reserve days.
func TestFoodCalibrationReserve(t *testing.T) {
	const colonists, pemmicanNutrition = 6, 0.05
	reserveDays := policy.DefaultFoodReserveDays
	units := math.Ceil(reserveDays * colonists * foodNutritionPerColonist / pemmicanNutrition)
	sc := foodScenario{name: "reserve", colonists: colonists, perDay: colonists * foodNutritionPerColonist, stock: units * pemmicanNutrition}
	rep := runFood(sc, int(reserveDays)+6).rep
	withinRate(t, "runway at the end of day 0", rep.Days[0].Runway[supplysim.Nutrition], reserveDays-1)
	withinDays(t, "first starved day", rep.FirstStarved[supplysim.Nutrition], int(reserveDays))
	for i := 1; i < int(reserveDays); i++ {
		if rep.Days[i].Runway[supplysim.Nutrition] >= rep.Days[i-1].Runway[supplysim.Nutrition] {
			t.Fatalf("the reserve did not draw down on day %d", i)
		}
		if rep.Days[i].Unmet[supplysim.Nutrition] != 0 {
			t.Fatalf("colonists starved on day %d while the reserve lasted", i)
		}
	}
}

// fishingScenario is one fishing region feeding colonists colonists.
func fishingScenario(colonists int, maxPop float64, fishers int) foodScenario {
	const nutr = 0.5
	return foodScenario{name: "fishing", colonists: colonists, perDay: foodNutritionPerColonist * float64(colonists), stock: foodNutritionPerColonist * float64(colonists) * 3,
		specs: []srcSpec{{kind: policy.FoodFishing, nutr: nutr, src: supplysim.NewFishing("water", maxPop, maxPop, fishers, nutr)}}}
}

// food/fishing: the ledger opens fishing at min(0.025 x max x nutritionPerFish,
// pawn capacity); a water body below its floor pauses, and two colonists feed
// on it for 15 days without soil.
func TestFoodCalibrationFishing(t *testing.T) {
	for _, tc := range []struct {
		name    string
		maxPop  float64
		fishers int
	}{{"regeneration-limited", 100, 3}, {"capacity-limited", 400, 1}} {
		t.Run(tc.name, func(t *testing.T) {
			sc := fishingScenario(8, tc.maxPop, tc.fishers)
			spec := sc.specs[0]
			rate := math.Min(supplysim.FishingRegenFraction*tc.maxPop*spec.nutr, float64(tc.fishers)*supplysim.FishPerFisherDay*spec.nutr)
			a := &foodAdapter{sc: sc}
			rep := supplysim.Run(sc.world(1), a, 90)
			withinRate(t, "steady catch nutrition per day", meanDelivered(rep, "water", spec.nutr, 60, 90), rate)
			// The planner reads the same rate off the water body.
			for _, e := range a.plans[1].Portfolio {
				if e.Channel.Kind == policy.FoodFishing {
					n, _ := e.Channel.NutritionPerDay.Value()
					withinRate(t, "planned fishing nutrition", n, rate)
					return
				}
			}
			t.Fatal("no fishing row planned")
		})
	}
	t.Run("two-colonists-fish-for-15-days", func(t *testing.T) {
		sc := fishingScenario(2, 300, 3)
		res := runFood(sc, 15)
		if res.rep.Starved(supplysim.Nutrition) {
			t.Fatalf("two colonists starved on fishing: first day %d", res.rep.FirstStarved[supplysim.Nutrition])
		}
		if res.rep.Delivered["water"] == 0 {
			t.Fatal("the planner never opened fishing")
		}
	})
	t.Run("paused-below-floor", func(t *testing.T) {
		sc := fishingScenario(2, 300, 3)
		sc.shocks = []supplysim.Shock{{Day: 3, Kind: supplysim.DestroyStock, Source: "water", Factor: 0.9}}
		rep := runFood(sc, 12).rep
		if rep.Days[4].Delivered["water"] != 0 {
			t.Fatal("fishing delivered below the population floor")
		}
	})
}

// cropScenario is one crop field grown inside a one-year window.
func cropScenario(grow int, window supplysim.Window) foodScenario {
	const cells, nutr = 12.0, 3.0
	return foodScenario{name: "crop", colonists: 4, perDay: 1, stock: 1000, specs: []srcSpec{{kind: policy.FoodCrop, nutr: nutr, cells: cells, grow: grow, window: window,
		src: supplysim.NewCrop("rice", cells, grow, nutr, window, cells)}}}
}

func harvestDays(rep supplysim.Report, id string) []int {
	var days []int
	for _, d := range rep.Days {
		if d.Delivered[id] > 0 {
			days = append(days, d.Day)
		}
	}
	return days
}

// farm/calendar: crops grow only inside the growing period and the harvest gap
// the calendar phases in before the frost is the gap it reads on the first
// non-growing day (#317), which is the drought the field then lives through.
func TestFoodCalibrationFarmCalendar(t *testing.T) {
	const grow = 3 // policy's first harvest cycle (rice)
	window := supplysim.Window{Period: policy.YearDays, From: 0, To: 45}
	sc := cropScenario(grow, window)
	rep := supplysim.Run(sc.world(1), supplysim.PlannerFunc(func(v supplysim.WorldView) []supplysim.Command {
		return []supplysim.Command{{Kind: supplysim.Open, Source: "rice"}}
	}), 2*policy.YearDays)
	harvests := harvestDays(rep, "rice")
	if len(harvests) == 0 {
		t.Fatal("no harvest")
	}
	withinDays(t, "first harvest day", harvests[0], grow+1)
	for _, h := range harvests {
		if h > window.To+grow+1 && h < policy.YearDays {
			t.Fatalf("harvest on day %d, past the growing period and its last cycle", h)
		}
	}
	// The gap on the last growing day equals the gap on the first non-growing
	// day, and a growing-period calendar on either day is valid.
	last, first := calendarOn(window, window.To-1), calendarOn(window, window.To)
	if !last.Valid() || !first.Valid() {
		t.Fatal("calendar out of range")
	}
	gapLast, _ := policy.HarvestGapDays(domain.Known(last), domain.Unknown[[]policy.DisasterCondition]()).Value()
	gapFirst, _ := policy.HarvestGapDays(domain.Known(first), domain.Unknown[[]policy.DisasterCondition]()).Value()
	withinDays(t, "gap phased in on the last growing day vs the first non-growing day", int(gapLast), int(gapFirst))
	// The sim's drought from the end of the growing period to the next
	// harvest is that gap.
	next := -1
	for _, h := range harvests {
		if h >= window.To {
			next = h
			if h > policy.YearDays {
				break
			}
		}
	}
	drought := 0
	for _, h := range harvests {
		if h > window.To+grow && h > policy.YearDays {
			drought = h - window.To
			break
		}
	}
	if next < 0 || drought == 0 {
		t.Fatalf("no second-season harvest in %v", harvests)
	}
	withinDays(t, "drought across the non-growing stretch", drought, int(gapFirst))
}

// farm/blight: blight kills planted cells; the responder cuts them and the
// field replants, so the harvest resumes one growing cycle later and the
// colony loses what the blight took, more the worse it is.
func TestFoodCalibrationFarmBlight(t *testing.T) {
	const grow, blightDay = 6, 10
	always := supplysim.Window{}
	openRice := supplysim.PlannerFunc(func(v supplysim.WorldView) []supplysim.Command {
		return []supplysim.Command{{Kind: supplysim.Open, Source: "rice"}}
	})
	run := func(factor float64) supplysim.Report {
		sc := cropScenario(grow, always)
		if factor > 0 {
			sc.shocks = []supplysim.Shock{{Day: blightDay, Kind: supplysim.DestroyStock, Source: "rice", Factor: factor}}
		}
		return supplysim.Run(sc.world(1), openRice, 40)
	}
	clean, half, full := run(0), run(0.5), run(1)
	if h := harvestDays(clean, "rice"); len(h) < 3 {
		t.Fatalf("clean field harvests %v", h)
	}
	resume := -1
	for _, h := range harvestDays(full, "rice") {
		if h >= blightDay {
			resume = h
			break
		}
	}
	withinDays(t, "harvest resumes after blight", resume, blightDay+grow+1)
	// By the time the blighted field's first replanted harvest is due, the
	// harvest has fallen with the blight.
	by := func(r supplysim.Report) (sum float64) {
		for _, d := range r.Days[:blightDay+grow] {
			sum += d.Delivered["rice"]
		}
		return sum
	}
	if !(by(clean) > by(half) && by(half) > by(full)) {
		t.Fatalf("harvest does not fall with the blight: clean %v half %v full %v", by(clean), by(half), by(full))
	}
}

// trade/routine: a caravan inside its visit window sells nutrition for silver
// when the colony opens trade; nothing sells outside the window or without
// silver, and every purchase draws silver down.
func TestFoodCalibrationTradeRoutine(t *testing.T) {
	const price, restock, nutr = 2.0, 40.0, 1.5
	window := supplysim.Window{Period: 10, From: 3, To: 5}
	build := func(silver float64) supplysim.World {
		src := supplysim.NewTrade("trader", window, restock, nutr, price)
		return supplysim.World{Workers: 2, Stock: map[supplysim.Good]float64{supplysim.Nutrition: 10, supplysim.Silver: silver},
			Consumers: []supplysim.Consumer{{Good: supplysim.Nutrition, PerDay: 2}}, Sources: []supplysim.Source{src}}
	}
	openTrade := supplysim.PlannerFunc(func(v supplysim.WorldView) []supplysim.Command {
		return []supplysim.Command{{Kind: supplysim.Open, Source: "trader"}}
	})
	rep := supplysim.Run(build(500), openTrade, 30)
	var bought float64
	for _, d := range rep.Days {
		if d.Delivered["trader"] > 0 && !window.Active(d.Day) {
			t.Fatalf("trade on day %d, outside the caravan's visit", d.Day)
		}
		bought += d.Delivered["trader"]
	}
	if bought == 0 {
		t.Fatal("nothing bought while the caravan visited")
	}
	withinRate(t, "silver spent", 500-rep.Days[29].Stock[supplysim.Silver], bought*price)
	if rep.Days[4].Stock[supplysim.Nutrition] <= rep.Days[2].Stock[supplysim.Nutrition] {
		t.Fatal("the colony ended the visit with no more food than it began")
	}
	if broke := supplysim.Run(build(0), openTrade, 30); broke.Delivered["trader"] != 0 {
		t.Fatal("bought food with no silver")
	}
	if closed := supplysim.Run(build(500), nil, 30); closed.Delivered["trader"] != 0 {
		t.Fatal("bought food without opening trade")
	}
}

// The recorded plans (#2141 and the ledger, larder and trade snapshots) are the
// seeds: given the rows each recorded, the adapter's real planner opens the
// same kinds of source on day 0.
func TestFoodSeedsOpenWhatSnapshotsOpened(t *testing.T) {
	recorded := map[string]map[policy.FoodChannelKind]bool{}
	for _, sc := range seedScenarios(t) {
		recorded[sc.name] = map[policy.FoodChannelKind]bool{}
	}
	for name, opened := range map[string][]policy.FoodChannelKind{
		"seed/food-ledger-baseline-forage-hunt": {policy.FoodForage, policy.FoodHunt},
		"seed/food-corpse-larder-release":       {policy.FoodForage, policy.FoodHunt},
		"seed/trade-food-bridge-one-day":        {policy.FoodForage},
		"seed/trade-food-crop-surplus-for-meat": {policy.FoodForage},
		"seed/food-starving-tribal-no-hunt-row": nil,
	} {
		for _, k := range opened {
			recorded[name][k] = true
		}
	}
	for _, sc := range seedScenarios(t) {
		res := runFood(sc, 1)
		got := map[policy.FoodChannelKind]bool{}
		for _, id := range res.days[0].opened {
			got[sc.spec(id).kind] = true
		}
		for kind := range recorded[sc.name] {
			if !got[kind] {
				t.Errorf("%s: the recorded plan opened %s, the adapter's planner did not", sc.name, kind)
			}
		}
		if strings.Contains(sc.name, "starving") && len(got) != 0 {
			t.Errorf("%s: the recorded plan opened nothing, the adapter's planner opened %v", sc.name, got)
		}
	}
}
