package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The warehouse shelf upgrade runs inside the stockpiles planner, so the one
// shelter gate holds it: no shelf placement while the initial shelter is owed,
// and the gate releases it once the shelter stands. The separate
// storage-shelves planner (MaintainResource zones only) is gone.
func TestStockpileShelfUpgradeWaitsForShelter(t *testing.T) {
	var stockpiles plannerEntry
	for _, entry := range plannerCatalog {
		if entry.name == "stockpiles" {
			stockpiles = entry
		}
		if entry.name == "storage-shelves" {
			t.Fatal("the separate storage-shelves planner is gone")
		}
	}
	if runsBeforeShelter["stockpiles"] {
		t.Fatal("stockpiles must not be allowlisted")
	}
	if _, held := heldByPriority(stockpiles, shelterOwedReview(domain.FindingUnmet)); !held {
		t.Fatal("stockpiles, and its shelf upgrade, must wait for the initial shelter")
	}
	if _, held := heldByPriority(stockpiles, shelterOwedReview(domain.FindingMet)); held {
		t.Fatal("the gate must release the stockpiles planner once the shelter stands")
	}
}
