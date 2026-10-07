// Package production holds issue #4 M4's multi-stage production case
// (production/ladder): on the Core tribal baseline, the service's default
// component floor (policy.DefaultResourceTargets, #875) asks for an item
// only a research-gated bench produces (components on a fabrication bench),
// and the ladder walks research -> bench -> workstation stockpile -> bill. The
// fixture stages what the ladder does not build: a roofed starter hut whose
// native room role hosts the Workshop facility (with a sleeping spot per
// colonist, so the initial shelter is met), a simple research bench and a
// fueled generator inside it, steel, wood and the bench's components loose
// beside its door (outside storage, so the stock reads 0), and Fabrication
// research a few points short of done (#344).
//
// Passing needs live native evidence, never the journal alone: Fabrication
// finished natively, a fabrication bench standing in a Workshop-hosting room
// carrying the component bill, an allow-list stockpile for its ingredients
// inside that room, and the stored component count above the pre-service
// baseline.
package production

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"path/filepath"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/startersite"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// baselineSave is the committed Core tribal start both production cases
// open; their fixtures stage the workshop room on it.
const baselineSave = sustained.BaselineSave

const (
	resource = policy.ComponentResource
	recipe   = "Make_ComponentIndustrial"
	project  = "Fabrication"
	// target is policy.DefaultResourceTargets()'s component floor (#875).
	target = 10
)

// ladderFamilies is facility/workshop's composition plus the research and
// stockpiles (the workstation stockpile rung) M4 adds, and the emergency responders: an injury
// (a social fight is enough) holds every development goal until tended, a
// predator hunting a colonist holds the clock until defense answers it, and
// a choice dialog the DLC save opens by itself (Verse.Dialog_NodeTree)
// force-pauses the game and holds every development row as an emergency
// until the dialog planner answers it (#156). The sleeping and shelter
// families (both serve MaintainHousing with sleeping spots) stay off
// for time: the fixture hut already holds a sleeping spot per colonist, and
// with them on the workshop ladder once staged a second shell before its
// bench (#218, a 16 minute run whose ingredient stockpile then had no clean
// floor, #223). An unserved priority-2 shelter goal gates only comfort,
// never MaintainResource.
var ladderFamilies = []routinefamily.Family{routinefamily.Temperature, routinefamily.Comfort, routinefamily.Work, routinefamily.Power, routinefamily.Supply, routinefamily.Defense, routinefamily.Tend, routinefamily.Rescue, routinefamily.Medical, routinefamily.Field, routinefamily.Stockpiles, routinefamily.Acquisition, routinefamily.Cooking, routinefamily.Resource, routinefamily.Workshop, routinefamily.Research, routinefamily.Gear, routinefamily.Armory, routinefamily.Dialog}

// benchWindow is how long the ladder gets to finish the research rung and
// raise its bench (the "bench-built" stage, cached across runs, #329);
// window is how long the staged colony then gets to land its first
// product. Each watch ends early on its rung.
const (
	benchWindow = 12 * time.Minute
	window      = 13 * time.Minute
)

// benchStage is the production cases' one stage: the research finished and
// the bench standing in the fixture hut, before any bill is placed on it.
// The bench is proven the service's at the stage boundary: a hit reloads
// the world and the review rebinds fresh goals, so the journal after it
// cannot.
const benchStage = "bench-built"

// ladderFailFast keeps the watch's fail-fast on but lets MaintainResource
// sit method_unavailable through the research rung: while the project the
// workshop recorded as gating the bench is unfinished the goal holds no
// method of its own by design (policy.RoundsFindings), so those reviews are
// not the planner committing nothing under a slot it was handed.
//
// MaintainResource also keeps other resources (medicine) stocked, and one of
// those acquisitions falling short (outcome_not_achieved) is retried, not
// the ladder failing (#1136); so is an acquisition native refused on a stale
// census (#1361).
var ladderFailFast = sustainedfood.FailFast{
	MethodUnavailableWaits: true,
	RetryableUnsuccessful:  []domain.UnsuccessfulReason{domain.OutcomeNotAchieved, domain.IntentRefused},
}

