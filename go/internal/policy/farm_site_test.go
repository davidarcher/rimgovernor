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
	r.Weights.Blight = 0 // Isolate contiguity from separation.
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

func TestFieldLaborAndCookingTerms(t *testing.T) {
	r := fieldRequest(1)
	r.Choices = r.Choices[:2]
	for i := range r.Choices {
		r.Choices[i].HarvestWork = domain.Known(200.0)
	}
	for _, tc := range []struct {
		growers int
		crop    string
	}{{1, "Plant_Corn"}, {3, "Plant_Rice"}} {
		r.Growers = domain.Known(tc.growers)
		p, ok := PlanField(r)
		if !ok || p.Crop.Name != tc.crop {
			t.Fatalf("growers=%d: %s", tc.growers, p.Explain())
		}
		if len(p.Candidates[0].Terms) == 0 {
			t.Fatal("missing crop terms")
		}
	}
	r.Growers = domain.Unknown[int]()
	berry := fieldCrop("Plant_Strawberry", 4.6, .4, .7, 1)
	berry.RawPreferred = domain.Known(true)
	r.Choices = append(r.Choices, berry)
	for _, cooks := range []int{0, 1} {
		r.Cooks = domain.Known(cooks)
		p, ok := PlanField(r)
		if !ok || (p.Crop.Name == berry.Name) != (cooks == 0) {
			t.Fatal(cooks, p.Explain())
		}
	}
}

func TestFarmSiteSeparationAndFirebreak(t *testing.T) {
	r := farmSiteFixture()
	r.Zones = []FarmZone{{ID: "field", Crop: r.Crop.Name, Managed: true}}
	for i := range r.Cells {
		s := &r.Cells[i]
		if s.Cell.X < 4 {
			s.Zone, s.ZoneID = domain.Known(true), domain.Known("field")
		}
	}
	p := PlanFarmSites(r)
	if p.Cells == 0 || p.Patches[0].X <= 4 {
		t.Fatal("touching field won", p.Explain())
	}
	// A roofed gap earns an explicit credit; it remains unplantable.
	for i := range r.Cells {
		if r.Cells[i].Cell.X == 4 {
			r.Cells[i].Roofed = domain.Known(true)
		}
	}
	p = PlanFarmSites(r)
	credited := false
	for _, term := range p.Selected[0].Terms {
		credited = credited || term.Name == "firebreak" && term.Value > 0
	}
	if !credited {
		t.Fatal(p.Explain())
	}
	// Two new patches on open soil also leave a gap when there is room.
	r = farmSiteFixture()
	r.Needed = 32
	p = PlanFarmSites(r)
	for _, candidate := range p.Selected {
		for _, term := range candidate.Terms {
			if term.Name == "blight" && term.Value < 0 {
				t.Fatal("new fields touch", p.Explain())
			}
		}
	}
}

func TestFarmPollutionSelectsOnlyCompatibleCells(t *testing.T) {
	r := fieldRequest(1)
	r.Choices = r.Choices[:1]
	r.Choices[0].RequiresCleanSoil = domain.Known(true)
	toxi := fieldCrop("Plant_Toxipotato", 7, .55, .5, .4)
	toxi.RequiresPollution = domain.Known(true)
	r.Choices = append(r.Choices, toxi)
	for i := range r.Site.Cells {
		r.Site.Cells[i].Polluted = domain.Known(true)
	}
	p, ok := PlanField(r)
	if !ok || p.Crop.Name != toxi.Name {
		t.Fatal(p.Explain())
	}
	for i := range r.Site.Cells {
		r.Site.Cells[i].Polluted = domain.Known(false)
	}
	p, ok = PlanField(r)
	if !ok || p.Crop.Name != "Plant_Rice" {
		t.Fatal(p.Explain())
	}
	for i := range r.Site.Cells {
		r.Site.Cells[i].Polluted = domain.Unknown[bool]()
	}
	if p, ok = PlanField(r); ok {
		t.Fatal("unknown pollution planted", p.Explain())
	}
}

