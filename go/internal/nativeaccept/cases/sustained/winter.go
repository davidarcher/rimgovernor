package sustained

import (
	"context"
	"fmt"
	"math"
	"os"
	"strconv"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// WinterWindowTicks is sustained/winter's sample window in game ticks: half
// a game day, opening six hours before the tile's first non-growing day so
// the reviews straddle the seasonal flip (the harvest gap changes from the
// phased-in coming winter to the wait until growth resumes) and run six
// hours into the non-growing stretch (about 100 ticks/s under peer load
// fits the wall ceiling). WinterWindowTicksEnv overrides it for a longer
// diagnostic (the whole 30-day winter of the baseline tile is 1800000);
// Window() stays the wall-clock ceiling.
const WinterWindowTicks uint64 = 30000

// WinterWindowTicksEnv names the environment variable that overrides
// WinterWindowTicks with a tick count.
const WinterWindowTicksEnv = "RIMGOVERNOR_ACCEPT_WINTER_TICKS"

// WinterWindow is WinterWindowTicksEnv when set and valid, else
// WinterWindowTicks.
func WinterWindow() uint64 {
	if raw := os.Getenv(WinterWindowTicksEnv); raw != "" {
		if n, err := strconv.ParseUint(raw, 10, 64); err == nil && n > 0 {
			return n
		}
	}
	return WinterWindowTicks
}

// winterHoursBeforeFrost is where the fixture parks the calendar: inside
// the growing season with the frost this many hours on, so one day's
// window covers both sides of it.
const winterHoursBeforeFrost = 6

func init() {
	cases.Register(cases.Case{
		Name: "sustained/winter",
		Scope: "Winter survival (#251, the acceptance #229 named): on the " + BaselineSave + " save moved to the last hours of its growing " +
			"season (test/winter_prepare) with a larder stocked to the seasonal food target the calendar derives (test/winter_stock), " +
			"EnsureFoodSupply's families run without a player target and every routine review through the window reads a food " +
			"runway at or above the seasonal FoodMinDays, on both sides of the tile's first non-growing day.",
		Start:  cases.Fixture{Op: "test/winter_prepare", Args: map[string]any{"hoursBeforeFrost": winterHoursBeforeFrost}, On: cases.Save{Name: BaselineSave}},
		Keep:   []string{string(na.NeedFood)},
		Serve:  ptr(Spec("sustained-winter")),
		Budget: Window() + 7*time.Minute,
		Run:    runWinter,
	})
}

// winterSample is one routine review's stored-food reading off the flight
// recorder's routine_review row.
type winterSample struct {
	Revision   uint64  `json:"revision"`
	Tick       int64   `json:"tick"`
	FoodDays   float64 `json:"food_days"`
	Known      bool    `json:"food_days_known"`
	MinDays    float64 `json:"food_min_days"`
	TargetDays float64 `json:"food_target_days"`
	Until      float64 `json:"growing_days_until"`
	Remaining  float64 `json:"growing_days_remaining"`
	NonGrowing float64 `json:"non_growing_days"`
}

func runWinter(ctx context.Context, s cases.Session) error {
	report := s.Report()
	report["fixture"] = s.Prepared()
	_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
		WatchConfig: sustainedfood.WatchConfig{Watch: Window(), Window: WinterWindow(), Poll: 10 * time.Second},
		Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
			return stockWinterLarder(ctx, h, s.Identity(), s.Prepared(), report)
		},
		Audit: func(ctx context.Context, _ *na.Harness, report na.Report) error {
			return auditWinter(s.Config().Output, report)
		},
	})
	return err
}

