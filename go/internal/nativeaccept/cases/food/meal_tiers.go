package food

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"time"
)

func init() {
	cases.Register(cases.Case{Name: "food/meal-tiers", Scope: "Live cooking selects fine with a skilled cook and raw rice/milk surplus, then replaces its owned fine bill with simple after raw stock is drained, across a controller restart.", Start: EmptyChannels("MealSurvivalPack", 0), RequiredOps: []string{"test/meal_tiers_prepare", "test/meal_tiers_probe"}, Service: true, Budget: 5 * time.Minute, Stall: 90 * time.Second, Run: runMealTiers})
}

func runMealTiers(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h := s.Harness()
	identity := s.Identity()
	if _, err := h.Call(ctx, "meal-prepare", "test/meal_tiers_prepare", nil); err != nil {
		return err
	}
	if _, err := na.ConfirmColonyNames(ctx, h, report); err != nil {
		return err
	}
	service, err := s.Launch(ctx, na.ServiceLaunch{Families: []string{"cooking", "bill"}, Extra: na.ClockSpeedArgs()})
	if err != nil {
		return err
	}
	defer func() { service.Stop() }()
	token, err := service.SessionToken()
	if err != nil {
		return err
	}
	if _, err = service.WaitAttached(identity, 90*time.Second); err != nil {
		return err
	}
	if _, err = service.Resume("meal-fine", identity, token, report); err != nil {
		return err
	}
	keep := (&na.AuthorityKeepAlive{Service: service, Prefix: "meal-fine", Identity: identity, Token: token}).Start(ctx)
	defer func() { keep() }()
	var previous *domain.GoalMethod
	for _, recipe := range []string{"CookMealFine", "CookMealSimple"} {
		journal, e := na.OpenStoreWithRetry(ctx, service.StatePath)
		if e != nil {
			return e
		}
		stage, cancel := context.WithTimeout(ctx, 90*time.Second)
		_, method, e := na.WaitGoalMethod(stage, journal, policy.EnsureCooking, previous)
		if e != nil {
			cancel()
			journal.Close()
			return fmt.Errorf("%s method: %w", recipe, e)
		}
		plan, _, e := na.WaitPlanTerminal(stage, journal, method.Plan)
		cancel()
		journal.Close()
		if e != nil {
			return fmt.Errorf("%s output: %w", recipe, e)
		}
		bill, ok := plan.Spec.Actions()[0].ProductionBill()
		if !ok || bill.Recipe() != recipe {
			return fmt.Errorf("wanted %s, got %v", recipe, plan.Spec.Actions())
		}
		report[recipe+"_plan"] = string(method.Plan)
		keep()
		service.Stop()
		h, e = s.Reattach(ctx)
		if e != nil {
			return e
		}
		if e = pauseForProbe(ctx, h, "meal-"+recipe); e != nil {
			return e
		}
		probe, e := h.Call(ctx, "meal-"+recipe, "test/meal_tiers_probe", map[string]any{"drain": recipe == "CookMealFine"})
		if e != nil {
			return e
		}
		report[recipe+"_native"] = probe
		seen := false
		for _, raw := range na.AsSlice(probe["bills"]) {
			row, _ := na.AsMap(raw)
			name, _ := row["recipe"].(string)
			seen = seen || name == recipe
			if recipe == "CookMealSimple" && name == "CookMealFine" {
				return fmt.Errorf("fine bill survived downgrade")
			}
		}
		if !seen {
			return fmt.Errorf("native %s bill missing", recipe)
		}
		if recipe == "CookMealSimple" {
			return nil
		}
		previous = &method
		if e = s.Release(); e != nil {
			return e
		}
		service.Identity = identity
		service, e = service.Restart(ctx)
		if e != nil {
			return e
		}
		token = service.Token
		if _, e = service.Resume("meal-simple", identity, token, report); e != nil {
			return e
		}
		keep = (&na.AuthorityKeepAlive{Service: service, Prefix: "meal-simple", Identity: identity, Token: token}).Start(ctx)
	}
	return nil
}
