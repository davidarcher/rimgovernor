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
// -environment greenhouse|hydroponics stages the controlled-environment
// precondition through the private test/farm_environment_prepare fixture
// (FarmEnvironmentFixture.cs): a roofed room with a running sun lamp on its
// own generators under a cold snap that closes the outdoor season, floored
// with soil (greenhouse-reuse) or concrete plus basin research and stock
// (hydroponics). The audit then reads the zones or basin placements inside
// the fixture room so the selection is shown enacted, not only traced.
//
// -unavailable-crops gates crops behind unfinished research in the fixture
// game, so -environment hydroponics -unavailable-crops Plant_Rice
// -expect-crop Plant_Potato proves the basin candidate scores every
// Hydroponic crop and that a built basin is re-cropped from its default
// rice to the winner through the grower-crop patch (#102): with
// -expect-crop the hydroponics audit requires a built basin sowing it.
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

const (
	baselineSave   = "RimGovernor-tribal8-baseline"
	fixturePrepare = "test/farm_environment_prepare"
	fixtureObserve = "test/farm_environment_observe"
)

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
	na.BudgetFlag((12 * time.Minute) / 2)
	nativeTimeout := flag.Duration("native-timeout", 30*time.Second, "serve subprocess's own --timeout")
	expectKind := flag.String("expect-kind", "outdoor", "site kind every traced selection must choose (empty accepts any)")
	expectCrop := flag.String("expect-crop", "", "crop every traced selection must choose (empty accepts any)")
	minCells := flag.Int("expect-min-cells", 1, "minimum cells every traced selection must plant")
	families := flag.String("families", "field", "serve's RIMGOVERNOR_ROUTINE_FAMILIES; the field family alone keeps the whole step budget for the selection under test")
	environment := flag.String("environment", "", "stage a controlled environment before the service starts: greenhouse (lit soil room) or hydroponics (lit concrete room with basin research); empty runs the save as is")
	unavailableCrops := flag.String("unavailable-crops", "", "comma-separated plant defs the fixture gates behind unfinished research (requires -environment)")
	flag.Parse()
	if *environment != "" && *environment != "greenhouse" && *environment != "hydroponics" {
		fmt.Fprintln(os.Stderr, "-environment must be empty, greenhouse or hydroponics")
		os.Exit(2)
	}
	if *unavailableCrops != "" && *environment == "" {
		fmt.Fprintln(os.Stderr, "-unavailable-crops requires -environment")
		os.Exit(2)
	}
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
		Watch: *watch, Poll: *poll, NativeTimeout: *nativeTimeout,
		RequestPrefix: "farm-select", Families: *families,
		// Every native request and reply lands beside the service logs so a
		// refused preview can be read back instead of rerun.
		ServeArgs: []string{"--flight-recorder", filepath.Join(*output, "service", "flight.jsonl")},
	}
	var fixture map[string]any
	if *environment != "" {
		cfg.Prepare = func(ctx context.Context, h *na.Harness, report na.Report) error {
			names, err := h.Discovery(ctx)
			if err != nil {
				return err
			}
			if !na.Contains(names, fixturePrepare) {
				return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture FarmEnvironmentFixture", fixturePrepare)
			}
			prepared, err := h.Call(ctx, "prepare", fixturePrepare, map[string]any{"scenario": *environment, "unavailableCrops": *unavailableCrops})
			if err != nil {
				return err
			}
			if success, _ := na.AsBool(prepared["success"]); !success {
				return fmt.Errorf("%s refused: %#v", fixturePrepare, prepared)
			}
			// An unlit lamp (outside its sun schedule at this save's hour)
			// leaves no controlled kind plantable; fail before the watch.
			if lit, _ := na.AsBool(prepared["lampScheduled"]); !lit {
				return fmt.Errorf("fixture sun lamp is outside its schedule at this save's hour: %#v", prepared)
			}
			if powered, _ := na.AsBool(prepared["lampPowered"]); !powered {
				return fmt.Errorf("fixture sun lamp is unpowered: %#v", prepared)
			}
			if t := na.AsNumber(prepared["outdoorTemperatureC"]); t >= 0 {
				return fmt.Errorf("outdoor temperature %.1f C did not close the growing season: %#v", t, prepared)
			}
			fixture = prepared
			report["fixture"] = prepared
			return nil
		}
		cfg.Audit = func(ctx context.Context, h *na.Harness, report na.Report) error {
			interior, _ := na.AsMap(fixture["interior"])
			observed, err := h.Call(ctx, "observe", fixtureObserve, map[string]any{
				"minX": int(na.AsNumber(interior["minX"])), "minZ": int(na.AsNumber(interior["minZ"])),
				"maxX": int(na.AsNumber(interior["maxX"])), "maxZ": int(na.AsNumber(interior["maxZ"])),
			})
			if err != nil {
				return err
			}
			report["enacted"] = observed
			switch *environment {
			case "greenhouse":
				if len(na.AsSlice(observed["zones"])) == 0 {
					return fmt.Errorf("no growing zone inside the fixture greenhouse after the watch: %#v", observed)
				}
			case "hydroponics":
				if len(na.AsSlice(observed["basins"])) == 0 {
					return fmt.Errorf("no hydroponics basin placed inside the fixture room after the watch: %#v", observed)
				}
				if *expectCrop != "" {
					return basinSows(na.AsSlice(observed["basins"]), *expectCrop)
				}
			}
			return nil
		}
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

// basinSows requires at least one built basin sowing the expected crop: a
// new basin sows its definition's default, so a built basin still on
// another crop means the re-crop patch never landed.
func basinSows(basins []any, crop string) error {
	built := 0
	for _, row := range basins {
		basin, _ := na.AsMap(row)
		if basin["stage"] != "built" {
			continue
		}
		built++
		if basin["crop"] == crop {
			return nil
		}
	}
	if built == 0 {
		return fmt.Errorf("no basin finished construction inside the fixture room during the watch; %d placed: %#v", len(basins), basins)
	}
	return fmt.Errorf("%d built basins but none sows %s: %#v", built, crop, basins)
}
