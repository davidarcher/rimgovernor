package policy

import (
	"reflect"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Alignment tuning (#607): the placement search and the farm planner prefer
// footprints whose south-west corner sits on a colony grid intersection,
// through a weighted term that is zero at Camp and never sends a site a
// whole module away.

func alignedPlacement(t *testing.T, r PlacementSearchRequest) PlacementSearch {
	t.Helper()
	s, err := NewPlacementSearch(r)
	if err != nil {
		t.Fatal(err)
	}
	return s
}

func TestDefaultPlacementAlignmentIsZeroAtCamp(t *testing.T) {
	if DefaultPlacementAlignment(BuildTierCamp) != 0 || FarmSiteWeightsFor(BuildTierCamp) != DefaultFarmSiteWeights() {
		t.Fatal("Camp weights are not zero")
	}
	for _, tier := range []BuildTier{BuildTierMasonry, BuildTierPowered, BuildTierIndustrial, BuildTierSpacer} {
		if DefaultPlacementAlignment(tier) <= 0 || FarmSiteWeightsFor(tier).Alignment <= 0 {
			t.Fatal(tier, "weights are zero")
		}
	}
}

func TestPlacementSelectWithoutGridOrWeightIsNearest(t *testing.T) {
	r := placementSearchFixture()
	r.Protected = []domain.Cell{{X: 10, Z: 11}}
	grid := ColonyGrid{Origin: domain.Cell{X: 13, Z: 13}, Pitch: GridPitch}
	center := placementPreview(t, "center", r.Center, r.Center, domain.Cell{X: 10, Z: 11})
	next := placementPreview(t, "next", domain.Cell{X: 9, Z: 10}, domain.Cell{X: 9, Z: 10}, domain.Cell{X: 9, Z: 11})
	onGrid := placementPreview(t, "grid", domain.Cell{X: 13, Z: 13}, domain.Cell{X: 13, Z: 13}, domain.Cell{X: 13, Z: 14})
	for name, request := range map[string]PlacementSearchRequest{
		"no grid": r,
		"camp weight": func() PlacementSearchRequest {
			c := r
			c.Grid, c.Alignment = domain.Known(grid), DefaultPlacementAlignment(BuildTierCamp)
			return c
		}(),
		"no weight": func() PlacementSearchRequest { c := r; c.Grid = domain.Known(grid); return c }(),
		"unknown": func() PlacementSearchRequest {
			c := r
			c.Alignment = DefaultPlacementAlignment(BuildTierMasonry)
			return c
		}(),
	} {
		s := alignedPlacement(t, request)
		selected, score, ok, err := s.SelectScored("Bed", "WoodLog", []Preview{onGrid, center, next})
		if err != nil || !ok || selected.Action != next.Action || score.Alignment != 0 || score.CornerError != 0 {
			t.Fatal(name, selected.Action.ID(), score, ok, err)
		}
	}
	r.Alignment = -1
	if _, err := NewPlacementSearch(r); err == nil {
		t.Fatal("negative weight accepted")
	}
}

func TestPlacementSelectAtMasonryPrefersNearbyGridCorner(t *testing.T) {
	r := placementSearchFixture()
	r.Grid = domain.Known(ColonyGrid{Origin: domain.Cell{X: 13, Z: 13}, Pitch: GridPitch})
	r.Alignment = DefaultPlacementAlignment(BuildTierMasonry)
	s := alignedPlacement(t, r)
	// The center is six corner cells off the grid; the intersection 4.24
	// cells away wins.
	center := placementPreview(t, "center", r.Center, r.Center, domain.Cell{X: 10, Z: 11})
	onGrid := placementPreview(t, "grid", domain.Cell{X: 13, Z: 13}, domain.Cell{X: 13, Z: 13}, domain.Cell{X: 13, Z: 14})
	selected, score, ok, err := s.SelectScored("Bed", "WoodLog", []Preview{center, onGrid})
	if err != nil || !ok || selected.Action != onGrid.Action || score.CornerError != 0 || score.Alignment != 0 || score.Score != score.Distance {
		t.Fatal(selected.Action.ID(), score, ok, err)
	}
	if got := s.Score(r.Center, []domain.Cell{r.Center, {X: 10, Z: 11}}); got.CornerError != 6 || got.Alignment != 6 || got.Score != 6 {
		t.Fatal(got)
	}
	// Along one axis a corner cell of error is worth exactly one cell of
	// distance, so the tie falls to the nearer site.
	beside := placementPreview(t, "beside", domain.Cell{X: 13, Z: 10}, domain.Cell{X: 13, Z: 10}, domain.Cell{X: 13, Z: 11})
	if selected, _, ok, err = s.SelectScored("Bed", "WoodLog", []Preview{beside, center}); err != nil || !ok || selected.Action != center.Action {
		t.Fatal(selected.Action.ID(), ok, err)
	}
	// The next intersection along is a whole module away and never beats
	// the off-grid center: the term snaps to the nearest corner only.
	if far := s.Score(domain.Cell{X: 13, Z: 29}, []domain.Cell{{X: 13, Z: 29}, {X: 13, Z: 30}}); far.Score <= 6 {
		t.Fatal(far)
	}
}

func TestPlacementSelectNeverChoosesAnAisle(t *testing.T) {
	r := placementSearchFixture()
	grid := ColonyGrid{Origin: domain.Cell{X: 13, Z: 13}, Pitch: GridPitch}
	r.Grid, r.Alignment = domain.Known(grid), DefaultPlacementAlignment(BuildTierMasonry)
	r.Protected = grid.Aisles(r.Bounds)
	s := alignedPlacement(t, r)
	aisle := map[domain.Cell]bool{}
	for _, c := range r.Protected {
		aisle[c] = true
	}
	if !aisle[domain.Cell{X: 10, Z: 10}] || aisle[domain.Cell{X: 13, Z: 13}] {
		t.Fatal("fixture aisles", r.Protected)
	}
	center := placementPreview(t, "center", r.Center, r.Center, domain.Cell{X: 10, Z: 11})
	onGrid := placementPreview(t, "grid", domain.Cell{X: 13, Z: 13}, domain.Cell{X: 13, Z: 13}, domain.Cell{X: 13, Z: 14})
	if _, _, err := s.Select("Bed", "WoodLog", []Preview{center}); err == nil {
		t.Fatal("an aisle anchor is not a proposed site")
	}
	selected, ok, err := s.Select("Bed", "WoodLog", []Preview{onGrid})
	if err != nil || !ok || selected.Action != onGrid.Action {
		t.Fatal(selected.Action.ID(), ok, err)
	}
}

func alignedFarm(r FarmSiteRequest, origin domain.Cell, tier BuildTier) FarmSiteRequest {
	r.Grid = domain.Known(ColonyGrid{Origin: origin, Pitch: GridPitch})
	r.Weights = FarmSiteWeightsFor(tier)
	return r
}

func farmTerms(plan FarmSitePlan) map[string]float64 {
	out := map[string]float64{}
	for _, c := range plan.Selected {
		for _, term := range c.Terms {
			out[term.Name] = term.Value
		}
	}
	return out
}

func TestFarmSiteWithoutGridOrAtCampIsUnchanged(t *testing.T) {
	base := PlanFarmSites(farmSiteFixture())
	if base.Cells != 16 || len(base.Patches) != 1 {
		t.Fatal(base.Explain())
	}
	for name, r := range map[string]FarmSiteRequest{
		"camp": alignedFarm(farmSiteFixture(), domain.Cell{X: 10, Z: 4}, BuildTierCamp),
		"no grid": func() FarmSiteRequest {
			r := farmSiteFixture()
			r.Weights = FarmSiteWeightsFor(BuildTierMasonry)
			return r
		}(),
	} {
		plan := PlanFarmSites(r)
		if !reflect.DeepEqual(plan, base) || strings.Contains(plan.Explain(), "alignment") {
			t.Fatal(name, plan.Explain(), base.Explain())
		}
	}
	r := farmSiteFixture()
	r.Weights = FarmSiteWeightsFor(BuildTierMasonry)
	r.Weights.Alignment = -1
	if plan := PlanFarmSites(r); plan.Cells != 0 {
		t.Fatal("negative alignment weight accepted", plan.Explain())
	}
}

// Module fields (#608): from Masonry the planner plants whole grid module
// interiors, or 11x5 halves under 60 cells, snapped to the grid, and grows
// them as rows that share a full co-linear edge.

// moduleFarmFixture is a flat walkable strip of normal soil with the
// colony grid's origin at 2,2, so module interiors start at x and z in
// {3, 19, 35, ...}, and the anchor at the west end beside the south row.
func moduleFarmFixture(width, height int32, needed int) FarmSiteRequest {
	r := FarmSiteRequest{Bounds: Bounds{width, height}, Anchor: domain.Cell{X: 1, Z: 5}, Needed: needed, Crop: CropChoice{Name: "Plant_Rice", GrowDays: domain.Known(3.0), HarvestNutrition: domain.Known(0.3), FertilityMin: domain.Known(0.7), FertilitySensitivity: domain.Known(1.0)}}
	for x := int32(0); x < width; x++ {
		for z := int32(0); z < height; z++ {
			r.Cells = append(r.Cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), Roofed: domain.Known(false), Fertility: domain.Known(1.0)})
		}
	}
	return alignedFarm(r, domain.Cell{X: 2, Z: 2}, BuildTierMasonry)
}

