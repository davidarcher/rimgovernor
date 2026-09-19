package food

import (
	"context"
	"fmt"
	"math"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func init() {
	cases.Register(cases.Case{Name: "food/fishing", Scope: "Odyssey: the live ledger opens fishing at min(regeneration nutrition, pawn capacity); the shared food goal creates a reachable zone on that water body at a 60% population floor, and two colonists feed on fishing for 15 days without soil.",
		Start:      cases.Fixture{On: cases.DebugStart{Size: na.DebugStart{MapSize: 150, PlanetCoverage: 0.05, Biomes: "TropicalRainforest", Seed: "fishing-426"}}, Op: "test/fishing_prepare"},
		Expansions: []string{"ludeon.rimworld.odyssey"}, NoKeep: true, NoCheckpoint: true, Service: true, Keep: []string{"Food", "Rest"},
		RequiredOps: []string{"test/fishing_observe"}, Budget: 20 * time.Minute, Reason: "15 native days of food consumption and water-body recovery; a programmatic no-soil coast starts ready to fish", Run: runFishing})
}

func runFishing(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	reply, err := h.Wire(ctx, "fishing-before", "observations_read_colony_facts", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}, "planning": true})
	if err != nil {
		return err
	}
	_, facts, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	section, _ := na.AsMap(facts["foodChannels"])
	observed, _ := na.AsMap(section["observed"])
	water, _ := na.AsMap(observed["fishableWater"])
	regions := na.AsSlice(water["regions"])
	if len(regions) != 1 || water["fishingResearched"] != true {
		return fmt.Errorf("missing Odyssey fishing census: %v", water)
	}
	region, _ := na.AsMap(regions[0])
	root, _ := na.AsMap(region["root"])
	id := policy.FishingRegionID(domain.Cell{X: int32(na.AsNumber(root["x"])), Z: int32(na.AsNumber(root["z"]))})
	rate := math.Min(0.025*na.AsNumber(region["maxPopulation"])*na.AsNumber(region["nutritionPerFish"]), na.AsNumber(region["pawnFishWorkCapacity"]))
	if rate <= 0 || len(na.AsSlice(region["proposedCells"])) < int(na.AsNumber(region["concurrentFishers"])) {
		return fmt.Errorf("unusable fishing capacity: %v", region)
	}
	s.Report()["fishing_region"] = region
	// Review without the field writer first, so Open is observed before the
	// normal next review changes an already-delivering source to Hold.
	service, err := s.Launch(ctx, na.ServiceLaunch{Families: []string{"work"}, Extra: na.ClockSpeedArgs()})
	if err != nil {
		return err
	}
	defer service.Stop()
	token, err := service.SessionToken()
	if err != nil {
		return err
	}
	if _, err = service.WaitAttached(s.Identity(), 90*time.Second); err != nil {
		return err
	}
	if _, err = service.Resume("fishing-review", s.Identity(), token, s.Report()); err != nil {
		return err
	}
	poll := time.NewTicker(250 * time.Millisecond)
	defer poll.Stop()
	deadline := time.NewTimer(90 * time.Second)
	defer deadline.Stop()
	found := false
	for !found {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-deadline.C:
			return fmt.Errorf("live food plan did not open fishing")
		case <-poll.C:
		}
		colony, err := service.Get("GET", "/api/player/colony")
		if err != nil {
			return err
		}
		plan, _ := na.AsMap(colony["foodPlan"])
		for _, raw := range na.AsSlice(plan["portfolio"]) {
			row, _ := na.AsMap(raw)
			if row["kind"] == "Fishing" && row["id"] == id && row["decision"] == "Open" {
				if math.Abs(na.AsNumber(row["nutritionPerDay"])-rate) > 1e-5 {
					return fmt.Errorf("ledger rate is not regeneration/capacity derived: %v want %v", row, rate)
				}
				s.Report()["fishing_plan_open"] = row
				found = true
			}
		}
	}
	service.Stop()
	if _, err = s.Reattach(ctx); err != nil {
		return err
	}
	service, err = s.Launch(ctx, na.ServiceLaunch{Families: []string{"field", "work", "research"}, Extra: na.ClockSpeedArgs()})
	if err != nil {
		return err
	}
	defer service.Stop()
	token, err = service.SessionToken()
	if err != nil {
		return err
	}
	if _, err = service.WaitAttached(s.Identity(), 90*time.Second); err != nil {
		return err
	}
	if _, err = service.Resume("fishing-create", s.Identity(), token, s.Report()); err != nil {
		return err
	}
	stopKeep := (&na.AuthorityKeepAlive{Service: service, Prefix: "fishing", Identity: s.Identity(), Token: token}).Start(ctx)
	defer stopKeep()
	journal, err := na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		return err
	}
	defer journal.Close()
	methodCtx, cancel := context.WithTimeout(ctx, 2*time.Minute)
	defer cancel()
	_, method, err := na.WaitGoalMethod(methodCtx, journal, policy.EnsureFoodSupply, nil)
	if err != nil {
		return err
	}
	plan, _, err := na.WaitPlanTerminal(methodCtx, journal, method.Plan)
	if err != nil {
		return err
	}
	if len(plan.Spec.Actions()) != 1 {
		return fmt.Errorf("fishing method has unexpected actions")
	}
	zone, ok := plan.Spec.Actions()[0].ZoneCreate()
	if !ok || zone.Kind() != domain.FishingZone {
		return fmt.Errorf("food method did not create a fishing zone")
	}
	s.Report()["fishing_method"] = string(method.Plan)
	stopKeep()
	service.Stop()
	journal.Close()
	h, err = s.Reattach(ctx)
	if err != nil {
		return err
	}
	start := 0.0
	_, err = na.RunUntil(ctx, h, "fishing-fifteen-days", 2500, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		v, err := h.Call(ctx, "fishing-outcome", "test/fishing_observe", nil)
		if err != nil {
			return "", false, err
		}
		if na.AsNumber(v["colonists"]) != 2 || na.AsNumber(v["malnutrition"]) > 0.3 || na.AsNumber(v["soilCells"]) != 0 || na.AsNumber(v["competingSources"]) != 0 {
			return "", false, fmt.Errorf("fishing-only feeding failed: %v", v)
		}
		zones := na.AsSlice(v["zones"])
		if len(zones) != 1 {
			return "", false, fmt.Errorf("expected one native fishing zone: %v", v)
		}
		z, _ := na.AsMap(zones[0])
		if math.Abs(na.AsNumber(z["floor"])-domain.FishingPopulationFloor) > 1e-5 || len(na.AsSlice(z["cells"])) != len(zone.Cells()) || z["mode"] != "DoForever" || z["sameBody"] != true {
			return "", false, fmt.Errorf("native fishing zone settings or geometry differ: %v", z)
		}
		if start == 0 {
			start = na.AsNumber(v["tick"])
		}
		done := na.AsNumber(v["tick"])-start >= 15*60000
		if done && (na.AsNumber(v["foodNutrition"]) <= 0 || na.AsNumber(v["catches"]) <= 0) {
			return "", false, fmt.Errorf("no native fishing food outcome: %v", v)
		}
		s.Report()["fishing_after"] = v
		return na.Signature(v["tick"]), done, nil
	})
	return err
}
