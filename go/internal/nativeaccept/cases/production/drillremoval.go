package production

import (
	"context"
	"fmt"
	"path/filepath"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const drillRemovalPrefix = "routine-drill-removal-"

func init() {
	cases.Register(cases.Case{
		Name:        "production/drillremoval",
		Scope:       "Remove an exhausted deep drill through the Hands deconstruction path while a steel runway is in deficit (#538); the drill over barren ground must be designated and demolished by pawns, the scanner and seeded lump untouched.",
		Start:       cases.Fixture{Op: "test/production_materials_prepare", Args: map[string]any{"scenario": "exhausted"}, On: cases.Save{Name: baselineSave}},
		RequiredOps: []string{"test/production_materials_audit"},
		Serve:       &cases.ServeSpec{Families: []string{"work,resource,production-policy,power"}, NativeTimeout: 15 * time.Second, Prefix: "exhausted", Extra: []string{"--routine-resource-target", "Steel:250"}},
		QuietWorld:  true,
		// The removal runs fresh so the staged runway history and the single
		// removal plan always describe the same run.
		NoCheckpoint: true,
		Budget:       4 * time.Minute,
		Run:          runDrillRemoval,
	})
}

func runDrillRemoval(ctx context.Context, s cases.Session) error {
	before, err := s.Harness().Call(ctx, "removal-baseline", "test/production_materials_audit", map[string]any{})
	if err != nil {
		return err
	}
	if ready, _ := na.AsBool(before["researched"]); !ready || na.AsNumber(before["steel"]) >= 250 {
		return fmt.Errorf("exhausted fixture lacks staged research or a steel deficit: %v", before)
	}
	if na.AsNumber(before["drills"]) != 1 || na.AsNumber(before["drillsOnLump"]) != 0 || na.AsNumber(before["drillsDesignated"]) != 0 || na.AsNumber(before["surfaceOre"]) != 0 || na.AsNumber(before["scanners"]) != 1 || na.AsNumber(before["deepSteel"]) <= 0 {
		return fmt.Errorf("exhausted fixture lacks one undesignated barren drill beside the seeded lump: %v", before)
	}
	s.Report()["baseline"] = before
	journal, err := store.Open(ctx, filepath.Join(s.Config().Output, "service.sqlite"))
	if err != nil {
		return err
	}
	defer journal.Close()
	if err := seedMaterialHistory(ctx, journal, s.Identity(), domain.Tick(na.AsNumber(before["tick"]))); err != nil {
		return err
	}
	var removal domain.PlanID
	_, err = sustainedfood.Observe(ctx, s, sustainedfood.Observation{
		WatchConfig: sustainedfood.WatchConfig{Watch: 3 * time.Minute, Goal: policy.MaintainResource,
			FailFast: sustainedfood.FailFast{Disabled: true},
			// The window ends once the removal plan's Deconstruction completes
			// or, having been seen, leaves the live catalog on retirement; the
			// audit then proves the demolition natively.
			Until: func(map[string]any) bool {
				plans, err := journal.LoadPlans(ctx, 256)
				if err != nil {
					return false
				}
				present := false
				for _, plan := range plans {
					if !strings.HasPrefix(string(plan.Spec.ID()), drillRemovalPrefix) {
						continue
					}
					removal, present = plan.Spec.ID(), true
					for _, progress := range plan.Progress {
						if progress.View().Stage == domain.Completed {
							return true
						}
					}
				}
				return removal != "" && !present
			},
		},
		Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
			after, err := h.Call(ctx, "removal-final", "test/production_materials_audit", map[string]any{})
			if err != nil {
				return err
			}
			report["final"], report["removal_plan"] = after, removal
			if removal == "" {
				return fmt.Errorf("no %s plan was admitted: %v", drillRemovalPrefix, after)
			}
			// A replacement drill may already stand on the lump; the barren one
			// must be gone.
			if na.AsNumber(after["drills"]) != na.AsNumber(after["drillsOnLump"]) || na.AsNumber(after["scanners"]) != 1 || na.AsNumber(after["deepSteel"]) != na.AsNumber(before["deepSteel"]) {
				return fmt.Errorf("exhausted drill not demolished with scanner and lump intact: before=%v after=%v", before, after)
			}
			return nil
		},
	})
	return err
}
