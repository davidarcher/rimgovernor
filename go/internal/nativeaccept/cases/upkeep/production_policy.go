package upkeep

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "production/policy-clear", Scope: "Configured production stops/floors survive a save, then empty controller config clears them and native kibble production spends ingredients (#499).",
		Start: cases.Save{Name: "RimGovernor-tribal8-baseline"}, QuietWorld: true,
		RequiredOps: []string{"test/feed_setup"},
		Serve:       &cases.ServeSpec{Families: []string{"production-policy", "animal-feed", "resource", "bill", "work"}, Prefix: "policy-clear"},
		Budget:      4 * time.Minute, Run: runProductionPolicyClear,
	})
}

func runProductionPolicyClear(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	prepared, err := callFixture(ctx, h, s.Identity(), "test/feed_setup", map[string]any{})
	if err != nil {
		return err
	}
	s.Report()["prepared"] = prepared
	// The initial controller owns the restriction; the next starts with no
	// configured restriction. No native policy fixture writes the replacement.
	spec := s.Spec()
	spec.Families = []string{"production-policy"}
	spec.Extra = []string{"--routine-resource-reserve", "Hay:1000", "--routine-resource-stop", "Hay", "--routine-resource-stop", "Meat_Muffalo"}
	service, err := s.Serve(ctx, spec)
	if err != nil {
		return err
	}
	defer service.Stop()
	journal, err := serveStage(ctx, service, s.Report())
	if err != nil {
		return err
	}
	err = na.WaitProgress(ctx, na.Wait{Ceiling: 45 * time.Second, Stall: 30 * time.Second, Interval: time.Second, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		plans, err := journal.PlanHistoryWithPrefix(ctx, "routine-production-policy-", 256)
		if err != nil {
			return "", false, err
		}
		for _, plan := range plans {
			for i, action := range plan.Spec.Actions() {
				if value, ok := action.ProductionPolicy(); ok && len(value.Stopped()) == 2 && plan.Progress[i].View().Stage == domain.Completed {
					return "installed", true, nil
				}
			}
		}
		return na.Signature(plans), false, nil
	})
	service.Stop()
	if err != nil {
		return err
	}
	h, err = reattachPaused(ctx, s)
	if err != nil {
		return err
	}
	save := "RimGovernor-policy-clear"
	if _, err = h.Call(ctx, "save-policy", "rimworld/save_game", map[string]any{"saveName": save}); err != nil {
		return err
	}
	if _, err = h.Call(ctx, "reload-policy", "rimworld/load_game_ready", map[string]any{"saveName": save, "readiness": "visual", "timeoutMs": 90000, "ignoreModCompatibility": false}); err != nil {
		return err
	}
	if _, err = h.Call(ctx, "pause-policy", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	identity, err := na.ReadIdentity(ctx, h, "policy-reloaded-identity")
	if err != nil {
		return err
	}
	for key := range s.Identity() {
		delete(s.Identity(), key)
	}
	for key, value := range identity {
		s.Identity()[key] = value
	}
	read := func(label string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "observations_read_production_policy", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}, "page": map[string]any{"limit": 256}})
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		return observed, err
	}
	before, err := read("saved-policy")
	if err != nil {
		return err
	}
	s.Report()["saved_policy"] = before
	if len(na.AsSlice(before["stoppedDefs"])) != 2 {
		return fmt.Errorf("saved stops missing: %v", before)
	}
	stockBefore, err := readUpkeep(ctx, h, s.Identity(), "ingredients-before")
	if err != nil {
		return err
	}
	service, err = s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	journal, err = serveStage(ctx, service, s.Report())
	if err != nil {
		return err
	}
	if err = watchFeed(ctx, journal, prepared, s.Report()); err != nil {
		return err
	}
	service.Stop()
	h, err = reattachPaused(ctx, s)
	if err != nil {
		return err
	}
	after, err := read("cleared-policy")
	if err != nil {
		return err
	}
	s.Report()["cleared_policy"] = after
	if len(na.AsSlice(after["floors"])) != 0 || len(na.AsSlice(after["stoppedDefs"])) != 0 {
		return fmt.Errorf("obsolete policy survived: %v", after)
	}
	stockAfter, err := readUpkeep(ctx, h, s.Identity(), "ingredients-after")
	if err != nil {
		return err
	}
	count := func(c upkeepCensus, def string) int64 {
		var total int64
		for _, row := range c.Items {
			if row.Definition == def {
				total += row.Count
			}
		}
		return total
	}
	if count(stockAfter, "Kibble") <= count(stockBefore, "Kibble") || count(stockAfter, "Hay") >= count(stockBefore, "Hay") {
		return fmt.Errorf("native spending did not resume: hay %d -> %d, kibble %d -> %d", count(stockBefore, "Hay"), count(stockAfter, "Hay"), count(stockBefore, "Kibble"), count(stockAfter, "Kibble"))
	}
	s.Report()["hay_spent"] = count(stockBefore, "Hay") - count(stockAfter, "Hay")
	return verifyFeed(ctx, h, s.Identity(), prepared, s.Report())
}
