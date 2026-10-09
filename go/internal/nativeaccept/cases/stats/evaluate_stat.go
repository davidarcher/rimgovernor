// Package stats holds the cases over the game's stat evaluation.
package stats

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/encoding/protojson"
)

func init() {
	cases.Register(cases.Case{
		Name: "stats/evaluate-stat",
		Scope: "EvaluateStat (#2634) returns, for 20 sampled (def, stuff, stat) rows, the value and shown flag the definition catalog's stat table " +
			"carries, and a typed failure for an unknown stat and an unknown definition.",
		Start: cases.LabStart(),
		// Boot, one catalog read and 22 small reads on a kept process.
		Budget: 5 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: evaluateStat,
	})
}

// evaluateStatSample is the (def, stuff, stat) rows compared with the stat
// table: stuff-made buildings and raw
// materials and foods, over the stats a planner reads.
var evaluateStatSample = [][3]string{
	{"Wall", "BlocksGranite", "MaxHitPoints"},
	{"Wall", "BlocksGranite", "WorkToBuild"},
	{"Wall", "Steel", "MaxHitPoints"},
	{"Wall", "Steel", "Flammability"},
	{"Wall", "WoodLog", "MaxHitPoints"},
	{"Wall", "WoodLog", "Flammability"},
	{"Wall", "WoodLog", "Beauty"},
	{"Door", "Steel", "MaxHitPoints"},
	{"Door", "WoodLog", "WorkToBuild"},
	{"Door", "WoodLog", "MarketValue"},
	{"Steel", "", "MarketValue"},
	{"Steel", "", "Mass"},
	{"Steel", "", "MaxHitPoints"},
	{"WoodLog", "", "MarketValue"},
	{"WoodLog", "", "Flammability"},
	{"Silver", "", "MarketValue"},
	{"MealSimple", "", "MarketValue"},
	{"MealSimple", "", "Nutrition"},
	{"Campfire", "", "MaxHitPoints"},
	{"Campfire", "", "WorkToBuild"},
}

func evaluateStat(ctx context.Context, s cases.Session) error {
	client := s.Harness().Client
	catalog, err := cases.Catalog(ctx, client, s.Identity())
	if err != nil {
		return err
	}
	data, err := json.Marshal(s.Identity())
	if err != nil {
		return err
	}
	id := &c.Identity{}
	if err := protojson.Unmarshal(data, id); err != nil {
		return err
	}
	for _, row := range evaluateStatSample {
		def, stuff, stat := row[0], row[1], row[2]
		want, shown, err := catalog.ShownStatValue(def, stuff, stat)
		if err != nil {
			return fmt.Errorf("stat table %v: %w", row, err)
		}
		got, _, err := client.EvaluateStat(ctx, id, stat, bridge.StatDefSubject(def, stuff, nil))
		if err != nil {
			return fmt.Errorf("EvaluateStat %v: %w", row, err)
		}
		if got.Shown != shown {
			return fmt.Errorf("EvaluateStat %v: shown %v, stat table %v", row, got.Shown, shown)
		}
		if shown && float32(got.Value) != want {
			return fmt.Errorf("EvaluateStat %v: value %v, stat table %v", row, got.Value, want)
		}
	}
	if _, _, err := client.EvaluateStat(ctx, id, "NoSuchStat", bridge.StatDefSubject("Steel", "", nil)); err == nil {
		return fmt.Errorf("an unknown stat was evaluated")
	}
	if _, _, err := client.EvaluateStat(ctx, id, "MarketValue", bridge.StatDefSubject("NoSuchDef", "", nil)); err == nil {
		return fmt.Errorf("an unknown definition was evaluated")
	}
	s.Report()["rows_compared"] = len(evaluateStatSample)
	return nil
}
