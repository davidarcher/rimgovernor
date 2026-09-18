// Command sustainedfoodaccept is a diagnostic (not pass/fail) native
// acceptance run for issue #1's "crop labor and interim food before rations
// run out" eight-colonist startup deficit: it loads the real
// RimGovernor-tribal8-baseline.rws save, launches the live Go player-control
// service (EnsureFoodSupply's own routine families by default; -families
// narrows or widens the composition, since every family shares one step
// budget and a shared machine starves the full pipeline -- issue #103),
// acquires player authority, and then polls the
// durable store's EnsureFoodSupply goal state over a long wall-clock window
// to build a timeline of its Status/Need/Priority and committed methods --
// evidence for exactly where a real 8-colonist campaign's crop-replacement
// loop stalls (prior campaign evidence: commit 1c6c8af1's crop-eight-01
// "blocked at tick 272000 with zero food runway before sustained crop
// replacement").
//
// Unlike routinehaulaccept, this tool never needs a fixture (natural colony
// generation from the baseline save is exactly what's under test) and never
// reopens its own native session mid-run: every sample after the service
// starts comes from the durable SQLite journal (store.Store), which is safe
// to read concurrently with the service's own native session because it
// never touches the single shared GABP slot -- see routinehaulaccept's own
// comment on why only one native session can be connected at a time.
//
// This is the single-save, single-run case of sustainedmatrixaccept (issue
// #1's "larger sustained matrix"); both share the actual run mechanics in
// go/internal/nativeaccept/sustainedfood.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
)

const baselineSave = "RimGovernor-tribal8-baseline"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-sustained-food-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	rimgovernorBinary := flag.String("rimgovernor", "", "absolute path to a prebuilt rimgovernor binary (go build ./go/cmd/rimgovernor)")
	save := flag.String("save", baselineSave, "save name to load (default: the tribal8 baseline)")
	watch := flag.Duration("watch", 20*time.Minute, "wall-clock duration to observe EnsureFoodSupply after authority is acquired")
	poll := flag.Duration("poll", 5*time.Second, "sampling interval during the watch window")
	timeout := flag.Duration("timeout", 30*time.Minute, "overall run timeout (must exceed -watch plus startup/shutdown)")
	nativeTimeout := flag.Duration("native-timeout", 15*time.Second, "serve subprocess's own --timeout (native call budget per ClockScheduler.Step, shared across every chained routine planner in that step)")
	families := flag.String("families", "", "serve's RIMGOVERNOR_ROUTINE_FAMILIES; empty composes EnsureFoodSupply's full pipeline (field,food-storage,acquisition,cooking,supply,production-policy), \"all\" serve's autonomous default. Every family shares one step budget: on a machine running peer headless games narrow it to the family under test (e.g. field), as farmselectaccept does")
	stepStall := flag.Duration("step-stall", 90*time.Second, "fail fast unless a scheduler step has admitted a clock window this long after the watch starts (0 disables); a starved step budget under peer contention otherwise reads as an unchanged 20-minute timeline (issue #103)")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *rimgovernorBinary == "" {
		fmt.Fprintln(os.Stderr, "-rimgovernor is required (absolute path to a prebuilt rimgovernor binary)")
		os.Exit(2)
	}
	if !filepath.IsAbs(*rimgovernorBinary) {
		fmt.Fprintln(os.Stderr, "-rimgovernor must be an absolute path")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-sustained-food-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	entries, _ := os.ReadDir(*output)
	if len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport("Diagnostic: EnsureFoodSupply goal-state timeline against the real "+
		*save+" save under the live routine reviewer/field planner, evidence for "+
		"issue #1's eight-colonist crop labor / interim food deficit. Not a pass/fail acceptance gate.", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()

	cfg := sustainedfood.RunConfig{
		Root: *root, Output: *output, GameID: *game, Headless: !*rendered,
		RimgovernorBinary: *rimgovernorBinary, Save: *save,
		Watch: *watch, Poll: *poll, NativeTimeout: *nativeTimeout,
		Families: *families, StepStall: *stepStall,
	}
	timeline, err := sustainedfood.Run(ctx, cfg, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	report["metrics"] = sustainedfood.DeriveMetrics(timeline)
	os.Exit(report.Finalize(*output))
}