func init() {
	cases.Register(cases.Case{
		Name:   "production/ladder",
		Scope:  fmt.Sprintf("MaintainResource %s:%d walks research (%s) -> fabrication bench -> ingredient stockpile -> bill; the live item count must rise above the pre-service baseline (issue #4, M4).", resource, target, project),
		Start:  cases.Fixture{Op: "test/production_ladder_prepare", ArgsFrom: startersite.ArgsFor(11), Args: map[string]any{}, On: cases.Save{Name: baselineSave}},
		Serve:  &cases.ServeSpec{Families: ladderFamilies, NativeTimeout: 15 * time.Second, Prefix: "production"},
		Stages: []string{benchStage},
		Budget: benchWindow + window + 5*time.Minute,
		Reason: "the research rung and the bench build are a cached stage (#329); the stockpile and the first bill iteration after it run on a miss and a hit alike",
		Run: func(ctx context.Context, s cases.Session) error {
			if err := s.Stage(ctx, benchStage, func(ctx context.Context) error {
				_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
					WatchConfig: sustainedfood.WatchConfig{Watch: benchWindow, Concern: policy.MaintainResource, Until: benchBuilt, FailFast: ladderFailFast},
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
						baseline := productCount(before)
						report["baseline_count"] = baseline
						// The bill path and the postmortem read the baseline
						// back from the bundle (cases.RestoredState) on a hit
						// or a -postmortem-only rerun.
						na.SetCheckpointState(baselineKey, baseline)
						if baseline >= target {
							return fmt.Errorf("save already holds %v %s; the stock floor %d leaves no deficit to recover", baseline, resource, target)
						}
						if len(benches(before)) != 0 {
							return fmt.Errorf("save already holds a fabrication bench; the bench rung has nothing to prove")
						}
						return nil
					},
					Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
						live, err := h.Call(ctx, "bench-audit", "test/production_ladder_audit", map[string]any{})
						if err != nil {
							return err
						}
						report["bench_stage"] = live
						if finished, _ := na.AsBool(live["finished"]); !finished {
							return fmt.Errorf("%s did not finish natively within the bench window: progress=%v current=%v", project, live["progress"], live["current"])
						}
						if len(benches(live)) == 0 {
							return fmt.Errorf("no fabrication bench stands within the bench window (%v)", live)
						}
						return nil
					},
				})
				return err
			}); err != nil {
				return err
			}
			if restored := cases.RestoredState(s, baselineKey); restored != nil {
				s.Report()["baseline_count"] = na.AsNumber(restored)
			}
			_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
				WatchConfig: sustainedfood.WatchConfig{Watch: window, Concern: policy.MaintainResource, Until: billProduced, FailFast: ladderFailFast},
			})
			return err
		},
		Postmortem: func(ctx context.Context, s cases.Session) error {
			report := s.Report()
			baseline, ok := report["baseline_count"].(float64)
			if !ok {
				restored := cases.RestoredState(s, baselineKey)
				if restored == nil {
					return fmt.Errorf("no %s in the report or the bundle's state: the prepare phase never ran", baselineKey)
				}
				baseline = na.AsNumber(restored)
				report["baseline_count"] = baseline
			}
			journal, err := store.Open(ctx, filepath.Join(s.Config().Output, "service.sqlite"))
			if err != nil {
				return fmt.Errorf("reopen journal: %w", err)
			}
			defer journal.Close()
			return audit(ctx, s.Harness(), journal, report, baseline)
		},
	})
}

// baselineKey is the checkpoint state key the prepare phase records the
// pre-service component count under, for the postmortem of a later run.
const baselineKey = "baseline_count"

