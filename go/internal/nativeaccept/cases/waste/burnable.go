// The waste/burnable-filters case verifies the RimGovernorBurnable and
// RimGovernorNotBurnable special filters against real
// stockpile semantics and RimWorld's own hauling.
package waste

import (
	"context"
	"fmt"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const (
	burnableStageTool = "test/burnable_stage"
	burnableReadTool  = "test/burnable_read"
)

func init() {
	cases.Register(cases.Case{
		Name: "waste/burnable-filters",
		Scope: "Special thing filters RimGovernorBurnable and RimGovernorNotBurnable on real stockpile zones: a Low zone " +
			"disallowing not-burnable beside a Normal zone disallowing burnable, with one colonist hauling, so " +
			"RimWorld's own hauling puts rotted rottables and worn-out gear only in the Low zone and everything else " +
			"(fresh rottables, a rotted colonist corpse, valuable gear, a resource stack) in the Normal one. A Go " +
			"snapshot test cannot cover it: the verdict is vanilla ThingFilter and hauling semantics over live Things, " +
			"and whether MarketValue folds in hit points, quality and tainted is a read of the game's own stat, " +
			"recorded in the report.",
		Start:       cases.Fixture{Op: burnableStageTool, On: cases.LabStart()},
		RequiredOps: []string{burnableReadTool},
		QuietWorld:  true,
		Budget:      5 * time.Minute,
		Crew:        cases.Crew{Size: 3}, Run: runBurnableFilters,
	})
}

type burnableItem struct {
	label string
	id    string
}

// burnableExpect names the zone each staged item with a fixed verdict must
// end in; gear probes are judged by their read MarketValue instead.
var burnableExpect = map[string]string{
	"potatoes_rotted":        "dump",
	"potatoes_fresh":         "store",
	"corpse_animal_rotted":   "dump",
	"corpse_animal_fresh":    "store",
	"corpse_stranger_rotted": "dump",
	"corpse_colonist_rotted": "store",
	"revolver_normal":        "store",
	"steel_stack":            "store",
}

func runBurnableFilters(ctx context.Context, s cases.Session) error {
	prepared := s.Prepared()
	var items []burnableItem
	var ids []string
	for _, raw := range na.AsSlice(prepared["items"]) {
		row, _ := na.AsMap(raw)
		item := burnableItem{label: na.AsString(row["label"]), id: na.AsString(row["id"])}
		if item.label == "" || item.id == "" {
			return fmt.Errorf("stage: bad item row %#v", row)
		}
		items = append(items, item)
		ids = append(ids, item.id)
	}
	if len(items) == 0 {
		return fmt.Errorf("stage: no items: %#v", prepared)
	}
	cutoff := na.AsNumber(prepared["cutoff"])
	h := s.Harness()
	read := func(label string) (map[string]map[string]any, error) {
		reply, err := h.Call(ctx, label, burnableReadTool, map[string]any{"ids": strings.Join(ids, ",")})
		if err != nil {
			return nil, err
		}
		byLabel := map[string]map[string]any{}
		rows := na.AsSlice(reply["rows"])
		if len(rows) != len(items) {
			return nil, fmt.Errorf("%s: want %d rows, got %#v", label, len(items), reply)
		}
		for i, raw := range rows {
			row, _ := na.AsMap(raw)
			if ok, _ := na.AsBool(row["present"]); !ok {
				return nil, fmt.Errorf("%s: item %s (%s) is gone", label, items[i].label, items[i].id)
			}
			byLabel[items[i].label] = row
		}
		return byLabel, nil
	}
	zoneOf := func(row map[string]any) string {
		switch na.AsString(row["priority"]) {
		case "Low":
			return "dump"
		case "Normal":
			return "store"
		}
		return ""
	}

	// Run game time until every item stands in a zone, or give up after
	// less than one day (a fresh corpse starts rotting after a few).
	var rows map[string]map[string]any
	for advanced := 0; ; advanced += 600 {
		var err error
		if rows, err = read(fmt.Sprintf("burnable-read-%d", advanced)); err != nil {
			return err
		}
		stored := 0
		for _, row := range rows {
			if zoneOf(row) != "" {
				stored++
			}
		}
		if stored == len(rows) {
			break
		}
		if advanced >= 40000 {
			return fmt.Errorf("after %d ticks only %d of %d items are stored: %#v", advanced, stored, len(rows), zoneRows(rows))
		}
		if _, err := s.Advance(ctx, 600); err != nil {
			return err
		}
	}
	s.Report()["placement"] = zoneRows(rows)

	// The filter verdict and the zones' own admission agree with the
	// placement: the dump (Low) admits only burnable, the store (Normal)
	// only not-burnable.
	for label, row := range rows {
		zone := zoneOf(row)
		burnable, _ := na.AsBool(row["burnable"])
		notBurnable, _ := na.AsBool(row["notBurnable"])
		if burnable == notBurnable && !strings.HasPrefix(label, "steel") {
			return fmt.Errorf("%s: filters are not complements: burnable %v, notBurnable %v", label, burnable, notBurnable)
		}
		if zone == "dump" && !burnable || zone == "store" && burnable {
			return fmt.Errorf("%s: stored in the %s but burnable=%v: %#v", label, zone, burnable, row)
		}
		if allows, _ := na.AsBool(row["zoneAllows"]); !allows {
			return fmt.Errorf("%s: its zone does not admit it: %#v", label, row)
		}
		if want, ok := burnableExpect[label]; ok && zone != want {
			return fmt.Errorf("%s: want the %s, got the %q: %#v", label, want, zone, row)
		}
	}
	// A resource stack is outside both filters (CanEverMatch false), so
	// neither disallow rejects it.
	steel := rows["steel_stack"]
	if burnable, _ := na.AsBool(steel["burnable"]); burnable {
		return fmt.Errorf("steel_stack: burnable: %#v", steel)
	}
	if notBurnable, _ := na.AsBool(steel["notBurnable"]); notBurnable {
		return fmt.Errorf("steel_stack: notBurnable matches a def it can never judge: %#v", steel)
	}

	// Gear is judged by MarketValue below the cutoff, whatever folds into it.
	gear := map[string]float64{}
	burnableGear, keptGear := 0, 0
	for label, row := range rows {
		def := na.AsString(row["def"])
		if def != "Apparel_Pants" && def != "MeleeWeapon_Club" && def != "Gun_Revolver" {
			continue
		}
		value := na.AsNumber(row["marketValue"])
		gear[label] = value
		burnable, _ := na.AsBool(row["burnable"])
		if burnable != (value < cutoff) {
			return fmt.Errorf("%s: MarketValue %.2f against cutoff %.2f but burnable=%v", label, value, cutoff, burnable)
		}
		if burnable {
			burnableGear++
		} else {
			keptGear++
		}
	}
	if burnableGear == 0 || keptGear == 0 {
		return fmt.Errorf("gear probes did not straddle the %.2f cutoff (%d burnable, %d kept): %v", cutoff, burnableGear, keptGear, gear)
	}

	// MarketValue folds in a factor when the one-factor probe is cheaper
	// than the plain pants.
	plain := gear["pants_normal"]
	s.Report()["marketValue"] = map[string]any{
		"cutoff": cutoff, "gear": gear,
		"foldsHitPoints": gear["pants_low_hp"] < plain,
		"foldsQuality":   gear["pants_awful"] < plain,
		"foldsTainted":   gear["pants_tainted"] < plain,
	}
	return nil
}

// zoneRows is each item's label, zone and market value for the report.
func zoneRows(rows map[string]map[string]any) map[string]any {
	out := map[string]any{}
	for label, row := range rows {
		zone := ""
		switch na.AsString(row["priority"]) {
		case "Low":
			zone = "dump"
		case "Normal":
			zone = "store"
		}
		out[label] = map[string]any{"zone": zone, "marketValue": row["marketValue"], "rotStage": row["rotStage"]}
	}
	return out
}
