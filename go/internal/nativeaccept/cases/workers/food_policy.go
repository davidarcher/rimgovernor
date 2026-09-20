package workers

import (
	"context"
	"fmt"
	"slices"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/takeover"
)

func init() {
	cases.Register(cases.Case{
		Name: "takeover/food-policy", Scope: "Restrictive Manual diet is untouched in Manual, exact settings CAS refuses edits, Auto repairs repeated restrictions through Hands, actual eating recovers nutrition, and the diet survives save/load.",
		// runFoodPolicy establishes Manual before applying and checking each diet edit.
		Start: cases.Save{Name: "RimGovernor-tribal8-baseline"}, QuietWorld: true, Keep: []string{"Food"}, RequiredOps: []string{"test/food_policy"},
		Serve: &cases.ServeSpec{Families: []string{"work"}, Prefix: "food-policy"}, Budget: 4 * time.Minute, Run: runFoodPolicy,
	})
}

func runFoodPolicy(ctx context.Context, s cases.Session) error {
	fixture := func(label, action string) (map[string]any, error) {
		return s.Harness().Call(ctx, label, "test/food_policy", map[string]any{"action": action})
	}
	seen := map[domain.ActionID]bool{}
	for stage := 0; stage < 2; stage++ {
		label := fmt.Sprintf("food-%d", stage)
		grant, err := na.GrantAuto(ctx, s.Harness().WireFunc(), label+"-initial", s.Identity())
		if err != nil {
			return err
		}
		if _, err = na.RevokeManual(ctx, s.Harness().WireFunc(), label+"-manual", s.Identity(), grant); err != nil {
			return err
		}
		before, err := fixture(label+"-restrict", "restrict")
		if err != nil {
			return err
		}
		pawn := na.AsString(before["pawn"])
		if allowed, _ := na.AsBool(before["allowed"]); allowed || na.AsNumber(before["food"]) > .11 {
			return fmt.Errorf("restrictive hungry precondition absent: %v", before)
		}
		rows, err := readPawns(ctx, s, label+"-before", []string{pawn})
		if err != nil {
			return err
		}
		token, _ := rows[0].SnapshotToken.Value()
		diet, known := rows[0].FoodRestriction.Value()
		if !known || slices.Contains(diet.Allowed, "MealSimple") || !slices.Contains(diet.Eligible, "MealSimple") {
			return fmt.Errorf("missing typed restrictive diet: %+v", diet)
		}
		operation := map[string]any{"patchPawn": map[string]any{"pawn": map[string]any{"entityId": pawn, "expectedSnapshotToken": token}, "foodAllow": map[string]any{"defs": []string{"MealSimple"}}}}
		denied, err := s.Harness().Wire(ctx, label+"-manual-write", "operations_execute", map[string]any{
			"precondition": map[string]any{"identity": s.Identity(), "expectedGeneration": na.GrantGeneration(grant), "attempt": map[string]any{"controllerSessionId": owner, "actionId": fmt.Sprintf("food-manual-%d", stage), "attemptId": "1"}}, "operation": operation,
		})
		if err != nil {
			return err
		}
		if _, ok := na.AsMap(denied["failure"]); !ok {
			return fmt.Errorf("manual accepted diet write: %v", denied)
		}
		unchanged, err := fixture(label+"-manual-read", "read")
		if err != nil {
			return err
		}
		if na.AsString(unchanged["policy"]) != na.AsString(before["policy"]) {
			return fmt.Errorf("manual policy changed")
		}
		if _, err = fixture(label+"-edit", "edit"); err != nil {
			return err
		}
		stale, err := s.Harness().Wire(ctx, label+"-stale", "operations_preview", map[string]any{"identity": s.Identity(), "operation": operation})
		if err != nil {
			return err
		}
		if _, ok := na.AsMap(stale["failure"]); !ok {
			return fmt.Errorf("stale food settings accepted: %v", stale)
		}
		// Restore exactly the same Manual policy each round: recurrence must not
		// depend on a novel policy id or whitelist hash.
		before, err = fixture(label+"-reset", "restrict")
		if err != nil {
			return err
		}
		service, err := s.Serve(ctx, s.Spec())
		if err != nil {
			return err
		}
		if _, err = service.Acquire(); err != nil {
			service.Stop()
			return err
		}
		service.KeepAuthority(ctx)
		journal, err := service.Store(ctx)
		if err != nil {
			service.Stop()
			return err
		}
		var completed domain.ActionID
		err = na.WaitProgress(ctx, na.Wait{Ceiling: 90 * time.Second, Stall: 60 * time.Second, Interval: time.Second, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
			plans, err := journal.PlanHistoryWithPrefix(ctx, "routine-work-", 256)
			if err != nil {
				return "", false, err
			}
			for _, plan := range plans {
				for i, action := range plan.Spec.Actions() {
					w, ok := action.WorkAssignment()
					if ok && string(w.Pawn()) == pawn && len(w.FoodAllow()) > 0 && plan.Progress[i].View().Stage == domain.Completed && !seen[action.ID()] {
						completed = action.ID()
						return string(completed), true, nil
					}
				}
			}
			return na.Signature(plans), false, nil
		})
		if err == nil {
			seen[completed] = true
		}
		if err == nil {
			err = takeover.Journal(ctx, journal, s.Report())
		}
		service.Stop()
		if err != nil {
			return err
		}
		if _, err = s.Reattach(ctx); err != nil {
			return err
		}
		// Settings repair completes while paused. The work-only service has no
		// production work to admit a clock window, so observe eating in a bounded
		// native window after releasing the service's authority.
		clock := &na.ScenarioClock{Wire: s.Harness().WireFunc(), Identity: s.Identity(), Owner: s.RequestID(label + "-eat"), Report: s.Report()}
		if _, err = clock.Acquire(ctx, label+"-eat-acquire"); err != nil {
			return err
		}
		runtime := &na.ScenarioRuntime{Query: s.Harness().Call, Clock: clock, Report: s.Report(), Tools: s.Names()}
		if _, err = na.AdvanceGame(ctx, runtime, 2500); err != nil {
			return err
		}
		after, err := fixture(label+"-outcome", "read")
		if err != nil {
			return err
		}
		allowed, _ := na.AsBool(after["allowed"])
		preserved, _ := na.AsBool(after["manualStillRestricted"])
		if !allowed || !preserved || na.AsNumber(after["food"]) < .4 || na.AsNumber(after["eaten"]) <= na.AsNumber(before["eaten"]) {
			return fmt.Errorf("food takeover failed native eating/recovery: before=%v after=%v", before, after)
		}
		s.Report()[label] = map[string]any{"before": before, "after": after, "action": completed, "manual_refusal": denied, "stale_refusal": stale}
	}
	h := s.Harness()
	before, err := fixture("food-before-save", "read")
	if err != nil {
		return err
	}
	const save = "RimGovernor-food-policy"
	if _, err = h.Call(ctx, "food-save", "rimworld/save_game", map[string]any{"saveName": save}); err != nil {
		return err
	}
	if _, err = h.Call(ctx, "food-load", "rimworld/load_game_ready", map[string]any{"saveName": save, "readiness": "visual", "timeoutMs": 90000, "ignoreModCompatibility": false}); err != nil {
		return err
	}
	if _, err = h.Call(ctx, "food-pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	after, err := fixture("food-after-load", "read")
	if err != nil {
		return err
	}
	allowed, _ := na.AsBool(after["allowed"])
	if !allowed || na.AsString(after["policy"]) != na.AsString(before["policy"]) || na.AsNumber(after["eaten"]) < na.AsNumber(before["eaten"]) {
		return fmt.Errorf("food policy did not survive save/load: %v", after)
	}
	s.Report()["food_save_recovery"] = after
	return nil
}
