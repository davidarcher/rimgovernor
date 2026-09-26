package supply

import (
	"context"
	"fmt"
	"path/filepath"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func init() {
	cases.Register(cases.Case{
		Name:        "supply/loot-safety",
		Scope:       "Mid-run distant loot is forbidden while on a trap, allowed after removing the trap, and hauled into native storage.",
		Start:       cases.Fixture{Op: "test/storage_haul_prepare", Args: map[string]any{"itemCount": 1}, On: cases.FlatDebugStart()},
		RequiredOps: []string{"test/loot_drop", "test/loot_safety_control"},
		Quiet:       na.QuietRequired, QuietWorld: true, Budget: 8 * time.Minute,
		Serve: &cases.ServeSpec{Families: []string{"supply,resource"}, NativeTimeout: 30 * time.Second, Extra: lootServe},
		Run:   runLootSafety,
	})
}

func completedSupply(ctx context.Context, s cases.Session, thing, kind string) func(map[string]any) bool {
	return func(sample map[string]any) bool {
		journal, err := store.Open(ctx, filepath.Join(s.Config().Output, "service.sqlite"))
		if err != nil {
			return false
		}
		defer journal.Close()
		for _, key := range []string{"plans", "retired_plans"} {
			plans, _ := sample[key].([]map[string]any)
			for _, plan := range plans {
				loaded, err := journal.LoadPlan(ctx, domain.PlanID(na.AsString(plan["plan"])))
				if err != nil {
					continue
				}
				for _, progress := range loaded.Progress {
					target, ok := progress.Action().SupplyAllow()
					if ok && target.Thing() == thing && string(progress.Action().Kind()) == kind && progress.View().Stage == domain.Completed {
						return true
					}
				}
			}
		}
		return false
	}
}

// watchSupplySafety samples ManageSupplySafety until the condition holds and
// fails the case when the window ends without it. The watch itself reports no
// error on expiry, so an unmet condition would otherwise fall through to the
// next assertion and be diagnosed as the later state it produced (#664).
func watchSupplySafety(ctx context.Context, s cases.Session, what string, until func(map[string]any) bool) error {
	held := false
	_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{WatchConfig: sustainedfood.WatchConfig{
		Watch: 2 * time.Minute, Goal: policy.ManageSupplySafety, Until: func(sample map[string]any) bool {
			held = until(sample)
			return held
		},
	}})
	if err != nil {
		return err
	}
	if !held {
		return fmt.Errorf("supply safety never %s within the watch window", what)
	}
	return nil
}

func runLootSafety(ctx context.Context, s cases.Session) error {
	watch := func(what string, until func(map[string]any) bool) error {
		return watchSupplySafety(ctx, s, what, until)
	}
	if err := watch("reached a known need", func(sample map[string]any) bool {
		need := na.AsString(sample["need"])
		return need != "" && need != "unknown"
	}); err != nil {
		return err
	}
	drop, err := s.Harness().Call(ctx, "loot-drop", "test/loot_drop", map[string]any{})
	if err != nil {
		return err
	}
	s.Report()["drop"] = drop
	if ok, _ := na.AsBool(drop["success"]); !ok {
		return fmt.Errorf("drop fixture failed: %v", drop)
	}
	if err = watch("forbade the dangerous loot", completedSupply(ctx, s, na.AsString(drop["id"]), "supply_forbid")); err != nil {
		return err
	}
	unsafe, err := s.Harness().Call(ctx, "loot-unsafe", "test/loot_safety_control", map[string]any{})
	if err != nil {
		return err
	}
	s.Report()["unsafe"] = unsafe
	if forbidden, _ := na.AsBool(unsafe["forbidden"]); !forbidden {
		return fmt.Errorf("dangerous loot was not forbidden: %v", unsafe)
	}
	if _, err = s.Harness().Call(ctx, "loot-remove-danger", "test/loot_safety_control", map[string]any{"removeDanger": true}); err != nil {
		return err
	}
	if err = watch("allowed the loot once its trap was gone", completedSupply(ctx, s, na.AsString(drop["id"]), "supply_allow")); err != nil {
		return err
	}
	safe, err := s.Harness().Call(ctx, "loot-safe", "test/loot_safety_control", map[string]any{})
	if err != nil {
		return err
	}
	if forbidden, _ := na.AsBool(safe["forbidden"]); forbidden {
		return fmt.Errorf("safe loot remains forbidden: %v", safe)
	}
	// Allow's receipt is insufficient: ordinary pawn hauling must deliver
	// both 25-steel stacks to the prepared native stockpile.
	for i := 0; i < 8; i++ {
		if _, err = s.Advance(ctx, 1000); err != nil {
			return err
		}
		stored, err := s.Harness().Call(ctx, fmt.Sprintf("loot-stored-%d", i), "test/loot_safety_control", map[string]any{})
		if err != nil {
			return err
		}
		s.Report()["stored"] = stored
		spawned, _ := na.AsBool(stored["spawned"])
		inStockpile, _ := na.AsBool(stored["inStockpile"])
		if na.AsNumber(stored["storedSteel"]) >= 50 && (!spawned || inStockpile) {
			return nil
		}
	}
	return fmt.Errorf("allowed loot was not hauled to storage within 8000 ticks")
}
