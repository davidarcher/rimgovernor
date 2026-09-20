package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func layoutTestProjection(tier domain.Fact[policy.BuildTier], grid domain.Fact[policy.ColonyGrid]) observation.ColonyProjection {
	return observation.ColonyProjection{BuildTier: tier, ColonyGrid: grid, Bounds: policy.Bounds{Width: 48, Height: 48}, Region: policy.Rectangle{X: 8, Z: 8, Width: 16, Height: 16}}
}

func cellSet(cells []domain.Cell) map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for _, c := range cells {
		out[c] = true
	}
	return out
}

func TestLayoutProtectedUnchangedAtCampOrWithoutGrid(t *testing.T) {
	grid := domain.Known(policy.ColonyGrid{Pitch: policy.GridPitch})
	existing := []domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 2}}
	for name, p := range map[string]observation.ColonyProjection{
		"camp":         layoutTestProjection(domain.Known(policy.BuildTierCamp), grid),
		"unknown tier": layoutTestProjection(domain.Unknown[policy.BuildTier](), grid),
		"unknown grid": layoutTestProjection(domain.Known(policy.BuildTierMasonry), domain.Unknown[policy.ColonyGrid]()),
	} {
		got := layoutProtected(p, existing)
		if len(got) != len(existing) || got[0] != existing[0] || got[1] != existing[1] {
			t.Fatalf("%s: protected set changed: %v", name, got)
		}
	}
}

func TestLayoutProtectedMasonryProtectsAislesExceptBuilt(t *testing.T) {
	grid := policy.ColonyGrid{Pitch: policy.GridPitch}
	p := layoutTestProjection(domain.Known(policy.BuildTierMasonry), domain.Known(grid))
	wall, err := domain.NewBuilding("Wall", domain.Cell{X: 13, Z: 5}, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	built := []domain.Cell{{X: 13, Z: 5}, {X: 14, Z: 5}}
	p.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{{ID: "w", Building: wall, Cells: built}}})
	existing := []domain.Cell{{X: 1, Z: 1}}
	got := cellSet(layoutProtected(p, existing))
	if !got[existing[0]] {
		t.Fatal("existing protected cell dropped")
	}
	for _, c := range grid.Aisles(p.Bounds) {
		if c == built[0] || c == built[1] {
			if got[c] {
				t.Fatalf("aisle %v under a completed building is protected", c)
			}
			continue
		}
		if !got[c] {
			t.Fatalf("aisle %v not protected", c)
		}
	}
	// Interior cells are not touched.
	if got[domain.Cell{X: 5, Z: 5}] {
		t.Fatal("module interior protected")
	}
	// An exact-ID refresh is not a colony census: nothing is excluded.
	p.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Requested: []string{"w"}, Buildings: []policy.CurrentBuilding{{ID: "w", Building: wall, Cells: built}}})
	if !cellSet(layoutProtected(p, nil))[built[0]] {
		t.Fatal("non-census refresh excluded an aisle")
	}
}

func TestLayoutProtectedHigherTiersAndBound(t *testing.T) {
	grid := policy.ColonyGrid{Pitch: policy.GridPitch}
	p := layoutTestProjection(domain.Known(policy.BuildTierSpacer), domain.Known(grid))
	if len(layoutProtected(p, nil)) != len(grid.Aisles(p.Bounds)) {
		t.Fatal("Spacer tier did not protect the aisles")
	}
	// Past the search bound only the planning window's aisles are added.
	existing := make([]domain.Cell, protectedCellLimit-10)
	got := layoutProtected(p, existing)
	added := got[len(existing):]
	want := grid.AislesWithin(p.Bounds, p.Region)
	if len(added) != len(want) || len(added) == 0 {
		t.Fatalf("added %d aisles past the bound, want %d", len(added), len(want))
	}
	for i := range added {
		if added[i] != want[i] {
			t.Fatalf("aisle %d = %v, want %v", i, added[i], want[i])
		}
	}
}
