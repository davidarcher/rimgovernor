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

func TestFarmSiteAtMasonryPrefersGridCornerWithinReach(t *testing.T) {
	// The nearest 4x4 beside the anchor is six corner cells off the grid
	// whose line runs at x=10; the patch on the intersection eight steps
	// further out wins and explains the term.
	plan := PlanFarmSites(alignedFarm(farmSiteFixture(), domain.Cell{X: 10, Z: 4}, BuildTierMasonry))
	if plan.Cells != 16 || len(plan.Patches) != 1 || plan.Patches[0] != (Rectangle{X: 10, Z: 4, Width: 4, Height: 4}) {
		t.Fatal(plan.Explain())
	}
	terms := farmTerms(plan)
	if v, ok := terms["alignment"]; !ok || v != 0 || !strings.Contains(plan.Explain(), "alignment=0.0000") {
		t.Fatal(plan.Explain())
	}
	// Off-grid patches pay the term: the same planner with the origin
	// pushed east of every candidate charges the nearest patch's error.
	r := alignedFarm(farmSiteFixture(), domain.Cell{X: 26, Z: 4}, BuildTierMasonry)
	for i := range r.Cells {
		if x := r.Cells[i].Cell.X; x >= 8 && x < 14 {
			r.Cells[i].Occupied = domain.Known(true)
		}
	}
	plan = PlanFarmSites(r)
	if plan.Cells != 16 || len(plan.Patches) != 1 || plan.Patches[0].X >= 26 {
		t.Fatal("a patch a whole module away won", plan.Explain())
	}
	if v := farmTerms(plan)["alignment"]; v >= 0 {
		t.Fatal("off-grid patch paid nothing", plan.Explain())
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
