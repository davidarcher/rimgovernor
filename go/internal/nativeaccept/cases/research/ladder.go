// The research/ladder case proves the default research roadmap (#230): on
// the Core tribal baseline, with no --routine-research-target and no
// workshop need, the service selects the ladder's first unfinished rung on
// its own, lends the clock ticks until the game finishes it, and then
// selects the next. The fixture seeds what the ladder does not build: a
// simple research bench, and Stonecutting a few points short of done so
// Electricity is reached within a minute-scale watch.
//
// Passing needs live native evidence: Stonecutting finished natively and
// Electricity the current native project, never the journal alone.
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
// assigns the researcher, the emergency families keep an injury or a
// predator from holding the development ranking, and the dialog family
// answers a choice dialog the save may open by itself.
const ladderFamilies = "temperature,work,supply,defense,tend,rescue,medical,field,food-storage,acquisition,cooking,research,dialog,naming"

// ladderWindow is how long the roadmap gets to finish the seeded rung and
// select the next: the watch ends early on the second completed selection.
const ladderWindow = 10 * time.Minute

func init() {
	cases.Register(cases.Case{
		Name:   "research/ladder",
		Scope:  fmt.Sprintf("With no research target, EnsureResearch selects %s from the default ladder, keeps the clock moving until it finishes natively, then selects %s; both proven by the live research state (issue #230).", firstRung, secondRung),
		Start:  cases.Fixture{Op: "test/research_ladder_prepare", Args: map[string]any{"project": firstRung}, On: cases.Save{Name: baselineSave}},
		Serve:  &cases.ServeSpec{Families: []string{ladderFamilies}, NativeTimeout: 15 * time.Second, Prefix: "research"},
		Budget: ladderWindow + 5*time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
				WatchConfig: sustainedfood.WatchConfig{Watch: ladderWindow, Poll: 5 * time.Second, Goal: policy.EnsureResearch, Until: secondSelection},
				Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
					prepared := s.Prepared()
					report["fixture"] = prepared
					if finished, _ := na.AsBool(prepared["finished"]); finished {
						return fmt.Errorf("%s is already finished; the first rung has nothing to prove", firstRung)
					}
					if current := na.AsString(prepared["current"]); current != "" {
						return fmt.Errorf("the save already researches %s; the roadmap must select on an idle tab", current)
					}
					return nil
				},
				Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
					journal, err := store.Open(ctx, filepath.Join(s.Config().Output, "service.sqlite"))
					if err != nil {
						return fmt.Errorf("reopen journal: %w", err)
					}
					defer journal.Close()
					return audit(ctx, h, journal, report)
				},
			})
			return err
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

// audit compares the journal's research goal with the live research state
// after the service has stopped.
func audit(ctx context.Context, h *na.Harness, journal *store.Store, report na.Report) error {
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
