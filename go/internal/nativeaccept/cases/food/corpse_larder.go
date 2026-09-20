package food

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"strings"
	"time"
)

func init() {
	cases.Register(cases.Case{
		Name: "food/corpse-larder", Scope: "Three forbidden muffalo corpses remain frozen with a forever butcher bill and 60 meat. The live food-storage-upkeep planner releases one on cook demand; ordinary native butchering produces meat while two reserves remain forbidden, using fewer freezer tiles than butchered meat.",
		Start:   cases.Fixture{Op: "test/refrigeration_prepare", Args: map[string]any{"corpseLarder": true, "existingCooler": true, "roomTemperatureC": -5, "coolerTargetC": -20, "roomWidth": 8, "roomHeight": 6, "rotProgressFraction": 0}},
		Service: true, Budget: 5 * time.Minute, Stall: 90 * time.Second, Run: runCorpseLarder,
	})
}

func larderProbe(ctx context.Context, h *na.Harness, label string, drain bool) (map[string]any, error) {
	v, err := h.Call(ctx, label, "test/corpse_larder_probe", map[string]any{"drain": drain})
	if err != nil {
		return nil, err
	}
	if ok, _ := na.AsBool(v["success"]); !ok {
		return nil, fmt.Errorf("larder probe: %v", v)
	}
	return v, nil
}
func frozenReserves(v map[string]any, n int) bool {
	rows := na.AsSlice(v["corpses"])
	if len(rows) != n {
		return false
	}
	for _, raw := range rows {
		r, _ := na.AsMap(raw)
		forbidden, _ := na.AsBool(r["forbidden"])
		safe, _ := na.AsBool(r["safe"])
		if !forbidden || !safe || na.AsNumber(r["temperature"]) > 0 || r["rot"] != "Fresh" {
			return false
		}
	}
	forever, _ := na.AsBool(v["foreverButcher"])
	return forever
}
func runCorpseLarder(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	report := s.Report()
	identity := s.Identity()
	before, err := larderProbe(ctx, h, "larder-before", false)
	if err != nil {
		return err
	}
	report["before"] = before
	if !frozenReserves(before, 3) || na.AsNumber(before["rawMeat"]) != 60 || na.AsNumber(before["corpseTiles"]) >= na.AsNumber(before["butcheredTiles"]) {
		return fmt.Errorf("invalid larder fixture: %v", before)
	}
	start := na.AsNumber(before["tick"])
	_, err = na.RunUntil(ctx, h, "larder-idle", 1800, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		v, e := larderProbe(ctx, h, "larder-idle-probe", false)
		if e != nil {
			return "", false, e
		}
		report["idle"] = v
		if !frozenReserves(v, 3) {
			return "", false, fmt.Errorf("forbidden reserves did not survive: %v", v)
		}
		return na.Signature(v["tick"]), na.AsNumber(v["tick"])-start >= 1200, nil
	})
	if err != nil {
		return err
	}
	if _, err = h.Call(ctx, "larder-pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	drained, err := larderProbe(ctx, h, "larder-demand", true)
	if err != nil {
		return err
	}
	report["demand"] = drained
	if _, err = na.ConfirmColonyNames(ctx, h, report); err != nil {
		return err
	}
	service, err := s.Launch(ctx, na.ServiceLaunch{Families: []string{"food-storage-upkeep"}, Extra: na.ClockSpeedArgs()})
	if err != nil {
		return err
	}
	defer service.Stop()
	token, err := service.SessionToken()
	if err != nil {
		return err
	}
	if _, err = service.WaitAttached(identity, 90*time.Second); err != nil {
		return err
	}
	spare, _ := na.AsMap(s.Prepared()["spareCell"])
	if _, err = service.SubmitAndResume("corpse-larder", identity, map[string]any{"defName": "Wall", "x": int(na.AsNumber(spare["x"])), "z": int(na.AsNumber(spare["z"])), "rotation": "north", "stuff": "WoodLog"}, token, report); err != nil {
		return err
	}
	keep := (&na.AuthorityKeepAlive{Service: service, Prefix: "corpse-larder", Identity: identity, Token: token}).Start(ctx)
	defer keep()
	journal, err := na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		return err
	}
	defer journal.Close()
	methodCtx, cancel := context.WithTimeout(ctx, 3*time.Minute)
	defer cancel()
	// The food reserve (#428) reviews first under the same goal and may
	// hold or release the start's survival meals before the larder gets
	// its turn; follow those plans to their end and wait for the corpse.
	seen := map[domain.PlanID]bool{}
	var method domain.GoalMethod
	for {
		_, method, err = na.WaitGoalMethodExcluding(methodCtx, journal, policy.MaintainFoodStorage, seen)
		if err != nil {
			return err
		}
		seen[method.Plan] = true
		if strings.HasPrefix(string(method.Plan), "routine-corpse-") {
			break
		}
		if !strings.HasPrefix(string(method.Plan), "routine-reserve-") {
			return fmt.Errorf("expected a reserve or corpse plan, got %s", method.Plan)
		}
		if _, _, err = na.WaitPlanTerminal(methodCtx, journal, method.Plan); err != nil {
			return err
		}
	}
	plan, _, err := na.WaitPlanTerminal(methodCtx, journal, method.Plan)
	if err != nil {
		return err
	}
	actions := plan.Spec.Actions()
	if len(actions) != 1 || actions[0].Kind() != domain.SupplyAllowAction {
		return fmt.Errorf("expected one corpse release, got %v", actions)
	}
	report["release_plan"] = string(method.Plan)
	journal.Close()
	service.Stop()
	h, err = s.Reattach(ctx)
	if err != nil {
		return err
	}
	_, err = na.RunUntil(ctx, h, "larder-butcher", 12000, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		v, e := larderProbe(ctx, h, "larder-after", false)
		if e != nil {
			return "", false, e
		}
		report["after"] = v
		if len(na.AsSlice(v["corpses"])) < 2 {
			return "", false, fmt.Errorf("released more than one corpse: %v", v)
		}
		return na.Signature(v["rawMeat"], len(na.AsSlice(v["corpses"]))), frozenReserves(v, 2) && na.AsNumber(v["rawMeat"]) > 60, nil
	})
	return err
}
