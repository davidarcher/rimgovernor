package snapshot

import (
	"path/filepath"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// firebreakColony is a 40x40 map with a 5x5 walled base at 10..14 and a
// growing zone at 15..18 x 10..12 beside it, every cell open ground.
func firebreakColony(t *testing.T) (policy.FirebreakRequest, map[domain.Cell]bool, map[domain.Cell]bool) {
	t.Helper()
	base, zone := map[domain.Cell]bool{}, map[domain.Cell]bool{}
	var walls, zones []domain.Cell
	for x := int32(10); x <= 14; x++ {
		for z := int32(10); z <= 14; z++ {
			base[domain.Cell{X: x, Z: z}] = true
			walls = append(walls, domain.Cell{X: x, Z: z})
		}
	}
	for x := int32(15); x <= 18; x++ {
		for z := int32(10); z <= 12; z++ {
			zone[domain.Cell{X: x, Z: z}] = true
			zones = append(zones, domain.Cell{X: x, Z: z})
		}
	}
	wall, err := domain.NewBuilding("Wall", walls[0], domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	census := policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{{ID: "base", Building: wall, Cells: walls}}}
	home := policy.HomeCoverageObservation{Targets: []policy.HomeCoverageTarget{{ID: "base", Cells: walls, Shape: domain.Known("shape"), Missing: domain.Known(int64(0)), Excluded: domain.Known(int64(0)), ExtentGeometry: domain.Known(policy.HomeExtentGeometry{})}}}
	ground := map[domain.Cell]domain.Fact[policy.FirebreakGround]{}
	for x := int32(0); x < 40; x++ {
		for z := int32(0); z < 40; z++ {
			ground[domain.Cell{X: x, Z: z}] = domain.Known(policy.FirebreakOpen)
		}
	}
	return policy.FirebreakRequest{
		Bounds: domain.Known(policy.Bounds{Width: 40, Height: 40}), Construction: domain.Known(census), Claims: domain.Known([]policy.ConstructionClaim{}), Home: domain.Known(home),
		GrowingZones: domain.Known(zones), Planned: domain.Known([]domain.Cell{}), Ground: ground, Stage: domain.Known(policy.StageStable), Now: 60000,
	}, base, zone
}

// The ring's cut cells with standing grass produce one area cut over
// exactly those cells: grass standing in the base or the growing zone is
// never ordered (#1548).
func TestFirebreakCutsStandingGrassInTheRingOnly(t *testing.T) {
	request, base, zone := firebreakColony(t)
	grass := []domain.Cell{{X: 9, Z: 12}, {X: 8, Z: 8}, {X: 19, Z: 11}, {X: 12, Z: 16}}
	standing := append(append([]domain.Cell{}, grass...), domain.Cell{X: 12, Z: 12}, domain.Cell{X: 16, Z: 11}, domain.Cell{X: 30, Z: 30})
	path := filepath.Join(t.TempDir(), "firebreak-60000.json")
	if err := RecordFirebreak(filepath.Dir(path), NewFirebreak(request, nil, standing, nil)); err != nil {
		t.Fatal(err)
	}
	recorded, err := LoadFirebreak(path)
	if err != nil {
		t.Fatal(err)
	}
	plan, work, err := recorded.Replay()
	if err != nil {
		t.Fatal(err)
	}
	for _, c := range plan.Cells {
		if base[c.Cell] || zone[c.Cell] {
			t.Fatal("ring took a footprint cell", c)
		}
	}
	cut, err := domain.NewAreaPlantCut(work.Cut)
	if err != nil {
		t.Fatal(err)
	}
	want, _ := domain.NewAreaPlantCut(grass)
	if cut != want || len(work.Deconstruct) != 0 {
		t.Fatal(cut.Cells(), work.Deconstruct)
	}
	if !slices.Contains(work.Cut, domain.Cell{X: 19, Z: 11}) {
		t.Fatal("ring beside the growing zone not cut", work.Cut)
	}
}
