package upkeep

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/takeover"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func init() {
	for _, name := range []string{"built-facility", "home-removal"} {
		cases.Register(cases.Case{
			Name: "takeover/" + name, Scope: "Manual player facility/Home edit is maintained under Auto without autonomous build history; native Home readback and journal audit.",
			Start: cases.Save{Name: "RimGovernor-tribal8-baseline"}, QuietWorld: true,
			RequiredOps: []string{"test/sleeping_setup", "test/home_coverage_setup", "test/home_remove_cell", "test/home_coverage_read"},
			Serve:       &cases.ServeSpec{Families: []string{"home-coverage", "work"}, Prefix: "takeover-home"},
			Budget:      3 * time.Minute, Run: func(ctx context.Context, s cases.Session) error {
				return runHomeTakeover(ctx, s, name == "home-removal")
			},
		})
	}
	cases.Register(cases.Case{
		Name: "takeover/suspended-bill", Scope: "A player-created suspended kibble bill in Manual is corrected under Auto; the journal completes production and native reachable feed appears.",
		Start: cases.Save{Name: "RimGovernor-tribal8-baseline"}, QuietWorld: true, RequiredOps: []string{"test/feed_setup"},
		Serve:  &cases.ServeSpec{Families: []string{"animal-feed", "resource", "bill", "work"}, Prefix: "takeover-bill"},
		Budget: 4 * time.Minute, Run: runBillTakeover,
	})
}

func runHomeTakeover(ctx context.Context, s cases.Session, removal bool) error {
	if err := takeover.Manual(ctx, s); err != nil {
		return err
	}
	h := s.Harness()
	// A bed for everyone: with one short the Foothold stage (#630) holds
	// MaintainHomeCoverage behind the shelter gate and the wait never moves.
	prepared, err := callFixture(ctx, h, s.Identity(), "test/sleeping_setup", map[string]any{"bedsForAll": true})
	if err != nil {
		return err
	}
	beds := na.AsSlice(prepared["ownedBeds"])
	if len(beds) == 0 {
		return fmt.Errorf("fixture supplied no player-built bed")
	}
	bed, _ := na.AsMap(beds[0])
	target := na.AsString(bed["bed"])
	if target == "" {
		return fmt.Errorf("fixture bed lacks identity")
	}
	s.Report()["player_facility"] = bed
	if removal {
		if _, err = callFixture(ctx, h, s.Identity(), "test/home_remove_cell", map[string]any{"target": target, "x": bed["x"], "z": bed["z"]}); err != nil {
			return err
		}
	} else {
		if _, err = callFixture(ctx, h, s.Identity(), "test/home_coverage_setup", map[string]any{"target": target}); err != nil {
			return err
		}
	}
	before, err := readHomeCoverage(ctx, h, s.Identity(), target, "manual-home-readback")
	if err != nil {
		return err
	}
	if before.Total <= 0 || before.Covered >= before.Total {
		return fmt.Errorf("manual edit left no Home deficit: %+v", before)
	}
	s.Report()["home_before"] = before
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	journal, err := serveStage(ctx, service, s.Report())
	if err != nil {
		return err
	}
	// A fresh service database must not contain an autonomous construction receipt
	// for the fixture-built bed; maintenance must use the building census alone.
	plans, err := journal.PlanHistoryWithPrefix(ctx, "routine-sleeping-", 256)
	if err != nil {
		return err
	}
	if len(plans) != 0 {
		return fmt.Errorf("player facility unexpectedly has sleeping plan history")
	}
	// Every bed in this room shares the same Home footprint. Any one of them
	// can anchor the write; the independent audit below checks the edited bed.
	err = na.WaitProgress(ctx, na.Wait{Ceiling: 90 * time.Second, Stall: 45 * time.Second, Interval: time.Second, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		review, err := journal.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		recovered := false
		for _, binding := range review.Goals {
			if binding.Need != policy.MaintainHomeCoverage {
				continue
			}
			goal, err := journal.LoadGoal(ctx, binding.Goal)
			if err != nil {
				return "", false, err
			}
			recovered = goal.Goal.Need == domain.NeedRecovered
		}
		plans, err := journal.PlanHistoryWithPrefix(ctx, "routine-", 256)
		if err != nil {
			return "", false, err
		}
		completed := false
		for _, plan := range plans {
			for i, action := range plan.Spec.Actions() {
				if _, ok := action.HomeCoverage(); ok && plan.Progress[i].View().Stage == domain.Completed {
					completed = true
				}
			}
		}
		return na.Signature(recovered, completed), recovered && completed, nil
	})
	if err != nil {
		return err
	}
	if err = takeover.Journal(ctx, journal, s.Report()); err != nil {
		return err
	}
	service.Stop()
	h, err = reattachPaused(ctx, s)
	if err != nil {
		return err
	}
	after, err := readHomeCoverage(ctx, h, s.Identity(), target, "auto-home-readback")
	if err != nil {
		return err
	}
	s.Report()["home_after"] = after
	if after.Total != before.Total || after.Covered != after.Total {
		return fmt.Errorf("auto left missing Home: %+v", after)
	}
	return nil
}

func runBillTakeover(ctx context.Context, s cases.Session) error {
	if err := takeover.Manual(ctx, s); err != nil {
		return err
	}
	prepared, err := callFixture(ctx, s.Harness(), s.Identity(), "test/feed_setup", map[string]any{})
	if err != nil {
		return err
	}
	bench := na.AsString(prepared["bench"])
	if bench == "" {
		return fmt.Errorf("feed fixture lacks reachable bench")
	}
	edit, err := s.Harness().Call(ctx, "manual-suspended-bill", "home/bills", map[string]any{
		"action": "add", "bench": strings.TrimPrefix(bench, "Thing_"), "recipe": "Make_Kibble", "suspended": "on", "dryRun": false, "watch": false,
	})
	if err != nil {
		return err
	}
	if ok, _ := na.AsBool(edit["applied"]); !ok {
		return fmt.Errorf("player bill edit refused: %v", edit)
	}
	s.Report()["manual_bill_edit"] = edit
	before, err := s.Harness().Call(ctx, "manual-bill-readback", "home/bills", map[string]any{"action": "list", "bench": strings.TrimPrefix(bench, "Thing_")})
	if err != nil {
		return err
	}
	attention, _ := na.AsMap(before["attention"])
	if na.AsNumber(attention["suspendedBills"]) != 1 {
		return fmt.Errorf("player bill is not suspended: %v", before)
	}
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	journal, err := serveStage(ctx, service, s.Report())
	if err != nil {
		return err
	}
	if err = watchFeed(ctx, journal, prepared, s.Report()); err != nil {
		return err
	}
	if err = takeover.Journal(ctx, journal, s.Report()); err != nil {
		return err
	}
	service.Stop()
	h, err := reattachPaused(ctx, s)
	if err != nil {
		return err
	}
	after, err := h.Call(ctx, "auto-bill-readback", "home/bills", map[string]any{"action": "list", "bench": strings.TrimPrefix(bench, "Thing_")})
	if err != nil {
		return err
	}
	attention, _ = na.AsMap(after["attention"])
	if na.AsNumber(attention["suspendedBills"]) != 0 {
		return fmt.Errorf("auto retained suspended bill: %v", after)
	}
	s.Report()["bills_after"] = after
	return verifyFeed(ctx, h, s.Identity(), prepared, s.Report())
}
