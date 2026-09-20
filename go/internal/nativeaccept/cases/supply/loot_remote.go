package supply

import (
	"context"
	"fmt"
	"path/filepath"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// lootServe declares the Steel demand a remote stack scores against (#522):
// the resource family carries the target, MaintainResource's own work is
// incidental to these cases.
var lootServe = []string{"--routine-resource-target", "Steel:2000"}

func init() {
	cases.Register(cases.Case{
		Name:        "supply/loot-remote",
		Scope:       "A forbidden useful Steel stack near the far map edge stays forbidden with a reach hold while readiness is base, is allowed once readiness raises reach, and is hauled into native storage; its cell stays outside Home.",
		Start:       cases.Fixture{Op: "test/storage_haul_prepare", Args: map[string]any{"itemCount": 1}, On: cases.FlatDebugStart()},
		RequiredOps: []string{"test/loot_remote_drop", "test/loot_remote_control"},
		Quiet:       na.QuietRequired, QuietWorld: true, Budget: 10 * time.Minute,
		Serve: &cases.ServeSpec{Families: []string{"supply,resource"}, NativeTimeout: 30 * time.Second, Extra: lootServe},
		Run:   runLootRemote,
	})
}

// lootHeld reports whether the latest review holds the stack with a reach
// or demand reason, recording the reason on the report.
func lootHeld(ctx context.Context, s cases.Session, thing string) func(map[string]any) bool {
	return func(sample map[string]any) bool {
		journal, err := store.Open(ctx, filepath.Join(s.Config().Output, "service.sqlite"))
		if err != nil {
			return false
		}
		defer journal.Close()
		review, err := journal.LoadRoutineReview(ctx)
		if err != nil {
			return false
		}
		for _, hold := range review.EventLoot.Held {
			if hold.Thing == thing {
				s.Report()["hold"] = map[string]any{"reason": hold.Reason, "tick": uint64(review.Tick)}
				return true
			}
		}
		return false
	}
}

func runLootRemote(ctx context.Context, s cases.Session) error {
	watch := func(until func(map[string]any) bool) error {
		_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{WatchConfig: sustainedfood.WatchConfig{
			Watch: 2 * time.Minute, Goal: policy.ManageSupplySafety, Until: until,
		}})
		return err
	}
	drop, err := s.Harness().Call(ctx, "loot-remote-drop", "test/loot_remote_drop", map[string]any{})
	if err != nil {
		return err
	}
	s.Report()["drop"] = drop
	if ok, _ := na.AsBool(drop["success"]); !ok {
		return fmt.Errorf("remote drop fixture failed: %v", drop)
	}
	thing := na.AsString(drop["id"])
	// Base reach: the safe stack is a hold with a reach reason, not an Allow.
	if err = watch(lootHeld(ctx, s, thing)); err != nil {
		return err
	}
	held, err := s.Harness().Call(ctx, "loot-remote-held", "test/loot_remote_control", map[string]any{})
	if err != nil {
		return err
	}
	s.Report()["held"] = held
	if forbidden, _ := na.AsBool(held["forbidden"]); !forbidden {
		return fmt.Errorf("remote loot was allowed under base reach: %v", held)
	}
	raised, err := s.Harness().Call(ctx, "loot-remote-readiness", "test/loot_remote_control", map[string]any{"raiseReadiness": true})
	if err != nil {
		return err
	}
	s.Report()["readiness"] = raised
	if err = watch(completedSupply(ctx, s, thing, "supply_allow")); err != nil {
		return err
	}
	allowed, err := s.Harness().Call(ctx, "loot-remote-allowed", "test/loot_remote_control", map[string]any{})
	if err != nil {
		return err
	}
	if forbidden, _ := na.AsBool(allowed["forbidden"]); forbidden {
		return fmt.Errorf("remote loot remains forbidden after readiness rose: %v", allowed)
	}
	// Allow's receipt is insufficient: ordinary hauling must deliver the
	// stack to the prepared native stockpile, and its cell stays outside Home.
	for i := 0; i < 8; i++ {
		if _, err = s.Advance(ctx, 1000); err != nil {
			return err
		}
		stored, err := s.Harness().Call(ctx, fmt.Sprintf("loot-remote-stored-%d", i), "test/loot_remote_control", map[string]any{})
		if err != nil {
			return err
		}
		s.Report()["stored"] = stored
		spawned, _ := na.AsBool(stored["spawned"])
		inStockpile, _ := na.AsBool(stored["inStockpile"])
		inHome, _ := na.AsBool(stored["inHome"])
		if spawned && inHome && !inStockpile {
			return fmt.Errorf("remote loot cell joined Home: %v", stored)
		}
		if na.AsNumber(stored["storedSteel"]) >= na.AsNumber(drop["count"]) && (!spawned || inStockpile) {
			return nil
		}
	}
	return fmt.Errorf("allowed remote loot was not hauled to storage within 8000 ticks")
}
