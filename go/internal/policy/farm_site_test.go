package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// farmSiteFixture is a flat walkable 40x12 strip of normal soil with the
// colony anchor at the west end.
func farmSiteFixture() FarmSiteRequest {
	r := FarmSiteRequest{Bounds: Bounds{40, 12}, Anchor: domain.Cell{X: 2, Z: 6}, Needed: 16, Crop: CropChoice{Name: "Plant_Rice", GrowDays: domain.Known(3.0), HarvestNutrition: domain.Known(0.3), FertilityMin: domain.Known(0.7), FertilitySensitivity: domain.Known(1.0)}}
	for x := int32(0); x < 40; x++ {
		for z := int32(0); z < 12; z++ {
			r.Cells = append(r.Cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), Roofed: domain.Known(false), Fertility: domain.Known(1.0)})
		}
	}
	return r
}
func farmCellsOf(p FarmSitePlan) map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for _, patch := range p.Patches {
		for _, c := range rectCells(patch) {
			out[c] = true
		}
	}
	return out
}
func setSoil(r *FarmSiteRequest, x0, x1 int32, fertility float64) {
	for i := range r.Cells {
		if c := r.Cells[i].Cell; c.X >= x0 && c.X < x1 {
			r.Cells[i].Fertility = domain.Known(fertility)
		}
	}
}

func TestFarmSiteNearbySoilBeatsDistantRichSoil(t *testing.T) {
	r := farmSiteFixture()
	setSoil(&r, 32, 40, 1.4)
	plan := PlanFarmSites(r)
	if plan.Cells != 16 || len(plan.Patches) != 1 || plan.Fallback {
		t.Fatal(plan.Explain())
	}
	if plan.Patches[0].X >= 32 {
		t.Fatal("distant rich soil won", plan.Explain())
	}
	// The same rich soil next door is preferred: yield matters when travel is equal.
	r = farmSiteFixture()
	setSoil(&r, 3, 8, 1.4)
	plan = PlanFarmSites(r)
	if plan.Patches[0].X < 3 || plan.Patches[0].X+4 > 8 {
		t.Fatal("nearby rich soil lost", plan.Explain())
	}
	for _, c := range plan.Selected {
		names := map[string]bool{}
		for _, term := range c.Terms {
			names[term.Name] = true
		}
		if !names["yield"] || !names["travel"] || !names["hauling"] || !names["perimeter"] || !names["fragment"] {
			t.Fatal("missing explanation terms", c)
		}
	}
}

func TestFarmSiteTravelUsesWalkedPathNotDistance(t *testing.T) {
	// A wall at x=6 with a gap at z=11 makes x=8 far by foot though near by air.
	r := farmSiteFixture()
	for i := range r.Cells {
		if c := r.Cells[i].Cell; c.X == 6 && c.Z != 11 {
			r.Cells[i].Walkable = domain.Known(false)
		}
	}
	setSoil(&r, 7, 40, 0.75)
	r.Needed = 4
	plan := PlanFarmSites(r)
	if len(plan.Patches) != 1 || plan.Patches[0].X >= 6 {
		t.Fatal(plan.Explain())
	}
	// Soil with no walkable path never becomes free land.
	for i := range r.Cells {
		if r.Cells[i].Cell.X == 6 {
			r.Cells[i].Walkable = domain.Known(false)
		}
	}
	setSoil(&r, 0, 6, 0.0)
	if plan = PlanFarmSites(r); plan.Cells != 0 {
		t.Fatal("unreachable soil planted", plan.Explain())
	}
}

func TestFarmSiteFragmentedTerrainFallsBackToSingleCells(t *testing.T) {
	r := farmSiteFixture()
	for i := range r.Cells {
		if c := r.Cells[i].Cell; c.X%2 == 0 || c.Z%2 == 0 {
			r.Cells[i].Fertility = domain.Known(0.0)
		}
	}
	plan := PlanFarmSites(r)
	if !plan.Fallback || plan.Cells != 16 || len(plan.Patches) != 16 {
		t.Fatal(plan.Explain())
	}
	// One intact 4x4 patch is taken before any single cell.
	r = farmSiteFixture()
	for i := range r.Cells {
		if c := r.Cells[i].Cell; (c.X%2 == 0 || c.Z%2 == 0) && !(c.X >= 20 && c.X < 24 && c.Z >= 4 && c.Z < 8) {
			r.Cells[i].Fertility = domain.Known(0.0)
		}
	}
	plan = PlanFarmSites(r)
	if plan.Patches[0] != (Rectangle{20, 4, 4, 4}) || plan.Cells != 16 || len(plan.Patches) != 1 {
		t.Fatal(plan.Explain())
	}
	r.Needed = 20
	plan = PlanFarmSites(r)
	if plan.Patches[0] != (Rectangle{20, 4, 4, 4}) || !plan.Fallback || plan.Cells != 20 {
		t.Fatal(plan.Explain())
	}
}

