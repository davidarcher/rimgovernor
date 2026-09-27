package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// barricadeDefinitions is the runtime's wooden stand-in (#868's fallback).
func barricadeDefinitions() DefenseDefinitions {
	d := defenseFixture().Definitions
	d.Sandbag, d.SandbagStuff = "Barricade", "WoodLog"
	return d
}

func firingCover(t *testing.T, r DefenseRequest) map[domain.Cell]string {
	t.Helper()
	layout, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	tier, _ := layout.Tier(TierFiringLine)
	out := map[domain.Cell]string{}
	for _, b := range tier.Buildings {
		if b.Definition() != r.Definitions.Floor {
			out[b.Cell()] = b.Definition() + "/" + b.Stuff()
		}
	}
	return out
}

func TestDefenseLayoutSandbagsWhenFabricStocked(t *testing.T) {
	stock := map[Resource]int64{"WoodLog": 500, "Cloth": 20, "Leather_Plain": 40}
	d := DefenseCoverChoice(barricadeDefinitions(), stock, true, true, false, 3)
	if d.Sandbag != DefenseSandbags || d.SandbagStuff != "Leather_Plain" || d.Embrasure != "" {
		t.Fatalf("%+v", d)
	}
	r := defenseFixture()
	r.Definitions = d
	got := firingCover(t, r)
	for _, c := range cells(9, 22, 8, 22, 10, 22) {
		if got[c] != "Sandbags/Leather_Plain" {
			t.Fatalf("%v", got)
		}
	}
}

func TestDefenseLayoutBarricadeFallback(t *testing.T) {
	base := barricadeDefinitions()
	for name, d := range map[string]DefenseDefinitions{
		"short stock":     DefenseCoverChoice(base, map[Resource]int64{"Cloth": 14}, true, true, false, 3),
		"no fabric":       DefenseCoverChoice(base, map[Resource]int64{"WoodLog": 500, "Steel": 500}, true, true, false, 3),
		"stock unknown":   DefenseCoverChoice(base, nil, false, true, false, 3),
		"sandbags absent": DefenseCoverChoice(base, map[Resource]int64{"Cloth": 500}, true, false, false, 3),
	} {
		if d != base {
			t.Fatalf("%s: %+v", name, d)
		}
	}
	r := defenseFixture()
	r.Definitions = base
	if got := firingCover(t, r); got[domain.Cell{X: 9, Z: 22}] != "Barricade/WoodLog" {
		t.Fatalf("%v", got)
	}
}

// A firing line whose cover row is the perimeter wall builds embrasures in
// the wall when the game has the def, and without it leaves those cells to
// the wall and moves the line along.
func TestDefenseLayoutEmbrasureOnWallLine(t *testing.T) {
	r := defenseFixture()
	r.Killbox.Walled = cells(8, 22, 9, 22, 10, 22)
	r.Definitions = DefenseCoverChoice(r.Definitions, nil, false, false, true, 3)
	got := firingCover(t, r)
	for _, c := range cells(9, 22, 8, 22, 10, 22) {
		if got[c] != "Embrasure/BlocksGranite" {
			t.Fatalf("%v", got)
		}
	}
	r.Definitions.Embrasure = ""
	got = firingCover(t, r)
	for _, c := range cells(9, 22, 8, 22, 10, 22) {
		if _, ok := got[c]; ok {
			t.Fatalf("wall cell took cover without an embrasure: %v", got)
		}
	}
	if got[domain.Cell{X: 11, Z: 22}] != "Sandbags/" {
		t.Fatalf("%v", got)
	}
}
