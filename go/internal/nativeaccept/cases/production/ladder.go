// Package production holds issue #4 M4's multi-stage production case
// (production/ladder): open the workshop checkpoint save (a room whose
// native role hosts the Workshop facility already stands), run the service
// with a MaintainResource stock floor for an item only a research-gated
// bench produces (a gladius on a smithy), and watch the ladder walk
// research -> bench -> ingredient storage -> bill. The fixture seeds what
// the ladder does not build: steel and wood on the ground, a simple
// research bench, and Smithing research a few points short of done.
//
// Passing needs live native evidence, never the journal alone: Smithing
// finished natively, a smithy standing in a Workshop-hosting room carrying
// the gladius bill, an allow-list stockpile for its ingredients inside that
// room, and the live gladius count above the pre-service baseline.
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

// checkpointSave is the workshop checkpoint (facility/workshop's bench
// standing in a Workshop-hosting room) the ladder resumes from.
const checkpointSave = "RimGovernor-tribal8-workshop"

const (
	resource = "MeleeWeapon_Gladius"
	recipe   = "Make_MeleeWeapon_Gladius"
	project  = "Smithing"
	target   = 1
)

// ladderFamilies is facility/workshop's composition plus the research and
// ingredient-storage rungs M4 adds, and the emergency responders: an injury
// (a social fight is enough) holds every development goal until tended, a
// predator hunting a colonist holds the clock until defense answers it, and
// a choice dialog the DLC save opens by itself (Verse.Dialog_NodeTree)
// force-pauses the game and holds every development row as an emergency
// until the dialog planner answers it (#156). The sleeping and shelter
// families (both serve EnsureInitialShelter with sleeping spots) stay off
// for time: with them on, eight sleeping spots fill the checkpoint hut and
// the workshop ladder stages a second shell before its bench (#218, a 16
// minute run whose ingredient stockpile then has no clean floor, #223). An
// unserved priority-2 shelter goal gates only comfort, never
// MaintainResource.
const ladderFamilies = "temperature,comfort,work,supply,defense,tend,rescue,medical,field,food-storage,acquisition,cooking,production-policy,resource,workshop,research,ingredient-storage,gear,dialog,naming"

// window is how long the ladder gets to land its first product: research
// and a bench build are waits in ticks, so the window ends early on the
// first observed bill iteration.
const window = 25 * time.Minute

func init() {
	cases.Register(cases.Case{
		Name:   "production/ladder",
		Scope:  fmt.Sprintf("MaintainResource %s:%d walks research (%s) -> smithy -> ingredient stockpile -> bill; the live item count must rise above the pre-service baseline (issue #4, M4).", resource, target, project),
		Start:  cases.Fixture{Op: "test/production_ladder_prepare", Args: map[string]any{}, On: cases.Save{Name: checkpointSave}},
		Keep:   []string{string(na.NeedFood)},
		Serve:  &cases.ServeSpec{Families: []string{ladderFamilies}, NativeTimeout: 15 * time.Second, Prefix: "production", Extra: []string{"--routine-resource-target", fmt.Sprintf("%s:%d", resource, target)}},
		Budget: window + 15*time.Minute,
		Reason: "four dependent rungs (research completion, a bench build, a stockpile and a bill iteration) are one native campaign on the workshop checkpoint; the watch ends on the first product",
		Run: func(ctx context.Context, s cases.Session) error {
			var baseline float64
			_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
				WatchConfig: sustainedfood.WatchConfig{Watch: window, Goal: policy.MaintainResource, Until: billProduced},
				Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
					prepared := s.Prepared()
					report["fixture"] = prepared
					if finished, _ := na.AsBool(prepared["finished"]); finished {
						return fmt.Errorf("%s is already finished; the research rung has nothing to prove", project)
					}
					before, err := h.Call(ctx, "baseline-audit", "test/production_ladder_audit", map[string]any{})
					if err != nil {
						return err
					}
					baseline = gladiusCount(before)
					report["baseline_count"] = baseline
					if baseline >= target {
						return fmt.Errorf("save already holds %v %s; the stock floor %d leaves no deficit to recover", baseline, resource, target)
					}
					if len(smithies(before)) != 0 {
						return fmt.Errorf("save already holds a smithy; the bench rung has nothing to prove")
					}
					return nil
				},
				Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
					journal, err := store.Open(ctx, filepath.Join(s.Config().Output, "service.sqlite"))
					if err != nil {
						return fmt.Errorf("reopen journal: %w", err)
					}
					defer journal.Close()
					return audit(ctx, h, journal, report, baseline)
				},
			})
			return err
		},
	})
}

