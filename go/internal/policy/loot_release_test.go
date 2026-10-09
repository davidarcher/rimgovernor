package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func releaseRow(thing, def string, at domain.Cell) LootItem {
	return LootItem{SafetyKnown: true, SafeToHaul: true, Forbidden: true, Supply: StartingSupply{Thing: thing, Definition: def, Cell: at}}
}

// A dead colonist's gear and corpse arrive forbidden (Pawn.Kill,
// DropAndForbidEverything) and are released; a hive's jelly, forbidden by
// its spawner, and anything near a live fight or a hive is not.
func TestReleaseIsSelective(t *testing.T) {
	gear := releaseRow("rifle", "Gun_BoltActionRifle", domain.Cell{X: 10, Z: 10})
	corpse := releaseRow("corpse", "Corpse_Human", domain.Cell{X: 12, Z: 10})
	jelly := releaseRow("jelly", "InsectJelly", domain.Cell{X: 11, Z: 11})
	jelly.SpawnForbidden = true
	nearHive := releaseRow("steel", "Steel", domain.Cell{X: 60, Z: 60})
	allowedUnsafe := LootItem{SafetyKnown: true, SafeToHaul: false, Supply: StartingSupply{Thing: "mine", Definition: "Steel", Cell: domain.Cell{X: 60, Z: 61}}}
	seeds := domain.Known([]domain.Cell{{X: 60 + ThreatReachCells, Z: 60}})
	census, holds := FilterLootRelease(domain.Known([]LootItem{gear, corpse, jelly, nearHive, allowedUnsafe}), seeds)
	history, need, err := ReviewEventLoot(census, EventLootHistory{})
	if err != nil {
		t.Fatal(err)
	}
	if known, _ := need.Value(); !known {
		t.Fatal("pending unknown")
	}
	var got []string
	for _, row := range history.Pending {
		got = append(got, row.Thing)
		if row.Forbid != (row.Thing == "mine") {
			t.Fatalf("%s forbid=%t", row.Thing, row.Forbid)
		}
	}
	if len(got) != 3 || got[0] != "corpse" || got[1] != "mine" || got[2] != "rifle" {
		t.Fatalf("pending %v", got)
	}
	if len(holds) != 2 || holds[0].Thing != "jelly" || holds[0].Reason != LootHoldSpawnForbidden || holds[1].Thing != "steel" || holds[1].Reason != LootHoldDanger {
		t.Fatalf("holds %+v", holds)
	}
}

func TestReleaseWithUnknownSeedsHoldsOnlySpawnForbidden(t *testing.T) {
	near := releaseRow("steel", "Steel", domain.Cell{X: 1, Z: 1})
	jelly := releaseRow("jelly", "InsectJelly", domain.Cell{X: 2, Z: 2})
	jelly.SpawnForbidden = true
	census, holds := FilterLootRelease(domain.Known([]LootItem{near, jelly}), domain.Unknown[[]domain.Cell]())
	rows, _ := census.Value()
	if len(rows) != 1 || rows[0].Supply.Thing != "steel" || len(holds) != 1 {
		t.Fatal(rows, holds)
	}
}
