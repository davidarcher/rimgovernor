package farm

import (
	"context"
	"fmt"
	"math"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// farm/calendar reads the typed colony facts' growing calendar (#229) off
// the baseline save and holds it to the game's own readouts: the season
// and day of year the legacy home/status time block prints, the growing
// period home/world reads off the tile, and the two seasonal-temperature
// walks (days remaining, days until) agreeing with each other. The report
// records the seasonal food and wood thresholds a review would derive from
// it. Read-only: no game orders and no clock advance.
func init() {
	cases.Register(cases.Case{
		Name: "farm/calendar",
		Scope: "The typed colony read's growing calendar (season, day of year, growing period, days until the crop range is left " +
			"and re-entered) agrees with home/status and home/world on the " + sustained.BaselineSave + " save; read-only (#229).",
		Start:  cases.Save{Name: sustained.BaselineSave},
		Quiet:  na.QuietRequired,
		Budget: 5 * time.Minute,
		Run:    runCalendar,
	})
}

func runCalendar(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h := s.Harness()
	identityReply, err := h.Wire(ctx, "identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, loaded, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return err
	}
	loadedContext, _ := loaded["context"].(map[string]any)
	identity, _ := loadedContext["identity"].(map[string]any)
	facts, err := h.Wire(ctx, "colony-facts", "observations_read_colony_facts", map[string]any{"scope": map[string]any{"expectedIdentity": identity}})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(facts, "observed")
	if err != nil {
		return err
	}
	for _, raw := range na.AsSlice(observed["issues"]) {
		issue, _ := na.AsMap(raw)
		if na.AsString(issue["field"]) == "food_climate" {
			return fmt.Errorf("food_climate unavailable: %v", issue)
		}
	}
	climate, ok := na.AsMap(observed["foodClimate"])
	if !ok {
		return fmt.Errorf("colony facts carry no foodClimate")
	}
	for _, field := range []string{"growingDays", "growingDaysRemaining", "growingDaysUntil", "sowingNow", "season", "dayOfYear"} {
		if _, present := climate[field]; !present {
			return fmt.Errorf("foodClimate lacks %s: %v", field, climate)
		}
	}
	sowing, _ := na.AsBool(climate["sowingNow"])
	calendar := policy.Calendar{
		Season: na.AsString(climate["season"]), DayOfYear: int64(na.AsNumber(climate["dayOfYear"])),
		GrowingDays: na.AsNumber(climate["growingDays"]), GrowingDaysRemaining: na.AsNumber(climate["growingDaysRemaining"]),
		GrowingDaysUntil: na.AsNumber(climate["growingDaysUntil"]), Sowing: sowing,
	}
	report["calendar"] = calendar
	if !calendar.Valid() {
		return fmt.Errorf("calendar out of range: %+v", calendar)
	}
	// The two walks sample the same daily seasonal temperature: exactly one
	// of them is zero unless the tile never leaves (60/0) or never enters
	// (0/60) the crop range.
	remaining, until := calendar.GrowingDaysRemaining, calendar.GrowingDaysUntil
	switch {
	case remaining == policy.YearDays && until == 0, remaining == 0 && until == policy.YearDays:
	case remaining > 0 && until == 0, remaining == 0 && until > 0:
	default:
		return fmt.Errorf("growing walks disagree: remaining=%v until=%v", remaining, until)
	}

	status, err := h.Call(ctx, "status", "home/status", map[string]any{"colonists": false, "threats": false})
	if err != nil {
		return err
	}
	clock, _ := na.AsMap(status["time"])
	if season := na.AsString(clock["season"]); season != calendar.Season {
		return fmt.Errorf("season %q disagrees with home/status %q", calendar.Season, season)
	}
	if day := na.AsNumber(clock["dayOfYear"]); day != float64(calendar.DayOfYear) {
		return fmt.Errorf("day of year %d disagrees with home/status %v", calendar.DayOfYear, day)
	}
	world, err := h.Call(ctx, "world", "home/world", map[string]any{})
	if err != nil {
		return err
	}
	if period := na.AsNumber(world["growingPeriodDays"]); period != calendar.GrowingDays {
		return fmt.Errorf("growing period %v disagrees with home/world %v", calendar.GrowingDays, period)
	}
	report["biome"] = world["biomeDefName"]
	report["growing_period_label"] = world["growingPeriodLabel"]

	gap, _ := policy.HarvestGapDays(domain.Known(calendar)).Value()
	seasonal := policy.DefaultRoutinePolicy().Seasonal(domain.Known(calendar))
	report["harvest_gap_days"] = math.Round(gap*100) / 100
	report["seasonal_thresholds"] = map[string]any{
		"food_min_days": seasonal.FoodMinDays, "food_target_days": seasonal.FoodTargetDays,
		"wood_min": seasonal.WoodMin, "wood_target": seasonal.WoodTarget, "wood_max": seasonal.WoodMax,
	}
	if err := seasonal.Validate(); err != nil {
		return fmt.Errorf("seasonal policy: %w", err)
	}
	return nil
}