// billProduced reports a sample whose MaintainResource goal holds or held a
// production bill plan with every action completed: the first product landed.
func billProduced(sample map[string]any) bool {
	return planCompleted(sample, func(_ string, kinds map[string]int, actions int) bool {
		return kinds[string(domain.ProductionBillAction)] == actions
	})
}

// benchBuilt reports a sample whose MaintainResource goal holds or held
// (retired_plans: a completed method leaves the goal at the next review) a
// workshop bench plan with every action completed: the bench stands.
func benchBuilt(sample map[string]any) bool {
	return planCompleted(sample, func(method string, _ map[string]int, _ int) bool {
		return strings.HasPrefix(method, "workshop-") && !strings.HasPrefix(method, "workshop-shell")
	})
}

// planCompleted reports a plan match accepts (by its method, action kinds
// and count; #987), active or retired, with every action completed.
func planCompleted(sample map[string]any, match func(method string, kinds map[string]int, actions int) bool) bool {
	plans, _ := sample["plans"].([]map[string]any)
	retired, _ := sample["retired_plans"].([]map[string]any)
	for _, plan := range append(plans, retired...) {
		method, _ := plan["method"].(string)
		kinds, _ := plan["kinds"].(map[string]int)
		actions, _ := plan["actions"].(int)
		stages, _ := plan["stages"].(map[string]int)
		if actions > 0 && match(method, kinds, actions) && stages["completed"] == actions {
			return true
		}
	}
	return false
}

func productCount(audit map[string]any) float64 { return na.AsNumber(audit["components"]) }
func benches(audit map[string]any) []map[string]any {
	var rows []map[string]any
	for _, raw := range na.AsSlice(audit["benches"]) {
		row, _ := na.AsMap(raw)
		rows = append(rows, row)
	}
	return rows
}

// audit compares the journal's goals with the live research state, bench,
// stockpile and item count after the service has stopped.
func audit(ctx context.Context, h *na.Harness, journal *store.Store, report na.Report, baseline float64) error {
	review, err := journal.LoadRounds(ctx)
	if err != nil {
		return fmt.Errorf("load rounds: %w", err)
	}
	goals := map[policy.ConcernID]domain.ConcernID{}
	for _, binding := range review.Standards {
		goals[binding.Concern] = binding.Standard
	}
	id, ok := goals[policy.MaintainResource]
	if !ok {
		return fmt.Errorf("%s was never bound in the rounds", policy.MaintainResource)
	}
	goal, err := journal.LoadStandard(ctx, id)
	if err != nil {
		return err
	}
	report[strings.ToLower(string(policy.MaintainResource))+"_standard"] = map[string]any{"status": string(goal.Standard.Status), "need": string(goal.Standard.Finding), "methods": len(goal.Methods)}
	// EnsureResearch is a Project (#1911): its own row, not a goal.
	projectID, ok := review.ProjectFor(policy.EnsureResearch)
	if !ok {
		return fmt.Errorf("%s was never bound in the rounds", policy.EnsureResearch)
	}
	research, err := journal.LoadProject(ctx, projectID)
	if err != nil {
		return err
	}
	report[strings.ToLower(string(policy.EnsureResearch))+"_standard"] = map[string]any{"status": string(research.Project.Status), "need": string(research.Project.Finding), "methods": len(research.Methods)}
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
	for _, row := range benches(live) {
		if !workshop.Hosts(policy.RoomRole(na.AsString(row["roomRole"]))) {
			continue
		}
		for _, bill := range na.AsSlice(row["bills"]) {
			if na.AsString(bill) == recipe {
				hosted = append(hosted, row)
			}
		}
	}
	report["hosted_benches"] = hosted
	if len(hosted) == 0 {
		return fmt.Errorf("no fabrication bench inside a room hosting the Workshop facility carries a %s bill: %v", recipe, live["benches"])
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
	count := productCount(live)
	report["final_count"] = count
	if count <= baseline {
		return fmt.Errorf("%s count did not rise: baseline=%v final=%v", resource, baseline, count)
	}
	return nil
}
