package policy

import (
	"fmt"
	"math"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// FarmZone is an existing growing zone the site census can see. Managed zones
// were created by the controller; a candidate patch touching a managed zone
// growing the same crop is a contiguous addition rather than a new fragment.
// Player zones (unmanaged, or an explicitly chosen crop) are never extended and
// their cells never become free land.
type FarmZone struct {
	ID, Crop string
	Managed  bool
}

// FarmSiteWeights express every penalty as a fraction of one normal-soil
// cell's daily nutrition for the crop, so the balance between yield and cost
// is the same for fast rice and slow corn. A distant rich patch loses to
// suitable local soil once its walk, haul and fragmentation costs exceed its
// extra yield.
type FarmSiteWeights struct {
	// Travel is charged per patch cell per walked step from the anchor.
	Travel float64
	// Hauling is charged per patch cell per walked step to the storage cell.
	Hauling float64
	// Fragment is a fixed charge per separate patch; Perimeter per edge cell.
	Fragment, Perimeter float64
	// Contiguity is credited per cell of a patch that adjoins a compatible
	// managed zone, and such a patch is not charged the fixed Fragment cost.
	Contiguity float64
	// Blight charges each edge shared with another field; Firebreak credits
	// an intervening roofed empty cell or impassable occupied cell.
	Blight, Firebreak float64
}

// DefaultFarmSiteWeights make rich soil (+40% yield) worth about 27 extra
// walked steps and a separate patch cost half a cell's output.
func DefaultFarmSiteWeights() FarmSiteWeights {
	return FarmSiteWeights{Travel: 0.015, Hauling: 0.005, Fragment: 0.5, Perimeter: 0.04, Contiguity: 0.1, Blight: .8, Firebreak: .1}
}

type FarmSiteRequest struct {
	Bounds  Bounds
	Anchor  domain.Cell
	Storage domain.Fact[domain.Cell]
	Cells   []SiteCell
	// Protected cells are never planted: accepted footprints, player
	// exclusions, walkways, entrances and reserved routes.
	Protected []domain.Cell
	Zones     []FarmZone
	Crop      CropChoice
	Needed    int
	Weights   FarmSiteWeights
}

type FarmSiteTerm struct {
	Name  string
	Value float64
}

// FarmSiteCandidate records why a patch scored as it did. Score is the sum of
// Terms; Density is Score per cell and orders selection.
type FarmSiteCandidate struct {
	Patch          Rectangle
	Score, Density float64
	Terms          []FarmSiteTerm
	// Adjacent names the compatible managed zone the patch touches, if any.
	Adjacent string
}

type FarmSitePlan struct {
	Patches   []Rectangle
	Selected  []FarmSiteCandidate
	Cells     int
	Fallback  bool
	Unplanted int
}

// Explain renders the selection for logs and acceptance evidence.
func (p FarmSitePlan) Explain() string {
	var b strings.Builder
	fmt.Fprintf(&b, "%d cells in %d patches (fallback=%t unplanted=%d)", p.Cells, len(p.Patches), p.Fallback, p.Unplanted)
	for _, c := range p.Selected {
		fmt.Fprintf(&b, "\n  %dx%d@%d,%d score=%.4f density=%.4f", c.Patch.Width, c.Patch.Height, c.Patch.X, c.Patch.Z, c.Score, c.Density)
		for _, t := range c.Terms {
			fmt.Fprintf(&b, " %s=%.4f", t.Name, t.Value)
		}
		if c.Adjacent != "" {
			fmt.Fprintf(&b, " adjacent=%s", c.Adjacent)
		}
	}
	return b.String()
}

const farmSitePatchLimit = 32

// PlanFarmSites chooses up to 32 disjoint square patches for one crop by a
// bounded, explainable score shared by the starter template and expansion.
// Reward is the crop's fertility-adjusted nutrition rate over the patch;
// penalties are walked travel from the anchor, hauling to storage and
// fragmentation. Patches touching a compatible managed zone are contiguous
// additions. Isolated 1x1 cells are only used once no larger patch can meet
// the crop's fertility floor. Missing, unreachable, occupied, zoned, roofed
// and protected cells never become free land. Output is independent of
// census order.
func PlanFarmSites(r FarmSiteRequest) FarmSitePlan {
	w := r.Weights
	if w == (FarmSiteWeights{}) {
		w = DefaultFarmSiteWeights()
	}
	minimum, mk := r.Crop.FertilityMin.Value()
	sensitivity, sk := r.Crop.FertilitySensitivity.Value()
	days, dk := r.Crop.GrowDays.Value()
	yield, yk := r.Crop.HarvestNutrition.Value()
	if r.Needed <= 0 || r.Needed > 65536 || len(r.Cells) > 65536 || len(r.Protected) > 65536 || len(r.Zones) > 4096 || r.Bounds.Width <= 0 || r.Bounds.Height <= 0 || r.Bounds.Width > 4096 || r.Bounds.Height > 4096 || !mk || !fieldPositive(minimum) || !sk || !foodNumber(sensitivity) || !dk || !fieldPositive(days) || !yk || !fieldPositive(yield) {
		return FarmSitePlan{}
	}
	for _, v := range []float64{w.Travel, w.Hauling, w.Fragment, w.Perimeter, w.Contiguity, w.Blight, w.Firebreak} {
		if !foodNumber(v) || v < 0 {
			return FarmSitePlan{}
		}
	}
	inBounds := func(c domain.Cell) bool { return c.X >= 0 && c.Z >= 0 && c.X < r.Bounds.Width && c.Z < r.Bounds.Height }
	if !inBounds(r.Anchor) {
		return FarmSitePlan{}
	}
	census := make(map[domain.Cell]SiteCell, len(r.Cells))
	for _, c := range r.Cells {
		if _, dup := census[c.Cell]; dup || !inBounds(c.Cell) {
			return FarmSitePlan{}
		}
		census[c.Cell] = c
	}
	blocked := map[domain.Cell]bool{}
	for _, c := range r.Protected {
		blocked[c] = true
	}
	compatible := map[string]bool{}
	for _, z := range r.Zones {
		if z.Managed && z.Crop == r.Crop.Name && z.ID != "" {
			compatible[z.ID] = true
		}
	}
	walkable := func(c domain.Cell) bool {
		s, ok := census[c]
		return ok && positive(s.Walkable)
	}
	// Walked steps over the walkable census, four-connected. Cells the census
	// does not describe are walls for planning purposes.
	distanceFrom := func(origin domain.Cell) map[domain.Cell]int {
		dist := map[domain.Cell]int{}
		if !walkable(origin) {
			return dist
		}
		dist[origin] = 0
		queue := []domain.Cell{origin}
		for len(queue) > 0 {
			c := queue[0]
			queue = queue[1:]
			for _, n := range []domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}} {
				if _, seen := dist[n]; seen || !walkable(n) {
					continue
				}
				dist[n] = dist[c] + 1
				queue = append(queue, n)
			}
		}
		return dist
	}
	travel := distanceFrom(r.Anchor)
	haul := travel
	if storage, known := r.Storage.Value(); known && inBounds(storage) {
		if d := distanceFrom(storage); len(d) > 0 {
			haul = d
		}
	}
	free := map[domain.Cell]float64{}
	for c, s := range census {
		if _, reachable := travel[c]; !reachable {
			continue
		}
		if soil, ok := freeCropSoil(s, minimum, blocked); ok && cropSoilCompatible(r.Crop, s) {
			free[c] = soil
		}
	}
	rate := func(soil float64) float64 { return yield * math.Max(0, 1+(soil-1)*sensitivity) / days }
	unit := rate(1)
	if !fieldPositive(unit) {
		return FarmSitePlan{}
	}
	fields := map[string]bool{}
	for _, zone := range r.Zones {
		fields[zone.ID] = zone.ID != ""
	}
	edges, breaks := map[domain.Cell]int{}, map[domain.Cell]int{}
	addField := func(c domain.Cell) {
		for _, n := range farmNeighbors(c) {
			edges[n]++
			middle, known := census[n]
			if !known || !(positive(middle.Roofed) && siteKnownFalse(middle.Occupied) && siteKnownFalse(middle.Zone) || positive(middle.Occupied) && siteKnownFalse(middle.Walkable)) {
				continue
			}
			beyond := domain.Cell{X: 2*n.X - c.X, Z: 2*n.Z - c.Z}
			breaks[beyond]++
		}
	}
	for cell, s := range census {
		if id, known := s.ZoneID.Value(); known && fields[id] {
			addField(cell)
		}
	}
	// A zoned census cell adjoining the patch is a contiguity partner only if
	// it belongs to a compatible managed zone.
	adjacentZone := func(patch Rectangle) string {
		best := ""
		for _, c := range rectCells(patch) {
			for _, n := range []domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}} {
				s, ok := census[n]
				if !ok {
					continue
				}
				id, known := s.ZoneID.Value()
				if known && compatible[id] && (best == "" || id < best) {
					best = id
				}
			}
		}
		return best
	}
	score := func(patch Rectangle) (FarmSiteCandidate, bool) {
		cells := rectCells(patch)
		reward, walk, carry := 0.0, 0.0, 0.0
		shared, firebreak := 0, 0
		for _, c := range cells {
			soil, ok := free[c]
			if !ok {
				return FarmSiteCandidate{}, false
			}
			reward += rate(soil)
			walk += float64(travel[c])
			carry += float64(haul[c])
			shared += edges[c]
			firebreak += breaks[c]
		}
		n := float64(len(cells))
		adjacent := adjacentZone(patch)
		terms := []FarmSiteTerm{{"yield", reward}, {"travel", -w.Travel * unit * walk}, {"hauling", -w.Hauling * unit * carry}, {"perimeter", -w.Perimeter * unit * float64(2*(patch.Width+patch.Height))}}
		terms = append(terms, FarmSiteTerm{"blight", -w.Blight * unit * float64(shared)}, FarmSiteTerm{"firebreak", w.Firebreak * unit * float64(firebreak)})
		if adjacent == "" {
			terms = append(terms, FarmSiteTerm{"fragment", -w.Fragment * unit})
		} else {
			terms = append(terms, FarmSiteTerm{"contiguity", w.Contiguity * unit * n})
		}
		total := 0.0
		for _, t := range terms {
			total += t.Value
		}
		if !foodNumber(total) {
			return FarmSiteCandidate{}, false
		}
		return FarmSiteCandidate{Patch: patch, Score: total, Density: total / n, Terms: terms, Adjacent: adjacent}, true
	}
	ordered := make([]domain.Cell, 0, len(free))
	for c := range free {
		ordered = append(ordered, c)
	}
	sort.Slice(ordered, func(i, j int) bool { return cellLess(ordered[i], ordered[j]) })
	candidates := func(sizes []int32) []FarmSiteCandidate {
		var out []FarmSiteCandidate
		for _, size := range sizes {
			for _, c := range ordered {
				if c.X+size > r.Bounds.Width || c.Z+size > r.Bounds.Height {
					continue
				}
				if cand, ok := score(Rectangle{c.X, c.Z, size, size}); ok {
					out = append(out, cand)
				}
			}
		}
		sort.Slice(out, func(i, j int) bool {
			a, b := out[i], out[j]
			if a.Density != b.Density {
				return a.Density > b.Density
			}
			if a.Patch.Width != b.Patch.Width {
				return a.Patch.Width > b.Patch.Width
			}
			return cellLess(domain.Cell{X: a.Patch.X, Z: a.Patch.Z}, domain.Cell{X: b.Patch.X, Z: b.Patch.Z})
		})
		return out
	}
	plan := FarmSitePlan{}
	taken := map[domain.Cell]bool{}
	pick := func(pool []FarmSiteCandidate) {
		for len(pool) > 0 {
			if plan.Cells >= r.Needed || len(plan.Patches) >= farmSitePatchLimit {
				return
			}
			// Re-score after each selection: a second patch also pays for
			// touching a field admitted in this same batch.
			best := -1
			for i := range pool {
				clear := true
				shared, firebreak := 0, 0
				for _, c := range rectCells(pool[i].Patch) {
					if taken[c] {
						clear = false
						break
					}
					shared += edges[c]
					firebreak += breaks[c]
				}
				if !clear {
					continue
				}
				candidate := &pool[i]
				for j := range candidate.Terms {
					term := &candidate.Terms[j]
					value := term.Value
					if term.Name == "blight" {
						value = -w.Blight * unit * float64(shared)
					}
					if term.Name == "firebreak" {
						value = w.Firebreak * unit * float64(firebreak)
					}
					term.Value = value
				}
				candidate.Score = 0
				for _, term := range candidate.Terms {
					candidate.Score += term.Value
				}
				candidate.Density = candidate.Score / float64(candidate.Patch.Width*candidate.Patch.Height)
				if best < 0 || pool[i].Density > pool[best].Density {
					best = i
				}
			}
			if best < 0 {
				return
			}
			cand := pool[best]
			pool = append(pool[:best], pool[best+1:]...)
			cells := rectCells(cand.Patch)
			clear := true
			for _, c := range cells {
				clear = clear && !taken[c]
			}
			if !clear {
				continue
			}
			for _, c := range cells {
				taken[c] = true
				addField(c)
			}
			plan.Patches = append(plan.Patches, cand.Patch)
			plan.Selected = append(plan.Selected, cand)
			plan.Cells += len(cells)
		}
	}
	pick(candidates([]int32{4, 3, 2}))
	if plan.Cells < r.Needed && len(plan.Patches) < farmSitePatchLimit {
		before := len(plan.Patches)
		pick(candidates([]int32{1}))
		plan.Fallback = len(plan.Patches) > before
	}
	plan.Unplanted = max(0, r.Needed-plan.Cells)
	return plan
}
