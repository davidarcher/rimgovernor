package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The stockpiles planner runs while the initial shelter is owed: it zones the
// start kit's storage (zone edits are instant). The warehouse shelf upgrade and
// the storage shells need builders, so while the shelter is owed the planner
// drops them from its proposal (withoutBuilds) and files the shelter pending
// when only they remain. The separate storage-shelves planner is gone.
func TestStockpilesRunWhileShelterOwedWithoutBuilding(t *testing.T) {
	var stockpiles plannerEntry
	for _, entry := range plannerCatalog {
		if entry.name == "stockpiles" {
			stockpiles = entry
		}
		if entry.name == "storage-shelves" {
			t.Fatal("the separate storage-shelves planner is gone")
		}
	}
	if !runsBeforeShelter["stockpiles"] {
		t.Fatal("stockpiles must run while the shelter is owed")
	}
	if _, held := heldByPriority(stockpiles, shelterOwedReview(domain.FindingUnmet)); held {
		t.Fatal("the gate holds the stockpiles planner while the shelter is owed")
	}
	if !shelterOwed(*shelterOwedReview(domain.FindingUnmet)) || shelterOwed(*shelterOwedReview(domain.FindingMet)) {
		t.Fatal("shelterOwed does not follow the housing goal")
	}

	proposal := policy.StockpileReview{
		Known: true, Active: true,
		Edits: []policy.StockpileEdit{{Kind: policy.StockpileDelete, Zone: "Zone_1"}},
		Rooms: []policy.PlannedRole{policy.PlannedStorage},
		Shelf: policy.ShelfStep{Kind: policy.ShelfBuild},
	}
	stripped := withoutBuilds(proposal)
	if len(stripped.Rooms) != 0 || stripped.Shelf.Kind == policy.ShelfBuild || len(stripped.Edits) != 1 {
		t.Fatalf("the shelter-owed proposal still builds or lost its zone edits: %+v", stripped)
	}
	if len(proposal.Rooms) != 1 || proposal.Shelf.Kind != policy.ShelfBuild {
		t.Fatal("withoutBuilds changed its argument")
	}
}
