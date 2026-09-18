// Command startingsuppliesaccept is the native acceptance run for issues
// #114 and #120: the tribal8 baseline lands its whole starting food (six
// forbidden pemmican stacks beside the drop site) plus wood scattered up to
// 20 cells out, and the supply family must allow every starting stack
// before the colony eats through the pocket food. Run 24 allowed one stack,
// the colonists ate it within a day, and the Allow attempt then sat awaiting
// observation forever because the eaten item was no longer observable (#114);
// a cohort keyed by cell then lost every stack a builder hauled aside for
// the shell and never saw the wood outside the old 20-cell radius (#120).
//
// The run loads the save, launches the live service with the supply family
// beside the shelter family that keeps the clock moving, and watches the
// AllowStartingSupplies goal until its need reads recovered. The audit then
// compares the durable journal (no starting stack still pending, no allow
// attempt still awaiting observation) against the live native census: every
// stack the load census listed must be gone from the forbidden census
// (allowed, or eaten once allowed), wherever it lies now.
//
// The run mechanics (profile, launch, authority, watch window) are shared
// with sustainedfoodaccept through go/internal/nativeaccept/sustainedfood.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const baselineSave = "RimGovernor-tribal8-baseline"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-starting-supplies-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	rimgovernorBinary := flag.String("rimgovernor", "", "absolute path to a prebuilt rimgovernor binary (go build ./go/cmd/rimgovernor)")
	save := flag.String("save", baselineSave, "save name to load (default: the tribal8 baseline)")
	// The tribal8 cohort is 23 stacks, three eight-action plans landed one
	// after another with a review between them; at the bridge's live
	// throughput the third plan closes five to ten minutes in.
	watch := flag.Duration("watch", 12*time.Minute, "wall-clock duration to let the supply planner run after authority is acquired")
	poll := flag.Duration("poll", 5*time.Second, "sampling interval during the watch window")
	timeout := flag.Duration("timeout", 20*time.Minute, "overall run timeout (must exceed -watch plus startup/shutdown)")
	nativeTimeout := flag.Duration("native-timeout", 30*time.Second, "serve subprocess's own --timeout")
	stepStall := flag.Duration("step-stall", 3*time.Minute, "fail fast unless a scheduler step has admitted a clock window within this long of the watch starting (0 disables)")
	families := flag.String("families", "supply,shelter,sleeping", "serve's RIMGOVERNOR_ROUTINE_FAMILIES; the supply family alone allows every stack at the paused load tick and then never advances the clock, so no later review re-reads the census: the shelter (and its sleeping spots) keeps windows running the way run 24 did, with the shell built over the drop site")
	flag.Parse()
	if *root == "" || *rimgovernorBinary == "" || !filepath.IsAbs(*rimgovernorBinary) {
		fmt.Fprintln(os.Stderr, "-root and an absolute -rimgovernor are required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-starting-supplies-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	if entries, _ := os.ReadDir(*output); len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport("Starting supplies against the "+*save+" save under the live supply planner: "+
		"every forbidden starting stack of the load census must be allowed (or eaten once allowed), wherever it "+
		"was hauled, with no Allow attempt left awaiting observation and no starting stack still pending (issues #114, #120).", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	var baseline []map[string]any
	cfg := sustainedfood.RunConfig{
		Root: *root, Output: *output, GameID: *game, Headless: !*rendered,
		RimgovernorBinary: *rimgovernorBinary, Save: *save,
		Watch: *watch, Poll: *poll, NativeTimeout: *nativeTimeout,
		RequestPrefix: "starting-supplies", Families: *families, StepStall: *stepStall,
		Goal:  policy.AllowStartingSupplies,
		Until: recovered,
		// Every native request and reply lands beside the service logs so a
		// refused Allow can be read back instead of rerun.
		ServeArgs: []string{"--flight-recorder", filepath.Join(*output, "service", "flight.jsonl")},
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
	}
	statePath := filepath.Join(*output, "service.sqlite")
	cfg.Audit = func(ctx context.Context, h *na.Harness, report na.Report) error {
		journal, err := store.Open(ctx, statePath)
		if err != nil {
			return fmt.Errorf("reopen journal: %w", err)
		}
		defer journal.Close()
		return audit(ctx, h, journal, baseline, report)
	}
	timeline, err := sustainedfood.Run(ctx, cfg, report)
	report["timeline_samples"] = len(timeline)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
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