// zoneCells marks a rectangle as an existing managed zone of the fixture's
// crop.
func zoneCells(r *FarmSiteRequest, id string, rect Rectangle) {
	inside := map[domain.Cell]bool{}
	for _, c := range rectCells(rect) {
		inside[c] = true
	}
	for i := range r.Cells {
		if inside[r.Cells[i].Cell] {
			r.Cells[i].Zone, r.Cells[i].ZoneID = domain.Known(true), domain.Known(id)
		}
	}
	r.Zones = append(r.Zones, FarmZone{ID: id, Crop: r.Crop.Name, Managed: true})
}

func TestFieldModuleCells(t *testing.T) {
	for needed, want := range map[int]int{0: 0, 1: 55, 20: 55, 59: 55, 60: 121, 121: 121, 130: 242, 242: 242, 243: 363} {
		if got := FieldModuleCells(needed); got != want {
			t.Errorf("FieldModuleCells(%d) = %d, want %d", needed, got, want)
		}
	}
}

func TestFarmSiteAtMasonryPlantsOneModuleInteriorOnGrid(t *testing.T) {
	plan := PlanFarmSites(moduleFarmFixture(50, 20, 100))
	if plan.Cells != 121 || len(plan.Patches) != 1 || plan.Fallback || plan.Patches[0] != (Rectangle{X: 3, Z: 3, Width: 11, Height: 11}) {
		t.Fatal(plan.Explain())
	}
	if plan.Target != 121 || plan.Module != (Rectangle{Width: 11, Height: 11}) || !strings.Contains(plan.Explain(), "module=11x11 target=121") {
		t.Fatal(plan.Explain())
	}
	terms := farmTerms(plan)
	if v, ok := terms["alignment"]; !ok || v != 0 {
		t.Fatal("alignment term missing", plan.Explain())
	}
	if v, ok := terms["row"]; !ok || v != 0 || terms["fragment"] >= 0 || strings.Contains(plan.Explain(), "contiguity") {
		t.Fatal("a first module is a fragment with no row partner", plan.Explain())
	}
	// Under 60 cells a half module: the lower 11x5 of the nearest module.
	plan = PlanFarmSites(moduleFarmFixture(50, 20, 20))
	if plan.Cells != 55 || len(plan.Patches) != 1 || plan.Fallback || plan.Patches[0] != (Rectangle{X: 3, Z: 3, Width: 11, Height: 5}) || !strings.Contains(plan.Explain(), "module=11x5 target=55") {
		t.Fatal(plan.Explain())
	}
}

