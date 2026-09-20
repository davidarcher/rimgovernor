package clearance

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"time"
)

func init() {
	cases.Register(cases.Case{Name: "clearance/salvage-remote", Scope: "A useful far-edge ruin is deconstructed and its steel delivered without adding Home.",
		Start:       cases.Fixture{Op: "test/storage_haul_prepare", Args: map[string]any{"itemCount": 1}, On: cases.FlatDebugStart()},
		RequiredOps: []string{"test/loot_remote_drop", "test/salvage_remote"}, Quiet: na.QuietRequired, QuietWorld: true, Budget: 4 * time.Minute, Stall: 60 * time.Second,
		Serve: &cases.ServeSpec{Families: []string{"clearance", "supply", "resource"}, Extra: []string{"--routine-resource-target", "Steel:2000"}}, Run: runSalvageRemote})
}

func runSalvageRemote(ctx context.Context, s cases.Session) error {
	drop, err := s.Harness().Call(ctx, "remote-cell", "test/loot_remote_drop", map[string]any{})
	if err != nil {
		return err
	}
	s.Report()["drop"] = drop
	if !boolean(drop["success"]) {
		return fmt.Errorf("remote fixture: %v", drop)
	}
	staged, err := s.Harness().Call(ctx, "salvage-ready", "test/salvage_remote", map[string]any{"prepare": true})
	if err != nil {
		return err
	}
	s.Report()["prepared"] = staged
	if !boolean(staged["success"]) || !boolean(staged["homeUnchanged"]) {
		return fmt.Errorf("salvage fixture: %v", staged)
	}
	service, err := start(ctx, s)
	if err != nil {
		return err
	}
	defer service.Stop()
	journal, err := service.Store(ctx)
	if err != nil {
		return err
	}
	defer journal.Close()
	err = na.WaitProgress(ctx, wait(service), func(ctx context.Context) (string, bool, error) {
		plans, err := journal.PlanHistoryWithPrefix(ctx, "routine-clearance-", 256)
		if err != nil {
			return "", false, err
		}
		for _, p := range plans {
			for _, progress := range p.Progress {
				if action, ok := progress.Action().Deconstruction(); ok && action.Target() == na.AsString(staged["target"]) && progress.View().Stage == domain.Completed {
					return "salvage completed", true, nil
				}
			}
		}
		return "waiting for salvage", false, nil
	})
	if err != nil {
		return err
	}
	service.Stop()
	if err = reattach(ctx, s); err != nil {
		return err
	}
	for i := 0; i < 8; i++ {
		if _, err = s.Advance(ctx, 1000); err != nil {
			return err
		}
		audit, err := s.Harness().Call(ctx, fmt.Sprintf("salvage-audit-%d", i), "test/salvage_remote", map[string]any{})
		if err != nil {
			return err
		}
		s.Report()["audit"] = audit
		if !boolean(audit["homeUnchanged"]) {
			return fmt.Errorf("home changed: %v", audit)
		}
		if !boolean(audit["present"]) && na.AsNumber(audit["delivered"]) > 0 {
			return nil
		}
	}
	return fmt.Errorf("salvage yield not delivered within 8000 ticks")
}
