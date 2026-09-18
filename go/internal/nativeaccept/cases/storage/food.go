// The storage/food case verifies, against a naturally generated debug-ready
// colony (no fixture spawning, no construction or production orders), that
// native code actually populates FoodStock.roofed for observed food stock.
// policy.ReviewFoodStorage/buildingruntime.RoutineFoodStorageUpkeepPlanner
// (MaintainFoodStorage, issue #2 B04h phase 4) derive their entire
// stored-vs-unstored split from this one previously-unread fact -- no
// existing harness exercised it before this vertical was added, so a passing
// Go unit test suite alone cannot show the native side actually sets it.
//
// rimworld/start_debug_game_ready hands back a randomly generated biome each
// call, and some biomes (observed: Desert) have no reachable food source at
// all -- no huntable animals, no wild plants with food:true in the
// acquisition list -- so colonists never produce a single FoodStock row no
// matter how long simulated time runs. The repo's standing
// RimGovernor-tribal8-baseline save was tried as an alternative but is
// itself a deliberately-captured zero-food-runway crisis snapshot (see
// sustainedfoodaccept's own doc comment), so it never has stock either.
// Regenerating a second debug game within the same GABS session to reroll a
// bad biome also proved unreliable (observed: start_debug_game_ready itself
// timing out on the second call after several minutes of Superfast
// simulation), so this harness makes one attempt per process and fails fast
// with a distinct message when the rolled biome has no food source, rather
// than polling pointlessly or regenerating in-process; an unlucky run is
// simply rerun as a fresh process.
package storage

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name:   "storage/food",
		Scope:  "Naturally generated debug-ready colony (one attempt per process; a food-poor biome fails fast rather than retries); read-only colony-facts food-supply census, no fixture spawning or construction/production orders. Confirms FoodStock.roofed is populated by native code.",
		Start:  cases.DebugStart{},
		Quiet:  na.QuietIfAvailable,
		Reason: "asserts a production discovery (no test/ tools), so the quiet fixture cannot be present",
		Keep:   []string{string(na.LiveNeeds)},
		Budget: 5 * time.Minute,
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	// Repeatedly regenerating a fresh debug game within a single GABS session
	// to reroll a bad biome proved unreliable in practice (observed:
	// rimworld/start_debug_game_ready itself timing out on a second call
	// after several minutes of Superfast simulation), so this harness makes
	// exactly one attempt per process. A biome with no food-yielding
	// acquisition source fails fast with a distinct message instead of
	// polling pointlessly for minutes; callers unlucky enough to roll one
	// simply rerun the whole harness (a fresh process, fresh GABS session).
	// Needs stay live: this is a production build with no fixture exports.
	report := s.Report()
	h := s.Harness()
	names := s.Names()
	if !na.Contains(names, "rimgovernor/observations_read_colony_facts") {
		return fmt.Errorf("missing rimgovernor/observations_read_colony_facts in discovery")
	}
	for _, name := range names {
		if len(name) >= 5 && name[:5] == "test/" {
			return fmt.Errorf("unexpected fixture export %s in production food-storage discovery", name)
		}
	}
	readFoodSupply := func(identity map[string]any, label string) (map[string]any, []any, error) {
		reply, err := h.Wire(ctx, label, "observations_read_colony_facts", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "planning": false, "page": map[string]any{"limit": 256},
		})
		if err != nil {
			return nil, nil, err
		}
		_, observedSnapshot, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, nil, err
		}
		foodSupplySection, _ := na.AsMap(observedSnapshot["foodSupply"])
		_, foodObserved, err := na.Outcome(foodSupplySection, "observed")
		if err != nil {
			return nil, nil, fmt.Errorf("%s: food supply section unavailable: %w", label, err)
		}
		return observedSnapshot, na.AsSlice(foodObserved["stocks"]), nil
	}

	// hasFoodSource reports whether the freshly generated map has any real
	// food-yielding acquisition entry (wild plant with food:true, or a
	// huntable animal) -- a biome without one (observed: Desert, wood-only
	// cacti/trees) will never produce a FoodStock row, so it is cheaper to
	// discard it and start a new debug game than to poll it for minutes.
	hasFoodSource := func(observedSnapshot map[string]any) bool {
		for _, raw := range na.AsSlice(observedSnapshot["acquisition"]) {
			row, _ := na.AsMap(raw)
			if food, _ := row["food"].(bool); food {
				return true
			}
			if hunt, _ := row["hunt"].(bool); hunt {
				return true
			}
		}
		return false
	}

	identityBefore, err := h.Wire(ctx, "identity-before", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, before, err := na.Outcome(identityBefore, "loaded")
	if err != nil {
		return err
	}
	if paused, _ := na.AsBool(before["paused"]); !paused {
		return fmt.Errorf("fresh debug game did not start paused")
	}
	beforeContext, _ := before["context"].(map[string]any)
	identity, _ := beforeContext["identity"].(map[string]any)

	observedSnapshot, stocks, err := readFoodSupply(identity, "colony-facts-initial")
	if err != nil {
		return err
	}
	report["biome"] = observedSnapshot["biome"]
	if !hasFoodSource(observedSnapshot) {
		return fmt.Errorf("this debug game's biome (%v) has no food-yielding acquisition source; rerun the harness to roll a new one", observedSnapshot["biome"])
	}
	if len(stocks) == 0 {
		// Both ultraSpeedBoost and plain Superfast eventually crash the
		// headless process (native tool catalog goes empty entirely,
		// HeadlessPlayer.log stops mid-line with no shutdown message) at
		// roughly the same *wall-clock* elapsed time regardless of how many
		// simulated ticks that covers -- e.g. one plain-Superfast run only
		// reached tick ~128000 (~2 sim-days) by the time it crashed at the
		// same ~8 minute mark an ultraSpeedBoost run did. That points to a
		// wall-clock-driven instability (resource leak, watchdog, GC), not
		// something tied to simulated time, so there is no speed setting that
		// avoids it -- only staying within a safe real-time budget does.
		// Given that ceiling, the wait is stated in game time (RunUntil at
		// the boosted run speed maximizes simulated progress, and thus the
		// chance colonists get hungry enough to create a real FoodStock row)
		// with a wall-clock ceiling well under the crash mark as the safety
		// net, and the stall budget for a game that stops ticking.
		//
		// A read can transiently fail mid-poll -- tolerate a handful of
		// consecutive failures rather than aborting on one hiccup.
		const maxConsecutiveFailures = 4
		consecutiveFailures := 0
		polls := 0
		ticks, err := na.RunUntil(ctx, h, "harvest", 4*na.TicksPerDay, na.Wait{Stall: na.StallBudget(), Ceiling: 4 * time.Minute, Interval: 5 * time.Second}, func(ctx context.Context) (string, bool, error) {
			polls++
			var pollErr error
			_, stocks, pollErr = readFoodSupply(identity, fmt.Sprintf("colony-facts-poll-%d", polls))
			if pollErr != nil {
				consecutiveFailures++
				report["poll_error_last"] = pollErr.Error()
				if consecutiveFailures > maxConsecutiveFailures {
					return "", false, fmt.Errorf("colony-facts read failed %d times in a row while polling: %w", consecutiveFailures, pollErr)
				}
				return na.Signature("read-failed", consecutiveFailures), false, nil
			}
			consecutiveFailures = 0
			return na.Signature(len(stocks)), len(stocks) > 0, nil
		})
		report["harvest_ticks"] = ticks
		report["harvest_polls"] = polls
		if err != nil {
			return fmt.Errorf("no observed food stock after waiting for colonists to harvest/haul; cannot confirm roofed presence: %w", err)
		}
	}
	if len(stocks) == 0 {
		return fmt.Errorf("no observed food stock after waiting for colonists to harvest/haul; cannot confirm roofed presence")
	}
	// FoodSupplyFacts.completeness tracks the consumers page, not stocks
	// (stocks carries no page of its own in this reply shape), so it is not
	// checked against len(stocks) here.
	roofedTrue, roofedFalse, perishable := 0, 0, 0
	for i, raw := range stocks {
		row, _ := na.AsMap(raw)
		value, present := row["roofed"]
		if !present {
			return fmt.Errorf("food stock row %d missing roofed field entirely", i)
		}
		roofed, ok := value.(bool)
		if !ok {
			return fmt.Errorf("food stock row %d roofed field is not a bool: %#v", i, value)
		}
		if roofed {
			roofedTrue++
		} else {
			roofedFalse++
		}
		if p, ok := row["perishable"].(bool); ok && p {
			perishable++
		}
	}
	report["stock_count"] = len(stocks)
	report["roofed_true"] = roofedTrue
	report["roofed_false"] = roofedFalse
	report["perishable_count"] = perishable
	return nil
}
