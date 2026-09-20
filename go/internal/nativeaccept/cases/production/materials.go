package production

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
	for _, scenario := range []string{"deepdrill", "components"} {
		cases.Register(cases.Case{
			Name:        "production/" + scenario,
			Scope:       "Recover a material runway deficit through ordinary pawn production; native stock must rise, with deep steel depletion or a MakeComponent bill proving its source.",
			Start:       cases.Fixture{Op: "test/production_materials_prepare", Args: map[string]any{"scenario": scenario}, On: cases.Save{Name: baselineSave}},
			RequiredOps: []string{"test/production_materials_audit"},
			Serve:       &cases.ServeSpec{Families: []string{"work,resource,production-policy,power"}, NativeTimeout: 15 * time.Second, Prefix: scenario, Extra: []string{"--routine-resource-target", "Steel:250", "--routine-resource-target", "ComponentIndustrial:10"}},
			QuietWorld:  true,
			// The six-minute production window starts fresh so its native
			// stock baseline and staged history always describe the same run.
			NoCheckpoint: true,
			Budget:       8 * time.Minute,
			Reason:       "Nightly only: native drill construction and extraction or component fabrication exceed the smoke budget; prerequisites and runway history are staged.",
			Run:          func(ctx context.Context, s cases.Session) error { return runMaterials(ctx, s, scenario) },
		})
	}
}

func runMaterials(ctx context.Context, s cases.Session, scenario string) error {
	before, err := s.Harness().Call(ctx, "materials-baseline", "test/production_materials_audit", map[string]any{})
	if err != nil {
		return err
	}
	if err := materialPrecondition(before, scenario); err != nil {
		return err
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
	resource := policy.Resource("Steel")
	key := "steel"
	if scenario == "components" {
		resource, key = "ComponentIndustrial", "components"
	}
	baseline := na.AsNumber(before[key])
	_, err = sustainedfood.Observe(ctx, s, sustainedfood.Observation{
		WatchConfig: sustainedfood.WatchConfig{Watch: 6 * time.Minute, Goal: policy.MaintainResource,
			// A running native drill has no pending controller action after its
			// construction. Clock stalls remain bounded by the shared watcher.
			FailFast: sustainedfood.FailFast{Disabled: true},
			Until: func(map[string]any) bool {
				review, err := journal.LoadRoutineReview(ctx)
				if err != nil {
					return false
				}
				for _, row := range review.ResourceRunwayState() {
					if stock, known := row.Stock.Value(); row.Resource == resource && known && float64(stock) > baseline {
						return true
					}
				}
				return false
			},
		},
		Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
			after, err := h.Call(ctx, "materials-final", "test/production_materials_audit", map[string]any{})
			if err != nil {
				return err
			}
			report["final"] = after
			return materialOutcome(before, after, scenario)
		},
	})
	return err
}

func materialPrecondition(row map[string]any, scenario string) error {
	for _, key := range []string{"steel", "components", "deepSteel", "surfaceOre", "drills", "componentBills", "tick"} {
		if _, ok := row[key].(float64); !ok {
			return fmt.Errorf("missing native %s count", key)
		}
	}
	if na.AsNumber(row["surfaceOre"]) != 0 || na.AsNumber(row["drills"]) != 0 || na.AsNumber(row["componentBills"]) != 0 {
		return fmt.Errorf("fixture already has an alternative source or production order: %v", row)
	}
	if ready, _ := na.AsBool(row["researched"]); !ready {
		return fmt.Errorf("material research not staged: %v", row)
	}
	if scenario == "deepdrill" {
		if na.AsNumber(row["steel"]) < 100 || na.AsNumber(row["steel"]) >= 250 || na.AsNumber(row["components"]) < 3 || na.AsNumber(row["deepSteel"]) <= 0 || na.AsNumber(row["scanners"]) != 1 {
			return fmt.Errorf("deep drill fixture lacks funded construction, scanner, lump or deficit: %v", row)
		}
	} else if na.AsNumber(row["steel"]) < 370 || na.AsNumber(row["components"]) >= 10 || na.AsNumber(row["benches"]) != 1 {
		return fmt.Errorf("component fixture lacks funded steel, bench or deficit: %v", row)
	}
	return nil
}

func materialOutcome(before, after map[string]any, scenario string) error {
	for _, row := range []map[string]any{before, after} {
		for _, key := range []string{"steel", "components", "deepSteel"} {
			if count, ok := row[key].(float64); !ok || count < 0 {
				return fmt.Errorf("missing or invalid native %s count: %v", key, row)
			}
		}
	}
	key := "steel"
	if scenario == "components" {
		key = "components"
	}
	count, ok := after[key].(float64)
	if !ok || count <= na.AsNumber(before[key]) {
		return fmt.Errorf("native %s stock did not rise: before=%v after=%v", key, before, after)
	}
	if scenario == "deepdrill" {
		remaining, known := after["deepSteel"].(float64)
		if !known || remaining >= na.AsNumber(before["deepSteel"]) || na.AsNumber(after["drillsOnLump"]) < 1 {
			return fmt.Errorf("steel increase lacks drilled-lump depletion: %v", after)
		}
	} else if na.AsNumber(after["componentBills"]) < 1 || na.AsNumber(after["steel"]) >= na.AsNumber(before["steel"]) {
		return fmt.Errorf("component increase lacks MakeComponent bill and steel consumption: %v", after)
	}
	return nil
}

// Stage an inactive historical plan through the store API. The fixture clock
// starts at day two, so the forecast has a full day of zero consumption and
// derives a deficit from the configured reserve without playing a day first.
func seedMaterialHistory(ctx context.Context, journal *store.Store, identity map[string]any, tick domain.Tick) error {
	if tick < 60000 {
		return fmt.Errorf("fixture tick %d cannot hold a day of runway history", tick)
	}
	b, err := domain.NewBuilding("Wall", domain.Cell{X: 1, Z: 1}, domain.North, "WoodLog")
	if err != nil {
		return err
	}
	a, err := domain.NewBuildingAction("materials-history-action", b)
	if err != nil {
		return err
	}
	p, err := domain.NewPlan("materials-history", 1, []domain.Action{a})
	if err != nil {
		return err
	}
	if err = journal.CreatePlan(ctx, p); err != nil {
		return err
	}
	snapshot := domain.GenerationSnapshot{Colony: domain.ColonyID(na.AsString(identity["colonyId"])), Load: domain.LoadID(na.AsString(identity["loadToken"])), Map: domain.MapID(na.AsNumber(identity["mapId"])), Plan: "materials-history", Revision: 1}
	_, err = journal.ReserveAndPrepare(ctx, "materials-history", "materials-history-action", store.Admission{Snapshot: snapshot, Tick: tick - 60000, Costs: []store.MaterialCost{}, Footprint: []domain.Cell{{X: 1, Z: 1}}})
	if err != nil {
		return err
	}
	_, err = journal.Cancel(ctx, "materials-history", "materials-history-action")
	return err
}
