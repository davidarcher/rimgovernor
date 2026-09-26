package food

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func init() {
	cases.Register(cases.Case{
		Name: "food/ledger-baseline", Scope: "The ordinary tribal8 baseline's live controller portfolio opens forage and hunt with positive admitted nutrition before any rice is planted or harvested; native map and field censuses bracket the plan read.",
		Start: cases.Save{Name: sustained.BaselineSave}, Quiet: na.QuietRequired,
		RequiredOps: []string{"test/food_baseline_prey"},
		// Bill review produces the shared food ledger and establishes butchery.
		// Leave acquisition dispatch off so it cannot exhaust the forage being
		// inspected while the hunter and butcher prerequisites settle.
		Serve:  &cases.ServeSpec{Families: []string{"bill", "supply", "work", "equip"}, NativeTimeout: 30 * time.Second, Prefix: "ledger-baseline"},
		Budget: 4 * time.Minute, Run: ledgerBaseline,
	})
}

// Decode the public nullable facts without inventing a second plan. The raw
// response (including unknown rows and the controller's explain text) is evidence.
type baselineStatus struct {
	Tick         uint64  `json:"tick"`
	FoodPlanTick *uint64 `json:"foodPlanTick"`
	FoodPlan     *struct {
		Portfolio []struct {
			Kind            policy.FoodChannelKind  `json:"kind"`
			ID              string                  `json:"id"`
			Decision        policy.FoodPlanDecision `json:"decision"`
			Reason          string                  `json:"reason"`
			NutritionPerDay *float64                `json:"nutritionPerDay"`
			DeliveredPerDay float64                 `json:"deliveredPerDay"`
			Terms           []policy.FoodPlanTerm   `json:"terms"`
		} `json:"portfolio"`
		Explain         string  `json:"explain"`
		DeliveredPerDay float64 `json:"deliveredPerDay"`
		DemandPerDay    float64 `json:"demandPerDay"`
		GapPerDay       float64 `json:"gapPerDay"`
	} `json:"foodPlan"`
}

func (v baselineStatus) plan() policy.FoodPlan {
	p := policy.FoodPlan{DeliveredPerDay: v.FoodPlan.DeliveredPerDay, DemandPerDay: v.FoodPlan.DemandPerDay, GapPerDay: v.FoodPlan.GapPerDay}
	for _, row := range v.FoodPlan.Portfolio {
		rate := domain.Unknown[float64]()
		if row.NutritionPerDay != nil {
			rate = domain.Known(*row.NutritionPerDay)
		}
		p.Portfolio = append(p.Portfolio, policy.FoodPlanEntry{
			Channel:  policy.FoodChannel{Kind: row.Kind, ID: row.ID, NutritionPerDay: rate},
			Decision: row.Decision, Reason: row.Reason, DeliveredPerDay: row.DeliveredPerDay, Terms: row.Terms,
		})
	}
	return p
}

// The committed baseline has no growing zones or rice plants. The selected
// families cannot create a field, so checking both ends establishes pre-harvest without
// substituting an elapsed-tick threshold for native evidence.
func baselineCensus(ctx context.Context, s cases.Session, label string) (uint64, error) {
	h := s.Harness()
	zones, err := h.Wire(ctx, label+"-zones", "observations_list_zones", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}})
	if err != nil {
		return 0, err
	}
	observed, ok := na.AsMap(zones["observed"])
	if !ok {
		return 0, fmt.Errorf("zone census unavailable: %v", zones)
	}
	s.Report()[label+"_zones"] = observed
	complete, _ := na.AsMap(observed["completeness"])
	page, _ := na.AsMap(complete["page"])
	if done, _ := na.AsBool(page["complete"]); !done || na.AsNumber(complete["unreadable"]) != 0 || na.AsNumber(complete["filtered"]) != 0 {
		return 0, fmt.Errorf("incomplete field census: %v", complete)
	}
	for _, raw := range na.AsSlice(observed["zones"]) {
		row, _ := na.AsMap(raw)
		if na.AsString(row["cropDefName"]) != "" {
			return 0, fmt.Errorf("baseline already has a growing zone: %v", row)
		}
	}
	rice, err := h.Call(ctx, label+"-rice", "home/list_things", map[string]any{"category": "all", "ownership": "all", "match": "Rice", "includeHeld": true})
	if err != nil {
		return 0, err
	}
	s.Report()[label+"_rice"] = rice
	if success, _ := na.AsBool(rice["success"]); !success {
		return 0, fmt.Errorf("rice census unavailable: %v", rice)
	}
	if len(na.AsSlice(rice["things"])) != 0 {
		return 0, fmt.Errorf("baseline contains rice plants or harvested rice: %v", rice)
	}
	stamp, _ := na.AsMap(observed["context"])
	tick := na.AsNumber(stamp["tick"])
	if tick < 0 {
		return 0, fmt.Errorf("field census lacks tick: %v", stamp)
	}
	return uint64(tick), nil
}

