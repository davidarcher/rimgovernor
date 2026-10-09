package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A standing wall is swapped only for a stuff that ranks strictly above it and
// is in stock: wood for stone with blocks in hand, never with none,
// never a downgrade when the ladder falls back, never a stuff for itself.
func TestWallUpgradeIsUpgradeOnlyAndStockInHand(t *testing.T) {
	budget := policy.ShellWallBudget
	stone := shellProjection(map[policy.Resource]int64{"WoodLog": 1000, "BlocksGranite": 5 * budget})
	if want := shellStyle(stone).WallStuff(domain.ShellRun); want != "BlocksGranite" || !stone.StuffUpgrade("Wall", "WoodLog", want) {
		t.Fatal("wood wall, stone in stock and wanted is swapped", want)
	}
	if stone.StuffUpgrade("Wall", "BlocksGranite", "BlocksGranite") || stone.StuffUpgrade("Wall", "BlocksGranite", "WoodLog") {
		t.Fatal("the same stuff and a downgrade are never swapped")
	}
	none := shellProjection(map[policy.Resource]int64{"WoodLog": 1000})
	if none.StuffUpgrade("Wall", "WoodLog", "BlocksGranite") {
		t.Fatal("no stone in stock leaves the wood wall")
	}
	if stone.StuffUpgrade("Wall", "WoodLog", "Bioferrite") || stone.StuffUpgrade("Wall", "Nothing", "BlocksGranite") {
		t.Fatal("a stuff the def does not allow is no swap")
	}
}
