// The research/ladder case proves the default research roadmap (#230) and
// the bench it stands on (#254): on the Core tribal baseline, with no
// --routine-research-target, no workshop need and no research bench, the
// service stages a simple research bench in the fixture's starter hut,
// selects the ladder's first unfinished rung once the bench stands, lends
// the clock ticks until the game finishes it, and then selects the next.
// The fixture stages the hut (a roofed ring with a sleeping spot per
// colonist, as the basic comfort case does) because the bench rung waits
// behind the initial shelter and raising the shell from the baseline takes
// the whole window; the facility startup checkpoint would serve the same
// but its colony facts sit at the 1 MiB read limit (#320). The fixture
// seeds only Stonecutting a few points short of done so Electricity is
// reached within a minute-scale watch.
//
// Passing needs live native evidence: a research bench the service built,
// Stonecutting finished natively and Electricity the current native project,
// never the journal alone.
package research

import (
	"context"
	"fmt"
	"path/filepath"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	baselineSave = "RimGovernor-tribal8-baseline"
	firstRung    = "Stonecutting"
	secondRung   = "Electricity"
)

// ladderFamilies is the research family with the survival responders a
// serve-driven baseline run needs (see production/ladder): the work family
// assigns the researcher, supply keeps wood for the bench, the emergency
// families keep an injury or a predator from holding the development
// ranking, and the dialog family answers a choice dialog the save may open
// by itself. The research family carries its own building ladder for the
// bench; the sleeping and shelter families stay out so nothing else
// furnishes the fixture hut.
const ladderFamilies = "temperature,work,supply,defense,tend,rescue,medical,field,food-storage,acquisition,cooking,research,dialog,naming"

// benchWindow is how long the roadmap gets to build the bench (the
// "bench-built" stage, cached across runs, #329); ladderWindow is how long
// the staged colony then gets to finish the seeded rung and select the
// next (a restarted service re-ranks development for ~15k ticks before
// the first selection). Each watch ends early on its rung.
const (
	benchWindow  = 6 * time.Minute
	ladderWindow = 10 * time.Minute
)

// benchStage is the case's one stage: the research bench standing in the
// fixture hut, before any rung is selected on it.
const benchStage = "bench-built"

func init() {
	cases.Register(cases.Case{
		Name:   "research/ladder",
		Scope:  fmt.Sprintf("With no research target and no research bench, EnsureResearch stages a simple research bench in the fixture hut, selects %s from the default ladder, keeps the clock moving until it finishes natively, then selects %s; all proven by the live research state (issues #230, #254).", firstRung, secondRung),
		Start:  cases.Fixture{Op: "test/research_ladder_prepare", Args: map[string]any{"project": firstRung}, On: cases.Save{Name: baselineSave}},
		Serve:  &cases.ServeSpec{Families: []string{ladderFamilies}, NativeTimeout: 15 * time.Second, Prefix: "research"},
		Stages: []string{benchStage},
		Budget: benchWindow + ladderWindow + 3*time.Minute,
		Reason: "the bench build is a cached stage (#329) and the watch after it pays a fresh development ranking before the second selection; a hit runs only the second watch",
		Run: func(ctx context.Context, s cases.Session) error {
			// The bench: the service runs until the research goal has
			// completed a laboratory plan, then stops; the stage bundle
			// holds the colony there. This is where the bench is proven
			// the service's: a hit reloads the world and the review
			// rebinds fresh goals, so the journal after it cannot.
			if err := s.Stage(ctx, benchStage, func(ctx context.Context) error {
				_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
					WatchConfig: sustainedfood.WatchConfig{Watch: benchWindow, Goal: policy.EnsureResearch, Until: benchPlanCompleted},
					Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
						prepared := s.Prepared()
						report["fixture"] = prepared
						if finished, _ := na.AsBool(prepared["finished"]); finished {
							return fmt.Errorf("%s is already finished; the first rung has nothing to prove", firstRung)
						}
						if current := na.AsString(prepared["current"]); current != "" {
							return fmt.Errorf("the save already researches %s; the roadmap must select on an idle tab", current)
						}
						if benches := na.AsNumber(prepared["researchBenches"]); benches != 0 {
							return fmt.Errorf("the save already holds %v research benches; nothing for the ladder to build", benches)
						}
						return nil
					},
					Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
						live, err := h.Call(ctx, "bench-audit", "test/research_ladder_audit", map[string]any{"project": firstRung})
						if err != nil {
							return err
						}
						report["bench_stage"] = live
						if benches := na.AsNumber(live["researchBenches"]); benches <= 0 {
							return fmt.Errorf("no research bench stands after the bench plan completed (%v)", live)
						}
						if !benchBuilt(report, nil) {
							return fmt.Errorf("no routine-laboratory bench plan completed; the bench was not the service's")
						}
						return nil
					},
				})
				return err
			}); err != nil {
				return err
			}
			// The rungs, over the staged colony (a hit and a miss continue
			// from the same paused, bench-built world).
			_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
				WatchConfig: sustainedfood.WatchConfig{Watch: ladderWindow, Goal: policy.EnsureResearch, Until: secondSelection},
			})
			return err
		},
		Postmortem: func(ctx context.Context, s cases.Session) error {
			journal, err := store.Open(ctx, filepath.Join(s.Config().Output, "service.sqlite"))
			if err != nil {
				return fmt.Errorf("reopen journal: %w", err)
			}
			defer journal.Close()
			return audit(ctx, s.Harness(), journal, s.Report(), s.Prior())
		},
	})
}

