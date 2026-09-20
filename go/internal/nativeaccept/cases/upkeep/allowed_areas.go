package upkeep

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/takeover"
)

func init() {
	cases.Register(cases.Case{
		Name: "takeover/allowed-areas", Scope: "Manual colonist and animal restrictions exclude food; Auto clears them, both eat natively, and a repeated saved restriction is corrected after controller restart and save-load.",
		Start: cases.Save{Name: "RimGovernor-tribal8-baseline"}, QuietWorld: true, Keep: []string{"Food"},
		RequiredOps: []string{"test/feed_setup", "test/area_takeover_edit", "test/area_takeover_read"},
		Serve:       &cases.ServeSpec{Families: []string{"recovery"}, Prefix: "takeover-areas"},
		Budget:      5 * time.Minute, Run: runAllowedAreas,
	})
}

func runAllowedAreas(ctx context.Context, s cases.Session) error {
	if err := takeover.Manual(ctx, s); err != nil {
		return err
	}
	prepared, err := callFixture(ctx, s.Harness(), s.Identity(), "test/feed_setup", map[string]any{})
	if err != nil {
		return err
	}
	pet := na.AsString(prepared["pet"])
	seen := map[domain.PlanID]bool{}
	for pass := range 2 {
		if pass > 0 {
			grant, err := na.GrantAuto(ctx, s.Harness().WireFunc(), "areas-edit-auto", s.Identity())
			if err != nil {
				return err
			}
			if _, err = na.RevokeManual(ctx, s.Harness().WireFunc(), "areas-edit-manual", s.Identity(), grant); err != nil {
				return err
			}
		}
		edited, err := callFixture(ctx, s.Harness(), s.Identity(), "test/area_takeover_edit", map[string]any{"pet": pet, "area": prepared["area"]})
		if err != nil {
			return err
		}
		pawn := na.AsString(edited["pawn"])
		before, err := callFixture(ctx, s.Harness(), s.Identity(), "test/area_takeover_read", map[string]any{"pawn": pawn, "pet": pet})
		if err != nil {
			return err
		}
		if na.AsString(before["pawnArea"]) == "" || na.AsString(before["animalArea"]) == "" {
			return fmt.Errorf("manual lost restrictions: %v", before)
		}
		s.Report()[fmt.Sprintf("manual_%d", pass)] = before
		spec := s.Spec()
		spec.Prefix = fmt.Sprintf("takeover-areas-%d", pass)
		if pass == 1 {
			const save = "RimGovernor-allowed-areas"
			if _, err = s.Harness().Call(ctx, "save-areas", "rimworld/save_game", map[string]any{"saveName": save}); err != nil {
				return err
			}
			if _, err = s.Harness().Call(ctx, "load-areas", "rimworld/load_game_ready", map[string]any{"saveName": save, "readiness": "visual", "timeoutMs": 90000, "ignoreModCompatibility": false}); err != nil {
				return err
			}
			if _, err = s.Harness().Call(ctx, "pause-loaded-areas", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
				return err
			}
			identity, err := na.ReadIdentity(ctx, s.Harness(), "areas-loaded-identity")
			if err != nil {
				return err
			}
			current := s.Identity()
			clear(current)
			for key, value := range identity {
				current[key] = value
			}
		}
		service, err := s.Serve(ctx, spec)
		if err != nil {
			return err
		}
		defer service.Stop()
		journal, err := serveStage(ctx, service, s.Report())
		if err != nil {
			return err
		}
		var corrected domain.Tick
		err = na.WaitProgress(ctx, na.Wait{Ceiling: 90 * time.Second, Stall: 40 * time.Second, Interval: time.Second, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
			plans, err := journal.PlanHistoryWithPrefix(ctx, "routine-recovery-", 256)
			if err != nil {
				return "", false, err
			}
			colonist, animal := false, false
			for _, plan := range plans {
				if seen[plan.Spec.ID()] {
					continue
				}
				for i, action := range plan.Spec.Actions() {
					if plan.Progress[i].View().Stage != domain.Completed {
						continue
					}
					if w, ok := action.WorkAssignment(); ok && string(w.Pawn()) == pawn && w.AreaClear() {
						colonist = true
					}
					if h, ok := action.Husbandry(); ok && string(h.Animal()) == pet && h.Method() == domain.HusbandryAllowedArea && h.Argument() == "" {
						animal = true
					}
				}
			}
			review, err := journal.LoadRoutineReview(ctx)
			if err != nil {
				return "", false, err
			}
			if colonist && animal && corrected == 0 {
				corrected = review.Tick
			}
			return na.Signature(colonist, animal, review.Tick), corrected > 0 && review.Tick >= corrected+15000, nil
		})
		if err != nil {
			return err
		}
		if err = takeover.Journal(ctx, journal, s.Report()); err != nil {
			return err
		}
		plans, err := journal.PlanHistoryWithPrefix(ctx, "routine-recovery-", 256)
		if err != nil {
			return err
		}
		for _, plan := range plans {
			seen[plan.Spec.ID()] = true
		}
		service.Stop()
		if _, err = reattachPaused(ctx, s); err != nil {
			return err
		}
		after, err := callFixture(ctx, s.Harness(), s.Identity(), "test/area_takeover_read", map[string]any{"pawn": pawn, "pet": pet})
		if err != nil {
			return err
		}
		s.Report()[fmt.Sprintf("fed_%d", pass)] = after
		if na.AsString(after["pawnArea"]) != "" || na.AsString(after["animalArea"]) != "" || na.AsNumber(after["pawnFood"]) <= .4 || na.AsNumber(after["animalFood"]) <= .4 {
			return fmt.Errorf("auto must clear restrictions and both pawns must eat, pass %d: %v", pass, after)
		}
	}
	return nil
}
