package production

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

// production/stone (#231) is the ladder run for stone blocks: the operator
// names only a stone-block floor (--routine-stone-block-target), the review
// derives the block definition from the chunks the map counts most, and the
// ladder walks Stonecutting -> stonecutter's table -> do-until bill. The
// fixture stages the hut, the research bench, Stonecutting at 97% and the
// steel the table costs (the tribal save has none); the chunks are the
// map's own. The
// ingredient-storage family stays off: a chunk stockpile inside the hut is
// not a rung this case proves.
const (
	stoneProject = "Stonecutting"
	stoneBench   = "TableStonecutter"
	stoneTarget  = 40
)

const stoneFamilies = "temperature,comfort,work,supply,defense,tend,rescue,medical,field,food-storage,acquisition,cooking,production-policy,resource,workshop,research,gear,dialog,naming"

func init() {
	cases.Register(cases.Case{
		Name:   "production/stone",
		Scope:  fmt.Sprintf("--routine-stone-block-target %d walks research (%s) -> %s -> Make_StoneBlocks bill fed from map chunks; the live block count must rise above the pre-service baseline (#231).", stoneTarget, stoneProject, stoneBench),
		Start:  cases.Fixture{Op: "test/production_stone_prepare", Args: map[string]any{}, On: cases.Save{Name: baselineSave}},
		Serve:  &cases.ServeSpec{Families: []string{stoneFamilies}, NativeTimeout: 15 * time.Second, Prefix: "production", Extra: []string{"--routine-stone-block-target", fmt.Sprintf("%d", stoneTarget)}},
		Stages: []string{benchStage},
		Budget: benchWindow + window + 5*time.Minute,
		Reason: "the research rung and the table build are a cached stage (#329); the first bill iteration over map chunks after it runs on a miss and a hit alike",
		Run: func(ctx context.Context, s cases.Session) error {
			var baseline float64
			if err := s.Stage(ctx, benchStage, func(ctx context.Context) error {
				_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
					WatchConfig: sustainedfood.WatchConfig{Watch: benchWindow, Goal: policy.MaintainResource, Until: benchBuilt, FailFast: ladderFailFast},
					Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
						prepared := s.Prepared()
						report["fixture"] = prepared
						if finished, _ := na.AsBool(prepared["finished"]); finished {
							return fmt.Errorf("%s is already finished; the research rung has nothing to prove", stoneProject)
						}
						if len(stoneRows(prepared["chunks"])) == 0 {
							return fmt.Errorf("no stone chunks within reach of the colonists; the bill has nothing to cut")
						}
						before, err := h.Call(ctx, "baseline-audit", "test/production_stone_audit", map[string]any{})
						if err != nil {
							return err
						}
						baseline = stoneCount(before)
						report["baseline_count"] = baseline
						// The bill path reads the baseline back from the
						// bundle (cases.RestoredState) on a hit.
						na.SetCheckpointState(baselineKey, baseline)
						if baseline >= stoneTarget {
							return fmt.Errorf("save already holds %v stone blocks; the floor %d leaves no deficit to recover", baseline, stoneTarget)
						}
						if len(na.AsSlice(before["tables"])) != 0 {
							return fmt.Errorf("save already holds a stonecutter's table; the bench rung has nothing to prove")
						}
						return nil
					},
					Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
						live, err := h.Call(ctx, "bench-audit", "test/production_stone_audit", map[string]any{})
						if err != nil {
							return err
						}
						report["bench_stage"] = live
						if finished, _ := na.AsBool(live["finished"]); !finished {
							return fmt.Errorf("%s did not finish natively within the bench window: progress=%v current=%v", stoneProject, live["progress"], live["current"])
						}
						if len(na.AsSlice(live["tables"])) == 0 {
							return fmt.Errorf("no %s stands within the bench window (%v)", stoneBench, live)
						}
						return nil
					},
				})
				return err
			}); err != nil {
				return err
			}
			if restored := cases.RestoredState(s, baselineKey); restored != nil {
				baseline = na.AsNumber(restored)
				s.Report()["baseline_count"] = baseline
			}
			_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
				WatchConfig: sustainedfood.WatchConfig{Watch: window, Goal: policy.MaintainResource, Until: billProduced, FailFast: ladderFailFast},
				Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
					journal, err := store.Open(ctx, filepath.Join(s.Config().Output, "service.sqlite"))
					if err != nil {
						return fmt.Errorf("reopen journal: %w", err)
					}
					defer journal.Close()
					return auditStone(ctx, h, journal, report, baseline)
				},
			})
			return err
		},
	})
}

func stoneRows(raw any) []map[string]any {
	var rows []map[string]any
	for _, item := range na.AsSlice(raw) {
		row, _ := na.AsMap(item)
		rows = append(rows, row)
	}
	return rows
}

// stoneCount sums the live stone block count over every block definition.
func stoneCount(audit map[string]any) float64 {
	var total float64
	for _, row := range stoneRows(audit["blocks"]) {
		total += na.AsNumber(row["count"])
	}
	return total
}

func auditStone(ctx context.Context, h *na.Harness, journal *store.Store, report na.Report, baseline float64) error {
	review, err := journal.LoadRoutineReview(ctx)
	if err != nil {
		return fmt.Errorf("load routine review: %w", err)
	}
	ladder, ok, err := journal.LoadProductionLadder(ctx, store.World{Colony: review.Snapshot.Colony, Load: review.Snapshot.Load, Map: review.Snapshot.Map})
	if err != nil {
		return err
	}
	report["production_ladder"] = map[string]any{"recorded": ok, "resource": string(ladder.Resource), "bench": ladder.Bench, "recipe": ladder.Recipe, "research": ladder.Research}
	if !ok || !policy.StoneBlockResource(ladder.Resource) {
		return fmt.Errorf("the workshop ladder never recorded a stone block resource: %v", report["production_ladder"])
	}
	live, err := h.Call(ctx, "final-audit", "test/production_stone_audit", map[string]any{})
	if err != nil {
		return err
	}
	report["live"] = live
	if finished, _ := na.AsBool(live["finished"]); !finished {
		return fmt.Errorf("%s did not finish natively: progress=%v current=%v", stoneProject, live["progress"], live["current"])
	}
	workshop, err := policy.Facility(policy.RoomRoleWorkshop)
	if err != nil {
		return err
	}
	var hosted []map[string]any
	for _, row := range stoneRows(live["tables"]) {
		if !workshop.Hosts(policy.RoomRole(na.AsString(row["roomRole"]))) {
			continue
		}
		for _, bill := range na.AsSlice(row["bills"]) {
			if na.AsString(bill) == ladder.Recipe {
				hosted = append(hosted, row)
			}
		}
	}
	report["hosted_tables"] = hosted
	if len(hosted) == 0 {
		return fmt.Errorf("no %s inside a room hosting the Workshop facility carries a %s bill: %v", stoneBench, ladder.Recipe, live["tables"])
	}
	count := stoneCount(live)
	report["final_count"] = count
	if count <= baseline {
		return fmt.Errorf("stone block count did not rise: baseline=%v final=%v", baseline, count)
	}
	return nil
}
