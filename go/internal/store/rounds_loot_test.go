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
// StorageHeadroom: the first read of the loot census releases it anyway, and an
// unsafe start item is never released. Once the census was read, a later
// forbidden stack with no storage is held by the remote-loot throttle.
func TestStartKitReleasesOnTheFirstCensusRead(t *testing.T) {
	request := RoundsRequest{Facts: policy.RoundsFacts{Hostiles: domain.Known(int64(0)), EventLoot: domain.Known([]policy.LootItem{
		startKitRow("wood", true, true), startKitRow("steel", true, false), startKitRow("meat", false, false),
	})}}
	loot := policy.EventLootHistory{}
	census, held, err := lootReachFilter(request, loot, !loot.Read)
	if err != nil || len(held) != 0 {
		t.Fatal(held, err)
	}
	next, _, err := policy.ReviewEventLoot(census, loot)
	if err != nil || !next.Read {
		t.Fatal(next, err)
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

	// A stack that first shows up after the census was read is remote loot.
	request.Facts.EventLoot = domain.Known([]policy.LootItem{startKitRow("late", true, true)})
	census, held, err = lootReachFilter(request, policy.EventLootHistory{Read: true}, false)
	rows, _ := census.Value()
	if err != nil || len(rows) != 0 || len(held) != 1 || held[0].Reason != policy.RemoteHoldMissingStorage {
		t.Fatal(rows, held, err)
	}
}

// A census that was unread on the world's first review does not use up the
// start-kit release.
func TestUnreadCensusKeepsTheStartKitRelease(t *testing.T) {
	loot, _, err := policy.ReviewEventLoot(domain.Unknown[[]policy.LootItem](), policy.EventLootHistory{})
	if err != nil || loot.Read {
		t.Fatal(loot, err)
	}
}
