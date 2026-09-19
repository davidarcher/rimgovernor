// Package supply holds the starting-supplies case (issues #114 and #120):
// the tribal8 baseline lands its whole starting food (six forbidden
// pemmican stacks beside the drop site) plus wood scattered up to 20 cells
// out, and the supply family must allow every starting stack before the
// colony eats through the pocket food. Run 24 allowed one stack, the
// colonists ate it within a day, and the Allow attempt then sat awaiting
// observation forever because the eaten item was no longer observable
// (#114); a cohort keyed by cell then lost every stack a builder hauled
// aside for the shell and never saw the wood outside the old 20-cell
// radius (#120).
//
// The case runs the supply family beside the shelter family that keeps the
// clock moving and watches AllowStartingSupplies until its need reads
// recovered. The audit then compares the durable journal (no starting
// stack still pending, no allow attempt still awaiting observation)
// against the live native census: every stack the load census listed must
// be gone from the forbidden census (allowed, or eaten once allowed),
// wherever it lies now.
package supply

import (
	"context"
	"fmt"
	"path/filepath"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// families: the supply family alone allows every stack at the paused load
// tick and then never advances the clock, so no later review re-reads the
// census; the shelter (and its sleeping spots) keeps windows running the
// way run 24 did, with the shell built over the drop site. The baseline
// colonists start hungry with Food the one live need, and a colonist who
// goes down parks the clock behind a CriticalMedical emergency nothing
// else could serve (#319), so tend and rescue run beside them as in #201's
// startup cases.
const families = "supply,shelter,sleeping,tend,rescue"

// window: the tribal8 cohort is 23 stacks, three eight-action plans landed
// one after another with a review between them; at the bridge's live
// throughput the third plan closes five to ten minutes in.
const window = 12 * time.Minute

func init() {
	cases.Register(cases.Case{
		Name: "supply/starting",
		Scope: "Starting supplies against the " + sustained.BaselineSave + " save under the live supply planner: " +
			"every forbidden starting stack of the load census must be allowed (or eaten once allowed), wherever it " +
			"was hauled, with no Allow attempt left awaiting observation and no starting stack still pending (issues #114, #120).",
		Start: cases.Save{Name: sustained.BaselineSave},
		// Eating an allowed stack is part of what the audit tolerates (#114).
		Keep: []string{string(na.NeedFood)},
		Serve: &cases.ServeSpec{
			Families: []string{families}, NativeTimeout: 30 * time.Second,
			StepStall: 3 * time.Minute, Prefix: "starting-supplies",
		},
		Budget: 15 * time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			var baseline []map[string]any
			_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
				WatchConfig: sustainedfood.WatchConfig{Watch: window, Poll: 5 * time.Second, Goal: policy.AllowStartingSupplies, Until: recovered},
				Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
					rows, err := forbiddenSupplies(ctx, h, "baseline-colony-facts")
					if err != nil {
						return err
					}
					baseline = rows
					report["baseline_forbidden_supplies"] = rows
					if len(rows) == 0 {
						return fmt.Errorf("save holds no forbidden starting supply near the colonists; nothing to allow")
					}
					return nil
				},
				Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
					journal, err := store.Open(ctx, filepath.Join(s.Config().Output, "service.sqlite"))
					if err != nil {
						return fmt.Errorf("reopen journal: %w", err)
					}
					defer journal.Close()
					return audit(ctx, h, journal, baseline, report)
				},
			})
			return err
		},
	})
}

// recovered reports a sample whose AllowStartingSupplies goal reads no
// forbidden supply left and holds no attempt still in flight: the need
// recovers at the review after the last stack is allowed, while that
// stack's own Allow may still await its observation, and stopping the
// service there would leave it open in the audit through no stall.
func recovered(sample map[string]any) bool {
	need, _ := sample["need"].(string)
	if domain.NeedState(need) != domain.NeedRecovered {
		return false
	}
	plans, _ := sample["plans"].([]map[string]any)
	for _, plan := range plans {
		stages, _ := plan["stages"].(map[string]int)
		for stage, count := range stages {
			switch domain.Stage(stage) {
			case domain.Completed, domain.Cancelled, domain.Unsuccessful:
			default:
				if count > 0 {
					return false
				}
			}
		}
	}
	return true
}

