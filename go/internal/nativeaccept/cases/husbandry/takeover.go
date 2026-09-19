package husbandry

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
		Name: "takeover/herd-removal", Scope: "Manual release/slaughter flags on retained animals are cancelled through shared Hands on Auto; training resumes, no replacement tame is created, and cancellation survives controller restart and native save/load.",
		Start: cases.Fixture{Op: "test/husbandry_setup"}, RequiredOps: []string{"test/herd_removal"},
		Serve:  &cases.ServeSpec{Families: []string{"husbandry"}, Prefix: "herd-removal", Extra: []string{"--routine-herd-population-min", "Cow:1", "--routine-herd-population-min", "Husky:2"}},
		Budget: 4 * time.Minute, Run: runRemovalTakeover,
	})
}

func runRemovalTakeover(ctx context.Context, s cases.Session) error {
	if err := takeover.Manual(ctx, s); err != nil {
		return err
	}
	before, err := s.Harness().Call(ctx, "manual-removals", "test/herd_removal", map[string]any{"apply": true})
	if err != nil {
		return err
	}
	if before["slaughter"] != true || before["release"] != true {
		return fmt.Errorf("manual flags absent: %v", before)
	}
	s.Report()["manual_removals"] = before
	for phase := 0; phase < 2; phase++ {
		service, err := s.Serve(ctx, s.Spec())
		if err != nil {
			return err
		}
		defer service.Stop()
		if _, err = service.Acquire(); err != nil {
			return err
		}
		service.KeepAuthority(ctx)
		journal, err := service.Store(ctx)
		if err != nil {
			return err
		}
		_, _, err = service.WaitRoutineReview(ctx, journal, 45*time.Second)
		if err != nil {
			return err
		}
		err = na.WaitProgress(ctx, na.Wait{Ceiling: 60 * time.Second, Stall: 30 * time.Second, Interval: time.Second, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
			plans, err := journal.PlanHistoryWithPrefix(ctx, "routine-", 256)
			if err != nil {
				return "", false, err
			}
			cancelled := map[domain.HusbandryMethod]bool{}
			trained := false
			for _, plan := range plans {
				for _, progress := range plan.Progress {
					h, ok := progress.Action().Husbandry()
					if !ok {
						continue
					}
					if h.Method() == domain.HusbandryTame {
						return "", false, fmt.Errorf("unwanted replacement taming: %s", h.Animal())
					}
					if progress.View().Stage == domain.Completed {
						cancelled[h.Method()] = true
					}
					// Training's native request is read back below; handler labor is
					// deliberately disabled so removal cannot win a fixture race.
					if h.Method() == domain.HusbandryTrain && string(h.Animal()) == na.AsString(before["dog"]) {
						trained = progress.View().Stage == domain.Completed
					}
				}
			}
			return na.Signature(cancelled, trained), cancelled[domain.HusbandryCancelRelease] && cancelled[domain.HusbandryCancelSlaughter] && trained, nil
		})
		if err != nil {
			return err
		}
		if err = takeover.Journal(ctx, journal, s.Report()); err != nil {
			return err
		}
		service.Stop()
		h, err := s.Reattach(ctx)
		if err != nil {
			return err
		}
		if _, err = h.Call(ctx, "pause-removals", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
			return err
		}
		after, err := h.Call(ctx, "read-removals", "test/herd_removal", map[string]any{})
		if err != nil {
			return err
		}
		if after["slaughter"] != false || after["release"] != false || after["training"] != true {
			return fmt.Errorf("auto readback: %v", after)
		}
		s.Report()[fmt.Sprintf("auto_removals_%d", phase)] = after
	}
	h := s.Harness()
	if _, err = h.Call(ctx, "save-removals", "rimworld/save_game", map[string]any{"saveName": "RimGovernor-herd-removal"}); err != nil {
		return err
	}
	if _, err = h.Call(ctx, "load-removals", "rimworld/load_game_ready", map[string]any{"saveName": "RimGovernor-herd-removal", "readiness": "visual", "timeoutMs": 90000, "ignoreModCompatibility": false}); err != nil {
		return err
	}
	after, err := h.Call(ctx, "saved-removals", "test/herd_removal", map[string]any{})
	if err != nil {
		return err
	}
	if after["slaughter"] != false || after["release"] != false || after["training"] != true {
		return fmt.Errorf("save/load readback: %v", after)
	}
	s.Report()["saved_removals"] = after
	return nil
}
