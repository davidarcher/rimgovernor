// Package farm holds issue #3's deterministic crop and farm site selection
// cases: the live service runs with the field family on and the
// clock-scheduler trace enabled, the field planner runs, and the site-type
// selections it traced are asserted -- which kind (outdoor,
// greenhouse-reuse, greenhouse-new, hydroponics, dark-room) and crop won,
// that the winner carries a per-term score breakdown, and that every
// unplantable candidate states its reason. Zone or building receipts are
// not the evidence: the explained choice is.
//
// farm/select-greenhouse and farm/select-hydroponics stage the
// controlled-environment precondition through the private
// test/farm_environment_prepare fixture (FarmEnvironmentFixture.cs): a
// roofed room with a running sun lamp on its own generators under a cold
// snap that closes the outdoor season, floored with soil (greenhouse-reuse)
// or concrete plus basin research and stock (hydroponics). The audit then
// reads the zones or basin placements inside the fixture room so the
// selection is shown enacted, not only traced. The hydroponics case gates
// rice behind unfinished research and expects potatoes, proving the basin
// candidate scores every Hydroponic crop and that a built basin is
// re-cropped from its default rice to the winner through the grower-crop
// patch (#102).
package farm

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/farmselect"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
)

const (
	fixturePrepare = "test/farm_environment_prepare"
	fixtureObserve = "test/farm_environment_observe"
)

// window is how long the field planner gets to trace a selection.
const window = 4 * time.Minute

// selection is one farm/select-<environment> case: environment stages the
// fixture ("" runs the save as is), unavailableCrops gates crops behind
// research, expect is what every traced selection must choose.
type selection struct {
	environment      string
	unavailableCrops string
	expect           farmselect.Expectation
}

func init() {
	for name, sel := range map[string]selection{
		"outdoor":     {expect: farmselect.Expectation{Kind: "outdoor", MinCells: 1}},
		"greenhouse":  {environment: "greenhouse", expect: farmselect.Expectation{Kind: "greenhouse-reuse", MinCells: 1}},
		"hydroponics": {environment: "hydroponics", unavailableCrops: "Plant_Rice", expect: farmselect.Expectation{Kind: "hydroponics", Crop: "Plant_Potato", MinCells: 1}},
	} {
		cases.Register(sel.register("farm/select-" + name))
	}
}

func (sel selection) register(name string) cases.Case {
	var start cases.Start = cases.Save{Name: sustained.BaselineSave}
	if sel.environment != "" {
		start = cases.Fixture{Op: fixturePrepare, Args: map[string]any{"scenario": sel.environment, "unavailableCrops": sel.unavailableCrops}, On: start}
	}
	return cases.Case{
		Name: name,
		Scope: "Field site-type selection against the " + sustained.BaselineSave + " save under the live field planner: " +
			"every traced selection must choose the expected site kind/crop with a per-term breakdown and reasons " +
			"for every unplantable candidate (issue #3 M4).",
		Start: start,
		Keep:  []string{string(na.NeedFood)},
		// The field family alone keeps the whole step budget for the
		// selection under test; the planner explains its selection only on
		// the debug trace.
		Serve: &cases.ServeSpec{
			Families: []string{"field"}, NativeTimeout: 30 * time.Second, Prefix: "farm-select",
			Env: []string{"RIMGOVERNOR_CLOCK_DEBUG=1"},
		},
		Budget: 10 * time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			fixture := s.Prepared()
			if sel.environment != "" {
				// An unlit lamp (outside its sun schedule at this save's
				// hour) leaves no controlled kind plantable; fail before the
				// watch.
				if lit, _ := na.AsBool(fixture["lampScheduled"]); !lit {
					return fmt.Errorf("fixture sun lamp is outside its schedule at this save's hour: %#v", fixture)
				}
				if powered, _ := na.AsBool(fixture["lampPowered"]); !powered {
					return fmt.Errorf("fixture sun lamp is unpowered: %#v", fixture)
				}
				if t := na.AsNumber(fixture["outdoorTemperatureC"]); t >= 0 {
					return fmt.Errorf("outdoor temperature %.1f C did not close the growing season: %#v", t, fixture)
				}
				// The season must stay closed through the watch, not only at
				// the save's hour: at the day's peak the planner rightly goes
				// outdoors once the room is full (#194).
				if t := na.AsNumber(fixture["outdoorPeakTemperatureC"]); t >= 0 {
					return fmt.Errorf("outdoor peak temperature %.1f C reopens the growing season during the watch: %#v", t, fixture)
				}
			}
			observation := sustainedfood.Observation{WatchConfig: sustainedfood.WatchConfig{Watch: window, Poll: 5 * time.Second}}
			if sel.environment != "" {
				observation.Audit = func(ctx context.Context, h *na.Harness, report na.Report) error {
					interior, _ := na.AsMap(fixture["interior"])
					observed, err := h.Call(ctx, "observe", fixtureObserve, map[string]any{
						"minX": int(na.AsNumber(interior["minX"])), "minZ": int(na.AsNumber(interior["minZ"])),
						"maxX": int(na.AsNumber(interior["maxX"])), "maxZ": int(na.AsNumber(interior["maxZ"])),
					})
					if err != nil {
						return err
					}
					report["enacted"] = observed
					switch sel.environment {
					case "greenhouse":
						if len(na.AsSlice(observed["zones"])) == 0 {
							return fmt.Errorf("no growing zone inside the fixture greenhouse after the watch: %#v", observed)
						}
					case "hydroponics":
						if len(na.AsSlice(observed["basins"])) == 0 {
							return fmt.Errorf("no hydroponics basin placed inside the fixture room after the watch: %#v", observed)
						}
						if sel.expect.Crop != "" {
							return basinSows(na.AsSlice(observed["basins"]), sel.expect.Crop)
						}
					}
					return nil
				}
			}
			if _, err := sustainedfood.Observe(ctx, s, observation); err != nil {
				return err
			}
			report := s.Report()
			f, err := os.Open(filepath.Join(s.Config().Output, "service", "stderr.log"))
			if err != nil {
				return err
			}
			selections, err := farmselect.Parse(f)
			f.Close()
			if err != nil {
				return err
			}
			report["selections"] = len(selections)
			last, err := farmselect.Check(selections, sel.expect)
			report["selection"] = last
			return err
		},
	}
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
