package food

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// starvingRecoveryDays is the day budget: the colony is sampled each
// game day and the case fails when the chain below has not completed by the
// last. starvingRecoveryMalnutrition is the severity the recovered colony
// must be at or under; starvingHunger the food level the colonists start at,
// low enough that malnutrition really builds before the first meat.
const (
	starvingRecoveryDays         = 8
	starvingRecoveryMalnutrition = 0.3
	starvingHunger               = 0.05
)

func init() {
	cases.Register(cases.Case{Name: "food/starving-tribal-recovery",
		Scope: "Native postconditions and end-to-end signal (#2173; a Go snapshot test cannot cover the vanilla physics): the starving tribal colony of food/starving-tribal (tribal8, no food, no weapons, no butcher bill, six wild deer, Core only) under the autonomous controller " +
			"crafts a bow that a colonist wields, designates and kills prey, the killer's job after a kill is Hunt on the next live designated prey and not a haul of its own corpse, " +
			"the meat is butchered (delivery ledger) and eaten (native ingest counter), malnutrition has recovered to 0.3 or below and the food runway is positive, all within eight game days.",
		Start: EmptyChannels("MealSurvivalPack", 0), RequiredOps: []string{starvingPrepareOp, starvingObserveOp}, Keep: []string{"Food"}, NoCheckpoint: true,
		Service: true, Budget: 20 * time.Minute, Crew: cases.Crew{Size: 3}, Stall: 2 * time.Minute,
		Reason: "up to eight native days of a bow craft, hunts, butchering and recovery from malnutrition, like food/fishing's fifteen; the start is already the starving fixture",
		Run:    runStarvingRecovery})
}

func runStarvingRecovery(ctx context.Context, s cases.Session) error {
	report := s.Report()
	if err := prepareStarving(ctx, s, map[string]any{"foodLevel": starvingHunger}, false); err != nil {
		return err
	}
	var peak float64
	var chained, armed bool
	samples := []any{}
	var missing []string
	err := starvingServe(ctx, s, nil, 60000, starvingRecoveryDays, func(r starvingRound) (bool, error) {
		o := r.observed
		peak = max(peak, na.AsNumber(o["malnutrition"]))
		for _, raw := range na.AsSlice(o["colonists"]) {
			c, _ := na.AsMap(raw)
			// Every weapon was destroyed at the start: a ranged one in hand was crafted.
			if ranged, _ := na.AsBool(c["ranged"]); ranged {
				armed = true
			}
		}
		for _, raw := range na.AsSlice(o["killJobs"]) {
			k, _ := na.AsMap(raw)
			if live, _ := na.AsBool(k["targetLive"]); live && k["jobDef"] == "Hunt" {
				if designated, _ := na.AsBool(k["targetDesignated"]); designated {
					chained = true
				}
			}
		}
		butchered := 0.0
		if ledger, e := readLedger(ctx, s, r.prefix+"-ledger"); e == nil {
			butchered = na.AsNumber(ledger.raw["butchersTotal"])
		} else {
			report[r.prefix+"_ledger_error"] = e.Error()
		}
		runway := na.AsNumber(r.colony["foodRunwayDays"])
		sample := map[string]any{"round": r.n, "clockParked": r.parked, "malnutrition": o["malnutrition"], "peakMalnutrition": peak,
			"bowWielded": armed, "wildKills": o["wildKills"], "huntChain": chained, "butchers": butchered, "meatEaten": o["meatEaten"],
			"foodRunwayDays": r.colony["foodRunwayDays"], "pendingHunts": o["pendingHunts"], "killJobs": o["killJobs"]}
		samples = append(samples, sample)
		report["samples"] = samples
		report["latest"] = sample
		missing = missing[:0]
		for _, check := range []struct {
			name string
			ok   bool
		}{
			{"a bow was crafted and wielded", armed},
			{"prey was killed", na.AsNumber(o["wildKills"]) > 0},
			{"the killer's next job was Hunt on live designated prey", chained},
			{"meat was butchered", butchered > 0},
			{"meat was eaten", na.AsNumber(o["meatEaten"]) > 0},
			{fmt.Sprintf("malnutrition is at most %v", starvingRecoveryMalnutrition), na.AsNumber(o["malnutrition"]) <= starvingRecoveryMalnutrition},
			{"the food runway is positive", runway > 0},
		} {
			if !check.ok {
				missing = append(missing, check.name)
			}
		}
		return len(missing) == 0, nil
	})
	if err != nil {
		return err
	}
	if len(missing) > 0 {
		return fmt.Errorf("the starving colony did not recover within %d days; unmet: %v", starvingRecoveryDays, missing)
	}
	return nil
}