func TestFarmObservedRiskSelectsIndoorSite(t *testing.T) {
	r := siteFixture(1)
	r.Environment = domain.Known(siteEnv(21, siteLamp(domain.Cell{X: 6, Z: 6}, true)))
	p, ok := PlanSiteType(r)
	if !ok || p.Kind != SiteOutdoor {
		t.Fatal(p.Explain())
	}
	r.Field.Conditions = domain.Known([]DisasterCondition{{Definition: "ToxicFallout"}})
	p, ok = PlanSiteType(r)
	if !ok || p.Kind == SiteOutdoor {
		t.Fatal("fallout did not change the winner", p.Explain())
	}
	r.Field.Conditions = domain.Unknown[[]DisasterCondition]()
	r.Field.Calendar = domain.Known(Calendar{GrowingDays: 30, GrowingDaysRemaining: 1, NonGrowingDays: 30, Sowing: true})
	p, ok = PlanSiteType(r)
	if !ok || p.Kind == SiteOutdoor {
		t.Fatal("frost did not change the winner", p.Explain())
	}
	// Zero remaining season must still permit controlled crops.
	r.Field.Climate.DaysRemaining = domain.Known(0.0)
	r.Field.Climate.Sowing = domain.Known(false)
	if p, ok = PlanSiteType(r); !ok || p.Kind == SiteOutdoor {
		t.Fatal(p.Explain())
	}
}

func TestFarmDarkCropRequiresDietAndZeroGlow(t *testing.T) {
	r := siteFixture(0)
	r.Environment = domain.Known(siteEnv(21))
	fungus := fieldCrop("Plant_Nutrifungus", 6, .55, .5, .4)
	fungus.MinGlow = domain.Known(0.0)
	fungus.DietAllowed = domain.Known(true)
	r.Field.Choices = []CropChoice{fungus}
	p, ok := PlanSiteType(r)
	if !ok || p.Kind != SiteDarkRoom {
		t.Fatal(p.Explain())
	}
	// Night-time outdoor darkness is not a permanent dark growing room.
	if outdoor, ok := PlanField(r.Field); ok {
		t.Fatal(outdoor.Explain())
	}
	for _, candidate := range p.Candidates {
		if candidate.Kind != SiteDarkRoom && candidate.Cells > 0 {
			t.Fatal(p.Explain())
		}
	}
	r.Field.Choices[0].DietAllowed = domain.Known(false)
	if p, ok = PlanSiteType(r); ok {
		t.Fatal("penalised fungus selected", p.Explain())
	}
	r.Field.Choices[0].DietAllowed = domain.Known(true)
	for i := range r.Field.Site.Cells {
		r.Field.Site.Cells[i].Glow = domain.Known(.1)
	}
	if p, ok = PlanSiteType(r); ok {
		t.Fatal("lit fungus selected", p.Explain())
	}
}

func TestCropWorkersPreserveUnknownAndDisabledWork(t *testing.T) {
	pawn := testWorkPawn("farmer", true, false, []WorkSkill{{Name: "Plants", Level: 6}, {Name: "Cooking", Level: 0}})
	pawn.Work = domain.Known([]WorkPriority{{Work: WorkGrowing, Priority: 1}, {Work: WorkCooking, Priority: 0}})
	g, c := CropWorkers(domain.Known([]WorkPawn{pawn}))
	if g != domain.Known(1) || c != domain.Known(1) {
		t.Fatal(g, c)
	}
	pawn.Work = domain.Known([]WorkPriority{{Work: WorkGrowing, Priority: 0}, {Work: WorkCooking, Disabled: true}})
	g, c = CropWorkers(domain.Known([]WorkPawn{pawn}))
	if g != domain.Known(0) || c != domain.Known(0) {
		t.Fatal(g, c)
	}
	pawn.Work = domain.Unknown[[]WorkPriority]()
	g, c = CropWorkers(domain.Known([]WorkPawn{pawn}))
	if _, known := g.Value(); known {
		t.Fatal("unknown work counted growers")
	}
	if _, known := c.Value(); known {
		t.Fatal("unknown work counted cooks")
	}
}
