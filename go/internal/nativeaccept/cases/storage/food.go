// The storage/food case verifies, against a naturally generated debug-ready
// colony (no food spawning, no construction or production orders; one wild
// harvest designated through the ordinary player designator), that native
// code actually populates FoodStock.roofed for observed food stock.
// policy.ReviewFoodStorage/buildingruntime.RoutineFoodStorageUpkeepPlanner
// (MaintainFoodStorage, issue #2 B04h phase 4) derive their entire
// stored-vs-unstored split from this one previously-unread fact -- no
// existing harness exercised it before this vertical was added, so a passing
// Go unit test suite alone cannot show the native side actually sets it.
//
// The debug quick start rolls a random biome, and some (observed: Desert,
// Tundra) have no reachable food source at all -- no huntable animals, no
// wild plants with food:true in the acquisition list -- so colonists never
// produce a single FoodStock row no matter how long simulated time runs,
// and others (observed: BorealForest) have one so sparse that four days of
// simulation still observed nothing. Under the cached start (issue #91) the
// roll is made once per root, so "rerun to roll again" was dead advice
// (issue #172). The case therefore pins its start to a berry-rich biome
// through the debug start fixture's biome preference (the cached save is
// keyed on it), which makes it a fixture-build case like the rest of the
// registry rather than the one case that needed a production build. The
// repo's standing RimGovernor-tribal8-baseline save was tried as an
// alternative but is itself a deliberately-captured zero-food-runway crisis
// snapshot (see sustainedfoodaccept's own doc comment), so it never has
// stock either. The biome check stays as a guard: a planet that offers none
// of the preferred biomes fails the start itself, so reaching a foodless map
// here means the preference is wrong, not the roll.
//
// A food-bearing biome alone is still not enough: the quick start places no
// food items, and hungry colonists eat berries straight off the bush (the
// map's food:true acquisition rows were all gone after two and a half days
// on a TemperateForest run) without ever spawning an item, so left alone
// the colony never produces a FoodStock row. The case therefore designates
// the nearest wild food plants for harvest through home/acquire_resource
// (the same player designator hutaccept uses for trees) and waits for the
// harvested stack to be observed; the assertion is still on what native
// reads back, not on anything spawned.
package storage

import (
	"context"
	"fmt"
	"sort"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// FoodBiomes is the start's biome preference: biomes whose wild plants
// carry food (berry bushes), most likely first, so colonists harvest and
// haul a real FoodStock row within the wait.
const FoodBiomes = "TemperateForest,TropicalRainforest,TemperateSwamp,TropicalSwamp"

func init() {
	cases.Register(cases.Case{
		Name:   "storage/food",
		Scope:  "Naturally generated debug-ready colony pinned to a berry-rich biome (" + FoodBiomes + "); the nearest wild food plants designated for harvest through the player designator, then a read-only colony-facts food-supply census; no food spawning or construction/production orders. Confirms FoodStock.roofed is populated by native code.",
		Start:  cases.DebugStart{Size: na.DebugStart{Biomes: FoodBiomes}},
		Quiet:  na.QuietRequired,
		Keep:   []string{string(na.LiveNeeds)},
		Budget: 5 * time.Minute,
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	// Needs stay live: hunger is what sends colonists to the berry bushes.
	report := s.Report()
	h := s.Harness()
	if !na.Contains(s.Names(), "rimgovernor/observations_read_colony_facts") {
		return fmt.Errorf("missing rimgovernor/observations_read_colony_facts in discovery")
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

	// hasFoodSource reports whether the generated map has any real
	// food-yielding acquisition entry (wild plant with food:true, or a
	// huntable animal) -- a map without one (observed: Desert, wood-only
	// cacti/trees) will never produce a FoodStock row, so it is cheaper to
	// fail here than to poll it for minutes.
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
		return fmt.Errorf("this debug game's biome (%v) has no food-yielding acquisition source although the start asked for one of %s; delete the root's cached %s save and fix the preference", observedSnapshot["biome"], FoodBiomes, "RimGovernor-debug-*-"+strings.ToLower(strings.ReplaceAll(FoodBiomes, ",", "-")))
	}
	if len(stocks) == 0 {
		if err := designateWildFood(ctx, h, identity, observedSnapshot, report); err != nil {
			return err
		}
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
			return fmt.Errorf("no observed food stock after waiting for colonists to harvest the designated plants; cannot confirm roofed presence: %w", err)
		}
	}
	if len(stocks) == 0 {
		return fmt.Errorf("no observed food stock after waiting for colonists to harvest the designated plants; cannot confirm roofed presence")
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

// wildFoodDesignations is how many food-yielding wild plants the case
// designates: enough that one is still standing when a colonist gets to it
// (hungry colonists eat the nearest bushes bare), few enough to stay one
// afternoon's plant-cutting work.
const wildFoodDesignations = 3

// designateWildFood designates the nearest undesignated food-yielding wild
// plants (acquisition rows with food:true) for harvest through
// home/acquire_resource, the ordinary player designator, so a real
// harvested stack appears for the census to observe.
func designateWildFood(ctx context.Context, h *na.Harness, identity, facts map[string]any, report na.Report) error {
	center, _ := na.AsMap(facts["center"])
	cx, cz := na.AsNumber(center["x"]), na.AsNumber(center["z"])
	// Typed acquisition rows carry the plant under source: its id and
	// position; resource and yield sit on the row.
	type plant struct {
		id, resource string
		x, z         int
		yield        any
		distance     float64
	}
	var plants []plant
	for _, raw := range na.AsSlice(facts["acquisition"]) {
		row, _ := na.AsMap(raw)
		if food, _ := row["food"].(bool); !food {
			continue
		}
		if designated, _ := row["designated"].(bool); designated {
			continue
		}
		source, _ := na.AsMap(row["source"])
		position, _ := na.AsMap(source["position"])
		x, z := na.AsNumber(position["x"]), na.AsNumber(position["z"])
		plants = append(plants, plant{id: na.AsString(source["id"]), resource: na.AsString(row["resource"]),
			x: int(x), z: int(z), yield: row["yield"], distance: (x-cx)*(x-cx) + (z-cz)*(z-cz)})
	}
	sort.Slice(plants, func(i, j int) bool {
		if plants[i].distance != plants[j].distance {
			return plants[i].distance < plants[j].distance
		}
		return plants[i].id < plants[j].id
	})
	var designated []map[string]any
	var refused []map[string]any
	for _, p := range plants {
		if len(designated) >= wildFoodDesignations {
			break
		}
		result, err := h.Call(ctx, "designate-wild-food", "home/acquire_resource", map[string]any{
			"colonyId": identity["colonyId"], "loadToken": identity["loadToken"], "mapId": identity["mapId"],
			"thingId": p.id, "resource": p.resource, "x": p.x, "z": p.z, "dryRun": false,
		})
		if err != nil {
			return err
		}
		if ok, _ := na.AsBool(result["success"]); !ok {
			refused = append(refused, result)
			continue
		}
		designated = append(designated, map[string]any{"id": p.id, "resource": p.resource, "x": p.x, "z": p.z, "yield": p.yield})
	}
	report["designated_wild_food"] = designated
	if len(refused) > 0 {
		report["designate_wild_food_refused"] = refused
	}
	if len(designated) == 0 {
		return fmt.Errorf("no wild food plant could be designated for harvest from %d food-yielding acquisition rows", len(plants))
	}
	return nil
}
