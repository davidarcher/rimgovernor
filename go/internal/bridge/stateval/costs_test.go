package stateval

import (
	"slices"
	"testing"
)

func costsEvaluator(t *testing.T) *Evaluator {
	t.Helper()
	return New(recordedStub(t), recordedEnv(t))
}

// A stuffed def is made from every stuff that shares a category with it,
// sorted by name; a def not made from stuff has none.
func TestAllowedStuffsFor(t *testing.T) {
	e := costsEvaluator(t)
	stuffs, err := e.AllowedStuffsFor("Wall")
	if err != nil || !slices.IsSorted(stuffs) || !slices.Contains(stuffs, "Steel") || !slices.Contains(stuffs, "WoodLog") {
		t.Fatalf("wall stuffs %v %v", stuffs, err)
	}
	for _, name := range stuffs {
		if !StuffCanMake(e.stub().ThingDef(name), e.stub().ThingDef("Wall")) {
			t.Fatalf("%s cannot make a wall", name)
		}
	}
	if got, err := e.AllowedStuffsFor("Silver"); err != nil || len(got) != 0 {
		t.Fatalf("silver stuffs %v %v", got, err)
	}
	if _, err := e.AllowedStuffsFor("NoSuchDef"); err == nil {
		t.Fatal("an unknown def answered")
	}
}

// CostListAdjusted adds the stuff's units (costStuffCount over its volume) to
// the entry for the stuff; a def with a fixed cost list keeps it.
func TestCostListAdjusted(t *testing.T) {
	e := costsEvaluator(t)
	got, err := e.CostListAdjusted("Wall", "Steel")
	if want := []Cost{{Def: "Steel", Units: 5}}; err != nil || !slices.Equal(got, want) {
		t.Fatalf("steel wall %v %v", got, err)
	}
	if got, err := e.CostListAdjusted("Silver", ""); err != nil || len(got) != 0 {
		t.Fatalf("silver %v %v", got, err)
	}
	if _, err := e.CostListAdjusted("Wall", ""); err == nil {
		t.Fatal("a stuffed def with no stuff answered")
	}
	if _, err := e.CostListAdjusted("Silver", "Steel"); err == nil {
		t.Fatal("stuff for a def that takes none answered")
	}
	if _, err := e.CostListAdjusted("NoSuchDef", ""); err == nil {
		t.Fatal("an unknown def answered")
	}
}

// TerrainCostListAdjusted is the terrain's cost list for the difficulty; a free
// floor has none.
func TestTerrainCostListAdjusted(t *testing.T) {
	e := costsEvaluator(t)
	var priced, free int
	for name, terrain := range e.stub().terrains {
		got, err := e.TerrainCostListAdjusted(name)
		if err != nil {
			t.Fatalf("%s: %v", name, err)
		}
		if len(terrain.GetCostList()) == 0 && terrain.GetCostStuffCount() == 0 && terrain.GetCostListForDifficulty() == nil {
			if len(got) != 0 {
				t.Fatalf("%s is free but costs %v", name, got)
			}
			free++
			continue
		}
		if len(got) == 0 {
			t.Fatalf("%s has a cost but prices %v", name, got)
		}
		priced++
	}
	if priced == 0 || free == 0 {
		t.Fatalf("the recording has %d priced and %d free terrains", priced, free)
	}
	if _, err := e.TerrainCostListAdjusted("NoSuchTerrain"); err == nil {
		t.Fatal("an unknown terrain answered")
	}
}