// completedSelections counts the goal's research-select plans whose every
// action completed, current and retired epochs alike.
func completedSelections(sample map[string]any) int {
	plans, _ := sample["plans"].([]map[string]any)
	retired, _ := sample["retired_plans"].([]map[string]any)
	count := 0
	for _, plan := range append(plans, retired...) {
		id, _ := plan["plan"].(string)
		actions, _ := plan["actions"].(int)
		stages, _ := plan["stages"].(map[string]int)
		if strings.HasPrefix(id, "routine-research-") && actions > 0 && stages["completed"] == actions {
			count++
		}
	}
	return count
}

// secondSelection reports a sample where the roadmap has selected twice:
// the seeded rung and, once the game finished it, the next.
func secondSelection(sample map[string]any) bool { return completedSelections(sample) >= 2 }

// benchPlanCompleted reports a sample whose research goal holds or held
// (retired_plans: a completed method leaves the goal at the next review)
// a research bench plan with every action completed: the bench stands.
func benchPlanCompleted(sample map[string]any) bool {
	return benchBuilt(na.Report{"timeline": []map[string]any{sample}}, nil)
}

// benchBuilt reports whether any watch sample held a completed research
// bench plan under the research goal: the timeline keeps only the latest
// retired plan per sample, so the bench plan leaves the last sample once the
// selections that follow it retire. The timeline is the report's from this
// run's watch, else the prior run's result.json under -postmortem-only
// (JSON-typed, so every row is read through the tolerant accessors).
func benchBuilt(report na.Report, prior map[string]any) bool {
	timeline := rows(report["timeline"])
	if len(timeline) == 0 && prior != nil {
		timeline = rows(prior["timeline"])
	}
	for _, sample := range timeline {
		for _, plan := range append(rows(sample["plans"]), rows(sample["retired_plans"])...) {
			id := na.AsString(plan["plan"])
			actions := na.AsNumber(plan["actions"])
			completed := -1.0
			switch stages := plan["stages"].(type) {
			case map[string]int:
				completed = float64(stages["completed"])
			case map[string]any:
				completed = na.AsNumber(stages["completed"])
			}
			if strings.HasPrefix(id, "routine-laboratory-") && actions > 0 && completed == actions {
				return true
			}
		}
	}
	return false
}

// rows reads a slice of maps as the watch keeps it in memory
// ([]map[string]any) or as result.json round-trips it ([]any).
func rows(v any) []map[string]any {
	switch list := v.(type) {
	case []map[string]any:
		return list
	case []any:
		out := make([]map[string]any, 0, len(list))
		for _, raw := range list {
			if row, ok := raw.(map[string]any); ok {
				out = append(out, row)
			}
		}
		return out
	}
	return nil
}

// audit compares the journal's research goal with the live research state
// after the service has stopped.
func audit(ctx context.Context, h *na.Harness, journal *store.Store, report na.Report, prior map[string]any) error {
	review, err := journal.LoadRoutineReview(ctx)
	if err != nil {
		return fmt.Errorf("load routine review: %w", err)
	}
	var bound bool
	for _, binding := range review.Goals {
		if binding.Need != policy.EnsureResearch {
			continue
		}
		goal, err := journal.LoadGoal(ctx, binding.Goal)
		if err != nil {
			return err
		}
		bound = true
		report["research_goal"] = map[string]any{"status": string(goal.Goal.Status), "need": string(goal.Goal.Need), "methods": len(goal.Methods)}
	}
	if !bound {
		return fmt.Errorf("EnsureResearch was never bound in the routine review")
	}
	live, err := h.Call(ctx, "final-audit", "test/research_ladder_audit", map[string]any{"project": firstRung})
	if err != nil {
		return err
	}
	report["live"] = live
	if benches := na.AsNumber(live["researchBenches"]); benches <= 0 {
		return fmt.Errorf("no research bench stands: the ladder never built one (%v)", live)
	}
	if finished, _ := na.AsBool(live["finished"]); !finished {
		return fmt.Errorf("%s did not finish natively: progress=%v current=%v", firstRung, live["progress"], live["current"])
	}
	if current := na.AsString(live["current"]); current != secondRung {
		return fmt.Errorf("the roadmap did not reach %s: current=%q finished=%v", secondRung, current, live["finishedProjects"])
	}
	if len(na.AsSlice(live["researchers"])) == 0 {
		return fmt.Errorf("no colonist has Research work active: %v", live)
	}
	return nil
}