// stockWinterLarder reads the shifted calendar off the typed colony facts,
// derives the seasonal food policy the reviews will hold the colony to,
// and stocks the larder up to its FoodTargetDays at the colony's own
// nutrition demand: the precondition of a colony that filled its larder
// before the frost, so the window tests the winter and not the summer. The
// census's non-growing stretch must be the fixture's own walk to re-entry,
// and the last growing day's thresholds must already be the winter's
// (#317).
func stockWinterLarder(ctx context.Context, h *na.Harness, identity, prepared map[string]any, report na.Report) error {
	facts, err := h.Wire(ctx, "colony-facts", "observations_read_colony_facts", map[string]any{"scope": map[string]any{"expectedIdentity": identity}})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(facts, "observed")
	if err != nil {
		return err
	}
	climate, ok := na.AsMap(observed["foodClimate"])
	if !ok {
		return fmt.Errorf("colony facts carry no foodClimate")
	}
	sowing, _ := na.AsBool(climate["sowingNow"])
	calendar := policy.Calendar{
		Season: na.AsString(climate["season"]), DayOfYear: int64(na.AsNumber(climate["dayOfYear"])),
		GrowingDays: na.AsNumber(climate["growingDays"]), GrowingDaysRemaining: na.AsNumber(climate["growingDaysRemaining"]),
		GrowingDaysUntil: na.AsNumber(climate["growingDaysUntil"]), NonGrowingDays: na.AsNumber(climate["nonGrowingDays"]), Sowing: sowing,
	}
	report["calendar"] = calendar
	if !calendar.Valid() || calendar.GrowingDaysRemaining != 1 || calendar.GrowingDaysUntil != 0 {
		return fmt.Errorf("fixture calendar is not the last growing day: %+v", calendar)
	}
	nonGrowing := na.AsNumber(prepared["nonGrowingDays"])
	if nonGrowing <= 0 || nonGrowing >= policy.YearDays {
		return fmt.Errorf("fixture reports no non-growing stretch: %v", prepared["nonGrowingDays"])
	}
	if calendar.NonGrowingDays != nonGrowing {
		return fmt.Errorf("the census's non-growing stretch %v is not the fixture's walk to re-entry %v", calendar.NonGrowingDays, nonGrowing)
	}
	// The larder is sized on the thresholds the winter reviews will hold
	// it to: with the frost in, the harvest gap is the wait to re-entry
	// plus a harvest cycle, and the last growing day already phases the
	// same stretch in fully, so the two agree.
	winter := calendar
	winter.GrowingDaysRemaining, winter.GrowingDaysUntil, winter.Sowing = 0, nonGrowing, false
	noConditions := domain.Unknown[[]policy.DisasterCondition]()
	before := policy.DefaultRoutinePolicy().Seasonal(domain.Known(calendar), noConditions)
	seasonal := policy.DefaultRoutinePolicy().Seasonal(domain.Known(winter), noConditions)
	if math.Abs(before.FoodMinDays-seasonal.FoodMinDays) > 1e-9 || math.Abs(before.FoodTargetDays-seasonal.FoodTargetDays) > 1e-9 {
		return fmt.Errorf("seasonal food thresholds jump at the frost: %.2f/%.2f on the last growing day, %.2f/%.2f on the first non-growing day",
			before.FoodMinDays, before.FoodTargetDays, seasonal.FoodMinDays, seasonal.FoodTargetDays)
	}
	// The review's FoodDays is policy.ForecastFood over the combined
	// supply: the colony's pets compete for the same stock, so the larder
	// is sized on every consumer's demand, not the colonists' alone.
	human := na.AsNumber(observed["nutritionPerDay"])
	demand := 0.0
	forecast, _ := na.AsMap(observed["forecast"])
	forecastObserved, _ := na.AsMap(forecast["observed"])
	combined, _ := na.AsMap(forecastObserved["combinedFoodSupply"])
	for _, raw := range na.AsSlice(combined["consumers"]) {
		consumer, _ := na.AsMap(raw)
		demand += na.AsNumber(consumer["nutritionPerDay"])
	}
	if demand < human {
		return fmt.Errorf("the forecast's combined food supply names less demand (%v/day) than the colonists (%v/day)", demand, human)
	}
	if demand <= 0 {
		return fmt.Errorf("colony facts report no nutrition demand: %v", observed["nutritionPerDay"])
	}
	held := na.AsNumber(observed["foodNutrition"])
	nutrition := math.Max(0, seasonal.FoodTargetDays*demand-held)
	report["larder"] = map[string]any{
		"food_min_days": seasonal.FoodMinDays, "food_target_days": seasonal.FoodTargetDays,
		"last_growing_day_food_min_days": before.FoodMinDays, "last_growing_day_food_target_days": before.FoodTargetDays,
		"non_growing_days": nonGrowing, "nutrition_per_day": demand, "colonist_nutrition_per_day": human, "nutrition_held_before": held, "nutrition_stocked": nutrition,
	}
	if nutrition <= 0 {
		return nil
	}
	stocked, err := h.Call(ctx, "winter-stock", "test/winter_stock", map[string]any{"nutrition": math.Ceil(nutrition)})
	if err != nil {
		return err
	}
	if ok, _ := na.AsBool(stocked["success"]); !ok {
		return fmt.Errorf("test/winter_stock refused: %v", stocked["reason"])
	}
	report["larder"].(map[string]any)["stock"] = stocked
	after, err := h.Wire(ctx, "colony-facts-after", "observations_read_colony_facts", map[string]any{"scope": map[string]any{"expectedIdentity": identity}})
	if err != nil {
		return err
	}
	if _, observed, err = na.Outcome(after, "observed"); err != nil {
		return err
	}
	report["larder"].(map[string]any)["runway_days_after"] = observed["foodRunwayDays"]
	return nil
}