func TestFarmSiteSecondFieldSharesFullEdgeWithAlignedZone(t *testing.T) {
	// An aligned zone fills the middle module of the south row; both of
	// its row neighbours face it across an aisle with a full edge, and the
	// nearer one wins with the row term instead of the fragment charge.
	r := moduleFarmFixture(50, 36, 100)
	zoneCells(&r, "z1", Rectangle{X: 19, Z: 3, Width: 11, Height: 11})
	plan := PlanFarmSites(r)
	if plan.Cells != 121 || len(plan.Patches) != 1 || plan.Patches[0] != (Rectangle{X: 3, Z: 3, Width: 11, Height: 11}) {
		t.Fatal(plan.Explain())
	}
	if c := plan.Selected[0]; c.Adjacent != "z1" || farmTerms(plan)["row"] <= 0 || strings.Contains(plan.Explain(), "fragment") {
		t.Fatal(plan.Explain())
	}
	// A zone covering only half the facing edge is not a row partner.
	r = moduleFarmFixture(50, 36, 100)
	zoneCells(&r, "z1", Rectangle{X: 19, Z: 3, Width: 11, Height: 5})
	plan = PlanFarmSites(r)
	if plan.Patches[0] != (Rectangle{X: 3, Z: 3, Width: 11, Height: 11}) || plan.Selected[0].Adjacent != "" || farmTerms(plan)["row"] != 0 {
		t.Fatal("a partial edge earned the row term", plan.Explain())
	}
	// The row term outweighs a few steps of travel: from an anchor in the
	// aisle north-west of the zone's east neighbour, the module north of
	// the anchor is two steps nearer on average but has no partner, and
	// the one east of the zone that extends the row wins (the module
	// north of the zone, also a partner, is built over).
	r = moduleFarmFixture(50, 36, 100)
	zoneCells(&r, "z1", Rectangle{X: 19, Z: 3, Width: 11, Height: 11})
	r.Anchor = domain.Cell{X: 33, Z: 17}
	for i := range r.Cells {
		if c := r.Cells[i].Cell; c.X >= 19 && c.X < 30 && c.Z >= 19 && c.Z < 30 {
			r.Cells[i].Occupied = domain.Known(true)
		}
	}
	plan = PlanFarmSites(r)
	if len(plan.Patches) != 1 || plan.Patches[0] != (Rectangle{X: 35, Z: 3, Width: 11, Height: 11}) || plan.Selected[0].Adjacent != "z1" {
		t.Fatal(plan.Explain())
	}
}