func TestFarmSiteContiguousManagedExpansion(t *testing.T) {
	// A managed rice zone sits at x 20..23; a 4x4 beside it should win over the
	// equally fertile soil nearer the anchor because it is not a new fragment.
	r := farmSiteFixture()
	zone := func(id string, x0, x1 int32) {
		for i := range r.Cells {
			if c := r.Cells[i].Cell; c.X >= x0 && c.X < x1 && c.Z >= 4 && c.Z < 8 {
				r.Cells[i].Zone = domain.Known(true)
				r.Cells[i].ZoneID = domain.Known(id)
			}
		}
	}
	zone("farm-a", 20, 24)
	r.Zones = []FarmZone{{ID: "farm-a", Crop: "Plant_Rice", Managed: true}}
	r.Weights = DefaultFarmSiteWeights()
	r.Weights.Travel, r.Weights.Hauling = 0.005, 0
	plan := PlanFarmSites(r)
	if len(plan.Patches) != 1 || plan.Selected[0].Adjacent != "farm-a" {
		t.Fatal(plan.Explain())
	}
	p := plan.Patches[0]
	touches := p.X+p.Width == 20 || p.X == 24 || (p.X < 24 && p.X+p.Width > 20 && (p.Z+p.Height == 4 || p.Z == 8))
	if !touches {
		t.Fatal("not contiguous", plan.Explain())
	}
	for c := range farmCellsOf(plan) {
		if c.X >= 20 && c.X < 24 && c.Z >= 4 && c.Z < 8 {
			t.Fatal("existing zone cell replanted", c)
		}
	}
	// A different crop, or a player zone, is not a contiguity partner.
	for _, zones := range [][]FarmZone{{{ID: "farm-a", Crop: "Plant_Corn", Managed: true}}, {{ID: "farm-a", Crop: "Plant_Rice", Managed: false}}, nil} {
		r.Zones = zones
		plan = PlanFarmSites(r)
		if plan.Selected[0].Adjacent != "" || plan.Patches[0].X > 8 {
			t.Fatal(zones, plan.Explain())
		}
	}
}

func TestFarmSitePreservesPlayerZonesProtectedAndOccupiedCells(t *testing.T) {
	r := farmSiteFixture()
	r.Needed = 400
	for i := range r.Cells {
		c := &r.Cells[i]
		switch {
		case c.Cell.X >= 4 && c.Cell.X < 8:
			c.Zone = domain.Known(true)
			c.ZoneID = domain.Known("player")
		case c.Cell.X == 9:
			c.Occupied = domain.Known(true)
		case c.Cell.X == 10:
			c.Roofed = domain.Known(true)
		case c.Cell.X == 11:
			c.Zone = domain.Unknown[bool]()
		}
	}
	for z := int32(0); z < 12; z++ {
		r.Protected = append(r.Protected, domain.Cell{X: 12, Z: z})
	}
	plan := PlanFarmSites(r)
	if plan.Cells == 0 || len(plan.Patches) > 32 {
		t.Fatal(plan.Explain())
	}
	for c := range farmCellsOf(plan) {
		if c.X >= 4 && c.X <= 12 && c.X != 8 {
			t.Fatal("planted a preserved cell", c, plan.Explain())
		}
	}
	// Cells the census does not describe are never free land.
	r = farmSiteFixture()
	r.Cells = r.Cells[:24]
	plan = PlanFarmSites(r)
	for c := range farmCellsOf(plan) {
		if c.X >= 2 {
			t.Fatal("undescribed cell planted", c)
		}
	}
}

func TestFarmSiteDeterministicAndBounded(t *testing.T) {
	r := farmSiteFixture()
	r.Needed = 200
	setSoil(&r, 10, 20, 1.4)
	first := PlanFarmSites(r)
	for i, j := 0, len(r.Cells)-1; i < j; i, j = i+1, j-1 {
		r.Cells[i], r.Cells[j] = r.Cells[j], r.Cells[i]
	}
	if again := PlanFarmSites(r); !reflect.DeepEqual(first, again) {
		t.Fatal("census order changed the plan")
	}
	if first.Cells < 200 || len(first.Patches) > 32 {
		t.Fatal(first.Explain())
	}
	seen := map[domain.Cell]bool{}
	for _, patch := range first.Patches {
		for _, c := range rectCells(patch) {
			if seen[c] {
				t.Fatal("overlap", c)
			}
			seen[c] = true
		}
	}
	for _, bad := range []func(*FarmSiteRequest){
		func(r *FarmSiteRequest) { r.Needed = 0 },
		func(r *FarmSiteRequest) { r.Crop.FertilityMin = domain.Unknown[float64]() },
		func(r *FarmSiteRequest) { r.Anchor = domain.Cell{X: 40, Z: 0} },
		func(r *FarmSiteRequest) { r.Cells = append(r.Cells, r.Cells[0]) },
		func(r *FarmSiteRequest) { r.Weights = FarmSiteWeights{Travel: -1} },
	} {
		r := farmSiteFixture()
		bad(&r)
		if plan := PlanFarmSites(r); plan.Cells != 0 || len(plan.Patches) != 0 {
			t.Fatal("invalid request planned", plan.Explain())
		}
	}
}

func TestStarterFarmsUseSharedSiteScore(t *testing.T) {
	// Rich soil on the far west edge is over 27 walked steps from a room on the
	// east side, so suitable normal soil near the room wins.
	r := starterFixture()
	r.Anchor = domain.Cell{X: 35, Z: 20}
	for i := range r.Cells {
		if c := r.Cells[i].Cell; c.X < 6 {
			r.Cells[i].Fertility = domain.Known(1.4)
		}
	}
	layouts, err := StarterLayouts(r)
	if err != nil || len(layouts) == 0 {
		t.Fatal(err)
	}
	best := layouts[0]
	if best.Room.X < 30 || best.SelectedCells < 38 || best.FarmSites.Cells != best.SelectedCells {
		t.Fatal(best.FarmSites.Explain())
	}
	for _, patch := range best.Farms {
		if patch.X < 6 {
			t.Fatal("starter farms walked to distant rich soil", best.FarmSites.Explain())
		}
		if patch.Width < 2 {
			t.Fatal("starter used single cells with open soil available", best.FarmSites.Explain())
		}
	}
}
