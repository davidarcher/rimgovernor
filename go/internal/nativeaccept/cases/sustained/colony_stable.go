package sustained

import (
	"context"
	"errors"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Fixed equivalent of RIMGOVERNOR_ACCEPT_WINDOW_TICKS=900000: a shorter
// diagnostic environment override must not shorten the nightly gate.
const colonyStableTicks uint64 = 900000

func init() {
	c := colony("sustained/colony-stable", na.QuietRequired,
		"Fifteen game days on tribal8 with every routine family and live needs: no colonist lost or dead, no Malnutrition above 0.3, every immunity disease immune or tended, and food days at least FoodTargetDays.")
	// At 100 ticks/s the window takes 2.5 hours. The service still enforces
	// its 90-second step stall bound; this ceiling allows the full calendar.
	const watch = 3 * time.Hour
	c.Budget = watch + 7*time.Minute
	c.Reason = "nightly fifteen-day colony stability gate with live needs and every routine family"
	c.Run = func(ctx context.Context, s cases.Session) error {
		var initial []string
		_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
			WatchConfig: sustainedfood.WatchConfig{
				Watch: watch, Window: colonyStableTicks, Poll: 10 * time.Second,
				Concern: policy.EnsureFoodSupply, Extra: ColonyConcerns,
				FailFast: sustainedfood.FailFast{Disabled: true},
			},
			Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
				listed, err := na.ListColonists(ctx, h, "stable-initial", false, false)
				if err != nil {
					return err
				}
				for _, row := range listed {
					id := na.RowID(row)
					if id == "" {
						return fmt.Errorf("initial colonist has no id")
					}
					initial = append(initial, id)
				}
				report["initial_colonists"] = initial
				if len(initial) == 0 {
					return fmt.Errorf("no initial colonists observed")
				}
				return nil
			},
			Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
				var failures []error
				window, ok := report["window"].(*sustainedfood.TickWindow)
				if !ok || !window.Reached {
					failures = append(failures, fmt.Errorf("fifteen-day window not reached: %v", report["window"]))
				}
				failures = append(failures, auditReacquisitions(ctx, h, report), AuditNutrition(ctx, h, report))
				listed, err := na.ListColonists(ctx, h, "stable-health", true, true)
				if err != nil {
					return errors.Join(append(failures, err)...)
				}
				report["stable_health"] = listed
				alive := map[string]bool{}
				for _, row := range listed {
					id := na.RowID(row)
					dead, known := na.AsBool(row["dead"])
					alive[id] = known && !dead
					if !alive[id] {
						failures = append(failures, fmt.Errorf("colonist %s dead or death status unknown", id))
					}
					health, ok := na.AsMap(row["health"])
					if !ok {
						failures = append(failures, fmt.Errorf("colonist %s health missing", id))
						continue
					}
					for _, raw := range na.AsSlice(health["hediffs"]) {
						disease, _ := na.AsMap(raw)
						immunizable, _ := na.AsBool(disease["immunizable"])
						immune, _ := na.AsBool(disease["fullyImmune"])
						tended, _ := na.AsBool(disease["tended"])
						if immunizable && !immune && !tended {
							failures = append(failures, fmt.Errorf("colonist %s has untreated nonimmune %s", id, na.HediffDef(disease)))
						}
					}
				}
				// Corpses can despawn or be buried: a missing initial pawn must
				// fail even when the pawn read no longer returns the dead pawn.
				for _, id := range initial {
					if !alive[id] {
						failures = append(failures, fmt.Errorf("initial colonist %s no longer alive on map", id))
					}
				}
				failures = append(failures, auditStableFood(report))
				if err := errors.Join(failures...); err != nil {
					return fmt.Errorf("%w; %s", err, stableTimeline(report))
				}
				return nil
			},
		})
		return err
	}
	cases.Register(c)
}

// The census locates observed losses and the food trough, not the onset of
// individual hediffs. Days are one-based relative to the observation window.
func stableTimeline(report na.Report) string {
	timeline, _ := report["timeline"].([]map[string]any)
	window, _ := report["window"].(*sustainedfood.TickWindow)
	if window == nil || !window.Sampled {
		return "timeline day unavailable (no sampled game tick)"
	}
	day := func(tick uint64) uint64 { return (tick-window.FirstTick)/60000 + 1 }
	detail := "timeline: no sampled colonist-count loss"
	var previous float64
	var known bool
	for _, sample := range timeline {
		census, ok := na.AsMap(sample["colony"])
		if !ok || census["error"] != nil || census["colonists"] == nil {
			continue
		}
		count := na.AsNumber(census["colonists"])
		tick, timed := sample["tick"].(uint64)
		if known && count < previous && timed && tick >= window.FirstTick {
			detail = fmt.Sprintf("timeline: first sampled colonist-count loss on day %d (tick %d, %g -> %g)", day(tick), tick, previous, count)
			break
		}
		previous, known = count, true
	}
	outcome := sustainedfood.DeriveColonyOutcome(timeline)
	if outcome.MinFoodRunwayTick != nil && *outcome.MinFoodRunwayTick >= window.FirstTick {
		detail += fmt.Sprintf("; minimum sampled food runway %g days on day %d (tick %d)", *outcome.MinFoodRunwayDays, day(*outcome.MinFoodRunwayTick), *outcome.MinFoodRunwayTick)
	}
	return detail
}

// stableFoodDays is the runway an eight-person tribal colony must average over
// the last stableFoodSpanTicks: the runway swings as the colony eats and
// restocks, so one end-of-run read is not the gate.
const (
	stableFoodDays      = 14.0
	stableFoodSpanTicks = 5 * 60000
)

func auditStableFood(report na.Report) error {
	timeline, _ := report["timeline"].([]map[string]any)
	mean, samples, sampled := sustainedfood.MeanFoodRunway(timeline, stableFoodSpanTicks)
	report["stable_food"] = map[string]any{"mean_food_runway_days": mean, "mean_samples": samples, "required_days": stableFoodDays}
	if !sampled || mean < stableFoodDays {
		return fmt.Errorf("mean food runway %.2f days over the last %d days (%d samples) below %g days", mean, stableFoodSpanTicks/60000, samples, stableFoodDays)
	}
	return nil
}