func TestFarmSiteThirdFieldExtendsRowOrStartsNextRowOnGrid(t *testing.T) {
	r := moduleFarmFixture(60, 36, 250)
	plan := PlanFarmSites(r)
	if plan.Target != 363 || plan.Cells != 363 || len(plan.Patches) != 3 || plan.Fallback {
		t.Fatal(plan.Explain())
	}
	grid, _ := r.Grid.Value()
	for i, patch := range plan.Patches {
		if patch.Width != 11 || patch.Height != 11 || grid.SubCells(grid.Module(domain.Cell{X: patch.X, Z: patch.Z}))[0] != patch {
			t.Fatal("patch", i, "is not a module interior", plan.Explain())
		}
		if i > 0 && (plan.Selected[i].Adjacent != "plan" || plan.Selected[i].Terms[len(plan.Selected[i].Terms)-1].Name != "row") {
			t.Fatal("patch", i, "does not extend the row", plan.Explain())
		}
	}
	if plan.Patches[0] != (Rectangle{X: 3, Z: 3, Width: 11, Height: 11}) {
		t.Fatal(plan.Explain())
	}
	// The later two patches each share a full edge with a planned one.
	pitch := grid.Pitch
	for _, i := range []int{1, 2} {
		p := plan.Patches[i]
		partner := false
		for _, q := range plan.Patches[:i] {
			dx, dz := p.X-q.X, p.Z-q.Z
			partner = partner || dx == 0 && (dz == pitch || dz == -pitch) || dz == 0 && (dx == pitch || dx == -pitch)
		}
		if !partner {
			t.Fatal("patch", i, "has no row partner", plan.Explain())
		}
	}
}

func TestFarmSiteFallbackEngagesOnlyWhenNoModuleMeetsTheFloorAndSnaps(t *testing.T) {
	// One poor cell in each half of every module interior: no whole or
	// half module meets the fertility floor, so the ladder engages on the
	// modules' sub-cell corners.
	r := moduleFarmFixture(50, 20, 20)
	grid, _ := r.Grid.Value()
	for i := range r.Cells {
		if c := r.Cells[i].Cell; (c.X == 8 || c.X == 24 || c.X == 40) && (c.Z == 5 || c.Z == 11) {
			r.Cells[i].Fertility = domain.Known(0.5)
		}
	}
	plan := PlanFarmSites(r)
	if !plan.Fallback || plan.Cells < 55 || plan.Module.Height != 5 {
		t.Fatal(plan.Explain())
	}
	corners := map[domain.Cell]bool{}
	for _, m := range []Rectangle{grid.Module(domain.Cell{X: 3, Z: 3}), grid.Module(domain.Cell{X: 19, Z: 3}), grid.Module(domain.Cell{X: 35, Z: 3})} {
		for _, sub := range grid.SubCells(m) {
			corners[domain.Cell{X: sub.X, Z: sub.Z}] = true
		}
	}
	for _, patch := range plan.Patches {
		if patch.Width > 4 || !corners[domain.Cell{X: patch.X, Z: patch.Z}] {
			t.Fatal("fallback patch off the sub-cell corners", patch, plan.Explain())
		}
	}
	// With the poor cells only in the upper halves, the lower halves meet
	// the floor and no fallback engages.
	r = moduleFarmFixture(50, 20, 20)
	for i := range r.Cells {
		if c := r.Cells[i].Cell; (c.X == 8 || c.X == 24 || c.X == 40) && c.Z == 11 {
			r.Cells[i].Fertility = domain.Known(0.5)
		}
	}
	if plan = PlanFarmSites(r); plan.Fallback || len(plan.Patches) != 1 || plan.Patches[0] != (Rectangle{X: 3, Z: 3, Width: 11, Height: 5}) {
		t.Fatal(plan.Explain())
	}
}

func TestFarmSiteNeverPlantsAnAisle(t *testing.T) {
	r := alignedFarm(farmSiteFixture(), domain.Cell{X: 10, Z: 4}, BuildTierMasonry)
	grid, _ := r.Grid.Value()
	r.Protected = grid.Aisles(r.Bounds)
	plan := PlanFarmSites(r)
	if plan.Cells == 0 {
		t.Fatal(plan.Explain())
	}
	for c := range farmCellsOf(plan) {
		if u, v := grid.local(c); floorMod(u, grid.Pitch) >= ColonyGridModule || floorMod(v, grid.Pitch) >= ColonyGridModule {
			t.Fatal("aisle cell planted", c, plan.Explain())
		}
	}
}
