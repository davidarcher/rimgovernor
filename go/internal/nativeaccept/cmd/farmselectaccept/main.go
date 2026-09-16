// Command farmselectaccept is the native acceptance run for issue #3's
// deterministic crop and farm site selection: it loads a save, launches the
// live service with the field family on and the clock-scheduler trace
// enabled, lets the field planner run, and then asserts the site-type
// selections it traced -- which kind (outdoor, greenhouse-reuse,
// greenhouse-new, hydroponics, dark-room) and crop won, that the winner
// carries a per-term score breakdown, and that every unplantable candidate
// states its reason. Zone or building receipts are not the evidence: the
// explained choice is.
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

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/farmselect"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
)

const baselineSave = "RimGovernor-tribal8-baseline"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-farm-select-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	rimgovernorBinary := flag.String("rimgovernor", "", "absolute path to a prebuilt rimgovernor binary (go build ./go/cmd/rimgovernor)")
	save := flag.String("save", baselineSave, "save name to load (default: the tribal8 baseline)")
	watch := flag.Duration("watch", 4*time.Minute, "wall-clock duration to let the field planner run after authority is acquired")
	poll := flag.Duration("poll", 5*time.Second, "sampling interval during the watch window")
	timeout := flag.Duration("timeout", 12*time.Minute, "overall run timeout (must exceed -watch plus startup/shutdown)")
	nativeTimeout := flag.Duration("native-timeout", 30*time.Second, "serve subprocess's own --timeout")
	clockSpeed := flag.String("clock-speed", "Superfast", "serve's --clock-speed (Normal, Fast or Superfast)")
	expectKind := flag.String("expect-kind", "outdoor", "site kind every traced selection must choose (empty accepts any)")
	expectCrop := flag.String("expect-crop", "", "crop every traced selection must choose (empty accepts any)")
	minCells := flag.Int("expect-min-cells", 1, "minimum cells every traced selection must plant")
	families := flag.String("families", "field", "serve's RIMGOVERNOR_ROUTINE_FAMILIES; the field family alone keeps the whole step budget for the selection under test")
	flag.Parse()
	if *root == "" || *rimgovernorBinary == "" || !filepath.IsAbs(*rimgovernorBinary) {
		fmt.Fprintln(os.Stderr, "-root and an absolute -rimgovernor are required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-farm-select-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	if entries, _ := os.ReadDir(*output); len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	// The field planner explains its selection only on the debug trace.
	os.Setenv("RIMGOVERNOR_CLOCK_DEBUG", "1")
	report := na.NewReport("Field site-type selection against the "+*save+" save under the live field planner: "+
		"every traced selection must choose the expected site kind/crop with a per-term breakdown and reasons "+
		"for every unplantable candidate (issue #3 M4).", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	cfg := sustainedfood.RunConfig{
		Root: *root, Output: *output, GameID: *game, Headless: !*rendered,
		RimgovernorBinary: *rimgovernorBinary, Save: *save,
		Watch: *watch, Poll: *poll, NativeTimeout: *nativeTimeout, ClockSpeed: *clockSpeed,
		RequestPrefix: "farm-select", Families: *families,
	}
	timeline, err := sustainedfood.Run(ctx, cfg, report)
	if err != nil {
		report["error"] = err.Error()
		os.Exit(report.Finalize(*output))
	}
	report["metrics"] = sustainedfood.DeriveMetrics(timeline)
	f, err := os.Open(filepath.Join(*output, "service", "stderr.log"))
	if err != nil {
		report["error"] = err.Error()
		os.Exit(report.Finalize(*output))
	}
	selections, err := farmselect.Parse(f)
	f.Close()
	if err == nil {
		report["selections"] = len(selections)
		var last farmselect.Selection
		last, err = farmselect.Check(selections, farmselect.Expectation{Kind: *expectKind, Crop: *expectCrop, MinCells: *minCells})
		report["selection"] = last
	}
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}
