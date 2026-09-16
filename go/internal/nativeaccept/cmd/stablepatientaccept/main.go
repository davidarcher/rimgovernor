// Command stablepatientaccept is a diagnostic (not pass/fail) native
// acceptance run for issue #1's "extend stable-patient feeding acceptance to
// withdrawal recovery and concurrent food production": it starts a fresh
// debug game, seeds it with the disposable test/medical_management_setup
// fixture (two tendable Flu patients plus a fourth colonist forced into
// GoJuiceAddiction's withdrawal stage) and test/routine_production_prepare
// (a pre-grown rice zone and fueled campfire bill), launches the live Go
// player-control service with both the tend/medical and food routine
// families on, acquires player authority, and polls the durable store's
// CriticalMedicine and EnsureFoodSupply goal-state timelines over a
// wall-clock window -- evidence for whether ordinary triage/feeding and
// concurrent crop-replacement production progress together rather than one
// starving the other of pawn time.
//
// Shares its run mechanics with sustainedfoodaccept/sustainedmatrixaccept's
// own load/launch/acquire/poll-the-store shape (see
// go/internal/nativeaccept/sustainedfood), reimplemented in
// go/internal/nativeaccept/stablepatient for a fixture-seeded colony instead
// of the tribal8 baseline save, since medical_management_setup's disposable
// patients need to be deterministic rather than relying on natural colony
// generation producing a withdrawal-stage colonist on its own.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/stablepatient"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-stable-patient-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	rimgovernorBinary := flag.String("rimgovernor", "", "absolute path to a prebuilt rimgovernor binary (go build ./go/cmd/rimgovernor)")
	watch := flag.Duration("watch", 20*time.Minute, "wall-clock duration to observe CriticalMedicine/EnsureFoodSupply after authority is acquired")
	poll := flag.Duration("poll", 5*time.Second, "sampling interval during the watch window")
	timeout := flag.Duration("timeout", 30*time.Minute, "overall run timeout (must exceed -watch plus startup/shutdown)")
	nativeTimeout := flag.Duration("native-timeout", 15*time.Second, "serve subprocess's own --timeout (native call budget per ClockScheduler.Step, shared across every chained routine planner in that step)")
	clockSpeed := flag.String("clock-speed", "Superfast", "serve's --clock-speed (Normal, Fast or Superfast); faster packs more simulated ticks into the same wall-clock -watch window")
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
		*output = *root + "/native-stable-patient-acceptance"
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
	report := na.NewReport("Diagnostic: CriticalMedicine (two Flu patients plus a forced GoJuiceAddiction "+
		"withdrawal patient) and EnsureFoodSupply (concurrent pre-seeded growing zone/campfire bill) goal-state "+
		"timelines under the live routine reviewer/field planner, evidence for issue #1's stable-patient feeding "+
		"acceptance extension to withdrawal recovery and concurrent food production. Not a pass/fail acceptance gate.", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()

	cfg := stablepatient.RunConfig{
		Root: *root, Output: *output, GameID: *game, Headless: !*rendered,
		RimgovernorBinary: *rimgovernorBinary,
		Watch:             *watch, Poll: *poll, NativeTimeout: *nativeTimeout, ClockSpeed: *clockSpeed,
	}
	_, err := stablepatient.Run(ctx, cfg, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}