func ledgerBaseline(ctx context.Context, s cases.Session) error {
	if _, resumed := s.Resumed(); !resumed {
		prey, err := s.Harness().Call(ctx, "baseline-prey", "test/food_baseline_prey", nil)
		if err != nil {
			return err
		}
		s.Report()["prey_setup"] = prey
		if ok, _ := na.AsBool(prey["success"]); !ok {
			return fmt.Errorf("native prey setup failed: %v", prey)
		}
		if wild, _ := na.AsBool(prey["wild"]); !wild || na.AsString(prey["kind"]) != "Deer" || na.AsString(prey["prey"]) == "" {
			return fmt.Errorf("missing native wild deer: %v", prey)
		}
	}
	before, err := baselineCensus(ctx, s, "pre_harvest_before")
	if err != nil {
		return err
	}
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	if _, err = service.Acquire(); err != nil {
		return err
	}
	service.KeepAuthority(ctx)
	var status baselineStatus
	err = na.WaitProgress(ctx, na.Wait{Ceiling: 3 * time.Minute, Stall: time.Minute, Interval: 3 * time.Second, Terminal: service.Exited}, func(context.Context) (string, bool, error) {
		body, code, err := service.API("GET", "/api/player/colony", nil, "")
		if err != nil {
			return "", false, err
		}
		s.Report()["colony_status"] = body
		// 503 is the service saying the read raced the world it was reading
		// (observation.ErrChanged): the identity, generation or anchor moved
		// under it. That is what a running colony does between two polls, so
		// it is a sample to take again, not a verdict. Failing the poll on it
		// ended the case on a transport-shaped error (#663); the Wait's own
		// stall and ceiling still end a 503 that never clears.
		if code == 503 {
			s.Report()["last_colony_status_unavailable"] = body
			return "colony status unavailable", false, nil
		}
		if code != 200 {
			return "", false, fmt.Errorf("colony status HTTP %d: %v", code, body)
		}
		delete(s.Report(), "last_colony_status_unavailable")
		raw, err := json.Marshal(body)
		if err != nil {
			return "", false, err
		}
		status = baselineStatus{}
		if err = json.Unmarshal(raw, &status); err != nil {
			return "", false, err
		}
		ready := status.FoodPlan != nil && status.FoodPlanTick != nil && *status.FoodPlanTick >= before && *status.FoodPlanTick <= status.Tick
		if !ready {
			return "plan unavailable", false, nil
		}
		s.Report()["food_plan_explain"] = status.FoodPlan.Explain
		// Native hunting is unavailable until ordinary controller work supplies
		// a butcher bill and an eligible hunter. Preserve every API sample and
		// wait for channel changes, not merely a ticking game.
		check := CheckBaselinePlan(status.plan())
		if check != nil {
			s.Report()["last_plan_check"] = check.Error()
		}
		return status.FoodPlan.Explain, check == nil, nil
	})
	if err != nil {
		return err
	}
	s.Report()["food_plan_explain"] = status.FoodPlan.Explain
	delete(s.Report(), "last_plan_check")
	planErr := CheckBaselinePlan(status.plan())
	service.Stop()
	if _, err = s.Reattach(ctx); err != nil {
		return err
	}
	after, err := baselineCensus(ctx, s, "pre_harvest_after")
	if err != nil {
		return err
	}
	if after < status.Tick {
		return fmt.Errorf("post-plan census tick %d precedes plan census %d", after, status.Tick)
	}
	s.Report()["pre_harvest"] = map[string]any{"before_tick": before, "plan_tick": *status.FoodPlanTick, "after_tick": after, "evidence": "no growing zones, rice plants or harvested rice before or after the controller run; field creation disabled"}
	return planErr
}
