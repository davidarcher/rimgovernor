package bridge

import (
	"math"
	"strings"
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// TestDefinitionCatalogStatValues: the stat values and adjusted costs of the
// recorded game come from the Go evaluator over its rows. A def the catalog
// lacks, a stat the game does not show for the def and a catalog with no stat
// environment are errors, never a default.
func TestDefinitionCatalogStatValues(t *testing.T) {
	catalog := fullCatalog(t)
	for _, c := range []struct {
		def, stuff, stat string
		want             float32
	}{{"WoodLog", "", "MarketValue", 1.2}, {"Silver", "", "MarketValue", 1}, {"Steel", "", "MarketValue", 1.9}} {
		if got, err := catalog.StatValue(c.def, c.stuff, c.stat); err != nil || got != c.want {
			t.Fatalf("%v: got %v, %v", c, got, err)
		}
	}
	costs, err := catalog.AdjustedCosts("Wall", "Steel")
	if err != nil || len(costs) != 1 || costs[0].GetDefName() != "Steel" || costs[0].GetUnits() != 5 {
		t.Fatalf("costs %v %v", costs, err)
	}
	if costs, err := catalog.AdjustedCosts("Silver", ""); err != nil || len(costs) != 0 {
		t.Fatalf("no-cost def %v %v", costs, err)
	}
	if _, err := catalog.AdjustedCosts("Silver", "Steel"); err == nil {
		t.Fatal("a stuff for a def that is not made from stuff was answered")
	}
	if _, err := catalog.AdjustedCosts("Wall", ""); err == nil {
		t.Fatal("a stuffed def with no stuff was answered")
	}
	stuffs, err := catalog.AllowedStuffs("Wall")
	if err != nil || len(stuffs) == 0 {
		t.Fatalf("wall stuffs %v %v", stuffs, err)
	}
	if none, err := catalog.AllowedStuffs("Silver"); err != nil || len(none) != 0 {
		t.Fatalf("silver stuffs %v %v", none, err)
	}
	for _, c := range [][3]string{{"Missing", "", "MarketValue"}, {"WoodLog", "", "Missing"}, {"WoodLog", "", "ArmorRating_Sharp"}} {
		if v, err := catalog.StatValue(c[0], c[1], c[2]); err == nil {
			t.Fatalf("%v answered %v", c, v)
		}
	}
	bare, err := DecodeDefinitionCatalog(catalogReply(authorityTestContext(7)).GetObserved(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := bare.StatValue("Bed", "", "MarketValue"); err == nil || !strings.Contains(err.Error(), "no stat environment") {
		t.Fatalf("absent environment: %v", err)
	}
	var none *DefinitionCatalog
	if _, err := none.StatValue("WoodLog", "", "MarketValue"); err == nil {
		t.Fatal("nil catalog answered")
	}
}

// TestDecodeStatEnv: the stat environment decodes into the evaluator's, and a
// malformed one is refused.
func TestDecodeStatEnv(t *testing.T) {
	good := func() *o.StatEnv {
		return &o.StatEnv{
			ActiveMods: []string{"ludeon.rimworld"}, ClassicMode: true,
			ScenarioFactors:    []*o.StatFactor{{Stat: "WorkSpeedGlobal", Factor: 0.5}},
			ButcherYieldFactor: 1.5, FishingYieldFactor: 0.5,
			DifficultyFlags: []*o.DifficultyFlag{{Name: "classicMortars", Value: true}},
		}
	}
	env, err := decodeStatEnv(good())
	if err != nil || !env.ActiveMods["ludeon.rimworld"] || !env.ClassicMode || env.ScenarioFactors["WorkSpeedGlobal"] != 0.5 {
		t.Fatalf("env %+v %v", env, err)
	}
	if none, err := decodeStatEnv(nil); none != nil || err != nil {
		t.Fatalf("an absent environment decoded to %+v %v", none, err)
	}
	for name, change := range map[string]func(*o.StatEnv){
		"empty mod":       func(v *o.StatEnv) { v.ActiveMods = append(v.ActiveMods, "") },
		"repeated factor": func(v *o.StatEnv) { v.ScenarioFactors = append(v.ScenarioFactors, v.ScenarioFactors[0]) },
		"nan factor":      func(v *o.StatEnv) { v.ScenarioFactors[0].Factor = float32(math.NaN()) },
		"unnamed factor":  func(v *o.StatEnv) { v.ScenarioFactors[0].Stat = "" },
		"repeated flag":   func(v *o.StatEnv) { v.DifficultyFlags = append(v.DifficultyFlags, v.DifficultyFlags[0]) },
		"unnamed flag":    func(v *o.StatEnv) { v.DifficultyFlags[0].Name = "" },
	} {
		v := good()
		change(v)
		if _, err := decodeStatEnv(v); err == nil {
			t.Fatalf("%s accepted", name)
		}
	}
}