// auditWinter reads every routine review the service committed off the
// flight recorder and holds each one's food runway to the seasonal minimum
// it reviewed under; the window must have reached the first non-growing
// day so the seasonal flip is covered.
func auditWinter(output string, report na.Report) error {
	rows, err := bridge.ReadTimeline(na.FlightRecorderPath(output))
	if err != nil {
		return fmt.Errorf("read flight recorder: %w", err)
	}
	var samples []winterSample
	for _, row := range rows {
		if row.Kind != "routine_review" {
			continue
		}
		sample := winterSample{
			Revision: uint64(na.AsNumber(row.Payload["revision"])), Tick: int64(na.AsNumber(row.Payload["tick"])),
			MinDays: na.AsNumber(row.Payload["food_min_days"]), TargetDays: na.AsNumber(row.Payload["food_target_days"]),
			Until: na.AsNumber(row.Payload["growing_days_until"]), Remaining: na.AsNumber(row.Payload["growing_days_remaining"]),
			NonGrowing: na.AsNumber(row.Payload["non_growing_days"]),
		}
		if days, present := row.Payload["food_days"]; present {
			sample.FoodDays, sample.Known = na.AsNumber(days), true
		}
		samples = append(samples, sample)
	}
	report["review_samples"] = samples
	if len(samples) == 0 {
		return fmt.Errorf("the flight recorder holds no routine_review rows")
	}
	known, growing, waiting := 0, 0, 0
	lowest := math.Inf(1)
	var lastGrowing, firstWaiting *winterSample
	for i := range samples {
		sample := samples[i]
		if sample.Until > 0 {
			waiting++
			if firstWaiting == nil {
				firstWaiting = &samples[i]
			}
		} else {
			growing++
			if firstWaiting == nil {
				lastGrowing = &samples[i]
			}
		}
		if !sample.Known {
			continue
		}
		known++
		lowest = math.Min(lowest, sample.FoodDays-sample.MinDays)
		if sample.FoodDays < sample.MinDays {
			return fmt.Errorf("review %d at tick %d read food days %.2f under the seasonal minimum %.2f (target %.2f, growing days until %v)",
				sample.Revision, sample.Tick, sample.FoodDays, sample.MinDays, sample.TargetDays, sample.Until)
		}
	}
	report["review_summary"] = map[string]any{"reviews": len(samples), "food_days_known": known, "growing": growing, "waiting": waiting, "lowest_margin_days": lowest}
	if known == 0 {
		return fmt.Errorf("no routine review read a food runway across %d reviews", len(samples))
	}
	if growing == 0 || waiting == 0 {
		return fmt.Errorf("the reviews did not straddle the first non-growing day (growing %d, waiting %d over %d reviews; window %v)", growing, waiting, len(samples), report["window"])
	}
	// The thresholds do not jump at the flip: the last growing review and
	// the first waiting one hold the colony to the same figures (#317).
	if lastGrowing != nil && firstWaiting != nil {
		report["review_flip"] = map[string]any{"last_growing": *lastGrowing, "first_waiting": *firstWaiting}
		if math.Abs(lastGrowing.MinDays-firstWaiting.MinDays) > 1e-6 || math.Abs(lastGrowing.TargetDays-firstWaiting.TargetDays) > 1e-6 {
			return fmt.Errorf("seasonal thresholds jumped at the frost: review %d held %.2f/%.2f, review %d %.2f/%.2f",
				lastGrowing.Revision, lastGrowing.MinDays, lastGrowing.TargetDays, firstWaiting.Revision, firstWaiting.MinDays, firstWaiting.TargetDays)
		}
	}
	return nil
}
