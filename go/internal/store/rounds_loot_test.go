package store

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func startKitRow(thing string, forbidden, safe bool) policy.LootItem {
	return policy.LootItem{Supply: policy.StartingSupply{Thing: thing, Definition: "WoodLog", Cell: domain.Cell{X: 95, Z: 95}}, Forbidden: forbidden, SafeToHaul: safe, SafetyKnown: true, Count: 200}
}

// A hostile-temperature start has no storage yet, so the start kit has no
// StorageHeadroom: the loot filter still releases it, and an unsafe start item
// is never released.
func TestStartKitReleasesWithoutStorage(t *testing.T) {
	request := RoundsRequest{Facts: policy.RoundsFacts{Hostiles: domain.Known(int64(0)), EventLoot: domain.Known([]policy.LootItem{
		startKitRow("wood", true, true), startKitRow("steel", true, false), startKitRow("meat", false, false),
	})}}
	census, held, err := lootReachFilter(request)
	if err != nil || len(held) != 0 {
		t.Fatal(held, err)
	}
	next, _, err := policy.ReviewEventLoot(census, policy.EventLootHistory{})
	if err != nil {
		t.Fatal(err)
	}
	var wood, steel, meat *policy.StartingSupply
	for i, row := range next.Pending {
		switch row.Thing {
		case "wood":
			wood = &next.Pending[i]
		case "steel":
			steel = &next.Pending[i]
		case "meat":
			meat = &next.Pending[i]
		}
	}
	if wood == nil || wood.Forbid {
		t.Fatal("the forbidden safe start kit is not pending for release:", next.Pending)
	}
	if steel != nil {
		t.Fatal("an unsafe forbidden item was released:", next.Pending)
	}
	if meat == nil || !meat.Forbid {
		t.Fatal("an allowed unsafe item is not pending to forbid:", next.Pending)
	}
}