// billProduced reports a sample whose MaintainResource goal holds or held a
// production bill plan with every action completed: the first product landed.
func billProduced(sample map[string]any) bool {
	plans, _ := sample["plans"].([]map[string]any)
	retired, _ := sample["retired_plans"].([]map[string]any)
	for _, plan := range append(plans, retired...) {
		id, _ := plan["plan"].(string)
		actions, _ := plan["actions"].(int)
		stages, _ := plan["stages"].(map[string]int)
		if strings.HasPrefix(id, "routine-resource-") && actions > 0 && stages["completed"] == actions {
			return true
		}
	}
	return false
}

func gladiusCount(audit map[string]any) float64 {
	return na.AsNumber(audit["gladiusGround"]) + na.AsNumber(audit["gladiusCarried"])
}

func smithies(audit map[string]any) []map[string]any {
	var rows []map[string]any
	for _, raw := range na.AsSlice(audit["smithies"]) {
		row, _ := na.AsMap(raw)
		rows = append(rows, row)
	}
	return rows
}

// audit compares the journal's goals with the live research state, bench,
// stockpile and item count after the service has stopped.
func audit(ctx context.Context, h *na.Harness, journal *store.Store, report na.Report, baseline float64) error {
	review, err := journal.LoadRoutineReview(ctx)
	if err != nil {
		return fmt.Errorf("load routine review: %w", err)
	}
	goals := map[policy.GoalID]domain.GoalID{}
	for _, binding := range review.Goals {
		goals[binding.Need] = binding.Goal
	}
	for _, need := range []policy.GoalID{policy.MaintainResource, policy.EnsureResearch} {
		id, ok := goals[need]
		if !ok {
			return fmt.Errorf("%s was never bound in the routine review", need)
		}
		goal, err := journal.LoadGoal(ctx, id)
		if err != nil {
			return err
		}
		report[strings.ToLower(string(need))+"_goal"] = map[string]any{"status": string(goal.Goal.Status), "need": string(goal.Goal.Need), "methods": len(goal.Methods)}
	}
	ladder, ok, err := journal.LoadProductionLadder(ctx, store.World{Colony: review.Snapshot.Colony, Load: review.Snapshot.Load, Map: review.Snapshot.Map})
	if err != nil {
		return err
	}
	report["production_ladder"] = map[string]any{"recorded": ok, "resource": string(ladder.Resource), "bench": ladder.Bench, "recipe": ladder.Recipe, "research": ladder.Research}

	live, err := h.Call(ctx, "final-audit", "test/production_ladder_audit", map[string]any{})
	if err != nil {
		return err
	}
	report["live"] = live
	if finished, _ := na.AsBool(live["finished"]); !finished {
		return fmt.Errorf("%s did not finish natively: progress=%v current=%v", project, live["progress"], live["current"])
	}
	workshop, err := policy.Facility(policy.RoomRoleWorkshop)
	if err != nil {
		return err
	}
	var hosted []map[string]any
	for _, row := range smithies(live) {
		if !workshop.Hosts(policy.RoomRole(na.AsString(row["roomRole"]))) {
			continue
		}
		for _, bill := range na.AsSlice(row["bills"]) {
			if na.AsString(bill) == recipe {
				hosted = append(hosted, row)
			}
		}
	}
	report["hosted_smithies"] = hosted
	if len(hosted) == 0 {
		return fmt.Errorf("no smithy inside a room hosting the Workshop facility carries a %s bill: %v", recipe, live["smithies"])
	}
	var stockpiles []map[string]any
	for _, raw := range na.AsSlice(live["stockpiles"]) {
		row, _ := na.AsMap(raw)
		allowsSteel, _ := na.AsBool(row["allowsSteel"])
		if allowsSteel && workshop.Hosts(policy.RoomRole(na.AsString(row["roomRole"]))) && na.AsNumber(row["allowedCount"]) <= 16 {
			stockpiles = append(stockpiles, row)
		}
	}
	report["ingredient_stockpiles"] = stockpiles
	if len(stockpiles) == 0 {
		return fmt.Errorf("no allow-list stockpile for steel stands inside a room hosting the Workshop facility: %v", live["stockpiles"])
	}
	count := gladiusCount(live)
	report["final_count"] = count
	if count <= baseline {
		return fmt.Errorf("%s count did not rise: baseline=%v final=%v", resource, baseline, count)
	}
	return nil
}
