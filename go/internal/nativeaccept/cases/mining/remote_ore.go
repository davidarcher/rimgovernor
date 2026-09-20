package mining

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
		Name:        "mining/remote_ore",
		Scope:       "A demanded far-edge steel lump is mined and hauled into base storage; after delivery a 2500-tick wait and fresh service review leave remaining rocks and a foreign designation untouched.",
		Start:       cases.Fixture{Op: "test/mining_remote_prepare", On: cases.FlatDebugStart()},
		RequiredOps: []string{"test/mining_remote_observe"},
		Quiet:       na.QuietRequired, QuietWorld: true, Budget: 4 * time.Minute,
		Serve: &cases.ServeSpec{Families: []string{"resource"}, NativeTimeout: 30 * time.Second,
			Extra: []string{"--routine-resource-target", "Steel:20"}},
		Run: runRemoteOre,
	})
}

func runRemoteOre(ctx context.Context, s cases.Session) error {
	journal, err := store.Open(ctx, filepath.Join(s.Config().Output, "service.sqlite"))
	if err != nil {
		return err
	}
	defer journal.Close()
	completed := false
	_, err = sustainedfood.Observe(ctx, s, sustainedfood.Observation{WatchConfig: sustainedfood.WatchConfig{
		Watch: 2 * time.Minute, Goal: policy.MaintainResource,
		Until: func(sample map[string]any) bool {
			plans, err := journal.LoadPlans(ctx, 256)
			if err != nil {
				return false
			}
			for _, plan := range plans {
				for _, action := range plan.Spec.Actions() {
					if action.Kind() != domain.MineAcquisitionAction {
						continue
					}
					for _, progress := range plan.Progress {
						if progress.View().Stage == domain.Completed {
							completed = true
							return true
						}
					}
				}
			}
			return false
		},
	}})
	if err != nil {
		return err
	}
	if !completed {
		return fmt.Errorf("no completed mine acquisition")
	}
	read := func(label string, delivered bool) error {
		row, err := s.Harness().Call(ctx, label, "test/mining_remote_observe", map[string]any{})
		if err != nil {
			return err
		}
		s.Report()[label] = row
		preserved, _ := na.AsBool(row["foreignPreserved"])
		if !preserved || na.AsNumber(row["removed"]) != 1 || na.AsNumber(row["ownedRecords"]) != 1 || na.AsNumber(row["designated"]) != 0 {
			return fmt.Errorf("mining exceeded demand or changed foreign work: %v", row)
		}
		if delivered && na.AsNumber(row["storedSteel"]) < 20 {
			return fmt.Errorf("steel not delivered: %v", row)
		}
		return nil
	}
	if err = read("mined", false); err != nil {
		return err
	}
	// Once the resource goal is satisfied the service has no work to fund a
	// clock window. Advance native hauling explicitly and observe delivery.
	for i := 0; i < 8; i++ {
		if _, err = s.Advance(ctx, 1000); err != nil {
			return err
		}
		if err = read("delivery", true); err == nil {
			break
		}
	}
	if err != nil {
		return err
	}
	if _, err = s.Advance(ctx, 2500); err != nil {
		return err
	}
	_, err = sustainedfood.Observe(ctx, s, sustainedfood.Observation{WatchConfig: sustainedfood.WatchConfig{
		Watch: 10 * time.Second, Goal: policy.MaintainResource, FailFast: sustainedfood.FailFast{Disabled: true},
	}})
	if err != nil {
		return err
	}
	return read("after_wait", true)
}