// forbiddenSupplies reads the same native census the review's
// ForbiddenSupplies fact is judged on: forbidden stacks of the scenario's
// starting definitions reachable near the colonists, each with its thing
// id, definition, cell and count.
func forbiddenSupplies(ctx context.Context, h *na.Harness, label string) ([]map[string]any, error) {
	facts, err := h.Call(ctx, label, "home/colony_facts", map[string]any{})
	if err != nil {
		return nil, err
	}
	raw, ok := facts["forbiddenSupplies"].([]any)
	if !ok {
		return nil, fmt.Errorf("native forbidden supply census unavailable: %#v", facts["forbiddenSupplies"])
	}
	rows := make([]map[string]any, 0, len(raw))
	for _, r := range raw {
		row, ok := na.AsMap(r)
		if !ok || na.AsString(row["id"]) == "" {
			return nil, fmt.Errorf("native forbidden supply row lacks a thing id: %#v", r)
		}
		rows = append(rows, row)
	}
	return rows, nil
}

// audit compares the journal's starting-supply history and allow plans with
// the live census after the service has stopped.
func audit(ctx context.Context, h *na.Harness, journal *store.Store, baseline []map[string]any, report na.Report) error {
	review, err := journal.LoadRoutineReview(ctx)
	if err != nil {
		return fmt.Errorf("load routine review: %w", err)
	}
	report["pending_supplies"] = review.StartingSupplies.Pending
	if !review.StartingSupplies.Initialized {
		return fmt.Errorf("the review never took its starting supply census")
	}
	// Every allow plan this epoch committed, retired or not, must have
	// resolved each attempt: an eaten stack completes its Allow instead of
	// holding it, and one gone before dispatch is cancelled (#114). The catalog hides retired plans, so the goal's
	// method history names them.
	var goalID domain.GoalID
	for _, binding := range review.Goals {
		if binding.Need == policy.AllowStartingSupplies {
			goalID = binding.Goal
		}
	}
	if goalID == "" {
		return fmt.Errorf("the review never bound an AllowStartingSupplies goal")
	}
	goal, err := journal.LoadGoal(ctx, goalID)
	if err != nil {
		return fmt.Errorf("load goal: %w", err)
	}
	report["goal_status"] = string(goal.Goal.Status)
	report["goal_need"] = string(goal.Goal.Need)
	methods, err := journal.LoadGoalMethods(ctx, goalID, goal.Goal.Epoch)
	if err != nil {
		return fmt.Errorf("load goal methods: %w", err)
	}
	allow, completed, settled, open := 0, 0, map[string]int{}, map[string]int{}
	for _, method := range methods {
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return fmt.Errorf("load plan %s: %w", method.Plan, err)
		}
		allow++
		for _, p := range plan.Progress {
			switch stage := p.View().Stage; stage {
			case domain.Completed:
				completed++
			case domain.Cancelled, domain.Unsuccessful:
				// A stack that left its cell before its Allow (eaten once a
				// neighbour was allowed, hauled aside by a builder) settles
				// its attempt so the plan closes; the census decides whether
				// anything is still forbidden.
				settled[string(stage)]++
			default:
				open[string(stage)]++
			}
		}
	}
	report["allow_plans"] = allow
	report["allow_actions_completed"] = completed
	report["allow_actions_settled"] = settled
	report["allow_actions_open"] = open
	if allow == 0 {
		return fmt.Errorf("no allow plan was ever composed")
	}
	if len(open) > 0 {
		return fmt.Errorf("%d allow plans left attempts unresolved: %v", allow, open)
	}
	if len(review.StartingSupplies.Pending) > 0 {
		return fmt.Errorf("%d starting supplies still pending: %v", len(review.StartingSupplies.Pending), review.StartingSupplies.Pending)
	}
	// The live census may list forbidden items the colony met later (a
	// hunted animal's drop, a raider's weapon): the review never adopts
	// those, so the native check is on the load census itself. Every stack
	// it listed must be gone from the forbidden census (allowed, or eaten
	// once allowed), wherever a builder hauled it; and every stack the
	// journal admitted must have come from that census.
	cohort := map[string]bool{}
	for _, row := range baseline {
		cohort[na.AsString(row["id"])] = true
	}
	admitted := map[string]bool{}
	for _, method := range methods {
		plan, _ := journal.LoadPlan(ctx, method.Plan)
		for _, admission := range plan.SupplyAdmissions {
			admitted[admission.Admission.Thing] = true
			if !cohort[admission.Admission.Thing] {
				return fmt.Errorf("allow admitted %s, which the load census never listed", admission.Admission.Thing)
			}
		}
	}
	report["cohort_things"] = len(cohort)
	report["admitted_things"] = len(admitted)
	rows, err := forbiddenSupplies(ctx, h, "audit-colony-facts")
	if err != nil {
		return err
	}
	report["forbidden_supplies_after"] = rows
	var still []string
	for _, row := range rows {
		if id := na.AsString(row["id"]); cohort[id] {
			still = append(still, id)
		}
	}
	if len(still) > 0 {
		return fmt.Errorf("%d starting supplies are still forbidden after the watch: %v", len(still), still)
	}
	return nil
}
