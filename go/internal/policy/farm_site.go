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
	// On the colony grid (#608) the same weight credits a module patch
	// sharing a full co-linear edge with an aligned compatible zone (the
	// "row" term) and touch-based contiguity is not scored.
	Contiguity float64
	// Blight charges each edge shared with another field; Firebreak credits
	// an intervening roofed empty cell or impassable occupied cell.
	Blight, Firebreak float64
	// Alignment is a fixed charge per corner cell the patch sits off the
	// colony grid (#607); it applies only when the request carries a grid.
	Alignment float64
}

// DefaultFarmSiteWeights make rich soil (+40% yield) worth about 27 extra
// walked steps and a separate patch cost half a cell's output. The
// alignment charge (FarmSiteWeightsFor) is zero at Camp.
func DefaultFarmSiteWeights() FarmSiteWeights {
	return FarmSiteWeights{Travel: 0.015, Hauling: 0.005, Fragment: 0.5, Perimeter: 0.04, Contiguity: 0.1, Blight: .8, Firebreak: .1}
}

// FarmSiteWeightsFor are the default weights with the alignment charge for
// a build tier (#607): zero at Camp, and above it a charge that makes one
// corner cell of grid error on a 4x4 patch worth about 1.5 walked steps
// (travel and hauling together) for every cell of the patch, so a patch on a
// grid intersection 8 steps further out beats a 4x4 six cells off the grid,
// while one a whole module (16 steps) further out does not.
func FarmSiteWeightsFor(tier BuildTier) FarmSiteWeights {
	w := DefaultFarmSiteWeights()
	if tier >= BuildTierMasonry {
		w.Alignment = 0.5
	}
	return w
}

// DefaultPlacementAlignment is the placement search's alignment weight for
// a build tier (#607): zero at Camp, and above it one corner cell of grid
// error is worth one cell of distance. Moving a footprint one cell toward a
// grid line removes one cell of error, so the nearest intersection wins
// whenever its footprint is free (a diagonal step removes two cells of
// error for 1.41 of distance; along an axis the tie falls to the nearer
// site), while the next intersection along, a whole module (16 cells)
// further out, never beats a nearby off-grid site.
func DefaultPlacementAlignment(tier BuildTier) float64 {
	if tier < BuildTierMasonry {
		return 0
	}
	return 1
}

type FarmSiteRequest struct {
	Bounds  Bounds
	Anchor  domain.Cell
	Storage domain.Fact[domain.Cell]
	Cells   []SiteCell
	// Protected cells are never planted: accepted footprints, player
	// exclusions, walkways, entrances and reserved routes.
	Protected    []domain.Cell
	Zones        []FarmZone
	Crop         CropChoice
	Needed       int
	StrictTarget bool
	Weights      FarmSiteWeights
	// Grid is the colony grid the alignment term measures against (#607);
	// unknown, no patch pays it.
	Grid domain.Fact[ColonyGrid]
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
	// Module is the field module planted on the colony grid (#608), width
	// and height only; zero when the plan did not plan on the grid. Target
	// is the demand rounded up to whole modules (FieldModuleCells), the
	// cell count the plan set out to meet.
	Module Rectangle
	Target int
}

// Explain renders the selection for logs and acceptance evidence.
func (p FarmSitePlan) Explain() string {
	var b strings.Builder
	fmt.Fprintf(&b, "%d cells in %d patches (fallback=%t unplanted=%d)", p.Cells, len(p.Patches), p.Fallback, p.Unplanted)
	if p.Module.Width > 0 {
		fmt.Fprintf(&b, " module=%dx%d target=%d", p.Module.Width, p.Module.Height, p.Target)
	}
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
//
// On the colony grid (#608: a known Grid and a positive Alignment weight,
// which layoutAlignment and FarmSiteWeightsFor set together from Masonry
// up) demand is rounded up to whole field modules (FieldModuleCells) and
// the candidates are module patches: the 11x11 interior of a grid module,
// offsets 1..11 from its corner, so the wall ring (offsets 0 and 12) stays
// free for the Spacer hydroponics room and the aisle beyond it for
// hauling; under a whole module's demand the two 11x5 halves. A module
// patch sharing a full co-linear edge with an aligned compatible zone,
// one pitch away or across the half divider, earns the row term in place
// of touch contiguity. The size ladder is the fallback when no module
// patch meets the fertility floor, its patches starting on the module's
// sub-cell corners. At Camp nothing of this applies.
func PlanFarmSites(r FarmSiteRequest) FarmSitePlan {
	w := r.Weights
	if w == (FarmSiteWeights{}) {
		w = DefaultFarmSiteWeights()
	}
	minimum, mk := r.Crop.FertilityMin.Value()
	sensitivity, sk := r.Crop.FertilitySensitivity.Value()
	days, dk := r.Crop.GrowDays.Value()
	yield, yk := r.Crop.HarvestNutrition.Value()
	if units, known := r.Crop.HarvestUnits.Value(); known {
		yield, yk = units, true
	}
	if r.Needed <= 0 || r.Needed > 65536 || len(r.Cells) > 65536 || len(r.Protected) > 65536 || len(r.Zones) > 4096 || r.Bounds.Width <= 0 || r.Bounds.Height <= 0 || r.Bounds.Width > 4096 || r.Bounds.Height > 4096 || !mk || !fieldPositive(minimum) || !sk || !foodNumber(sensitivity) || !dk || !fieldPositive(days) || !yk || !fieldPositive(yield) {
		return FarmSitePlan{}
	}
	for _, v := range []float64{w.Travel, w.Hauling, w.Fragment, w.Perimeter, w.Contiguity, w.Blight, w.Firebreak, w.Alignment} {
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
	grid, aligned := r.Grid.Value()
	aligned = aligned && grid.Valid() && w.Alignment > 0
	target := r.Needed
	if aligned {
		target = FieldModuleCells(r.Needed)
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
	taken := map[domain.Cell]bool{}
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
	// rowPartner names the aligned compatible zone (or "plan" for a patch
	// admitted in this batch) whose facing edge, one pitch away along either
	// axis or across a half module's divider row, is fully zoned: a full
	// co-linear shared edge (#608). Touching is not enough on the grid.
	rowPartner := func(patch Rectangle) string {
		shifts := []domain.Cell{{X: grid.Pitch}, {X: -grid.Pitch}, {Z: grid.Pitch}, {Z: -grid.Pitch}}
		if patch.Height == ColonyGridSubCell {
			shifts = append(shifts, domain.Cell{Z: ColonyGridSubCell + 1}, domain.Cell{Z: -(ColonyGridSubCell + 1)})
		}
		if patch.Width == ColonyGridSubCell {
			shifts = append(shifts, domain.Cell{X: ColonyGridSubCell + 1}, domain.Cell{X: -(ColonyGridSubCell + 1)})
		}
		best := ""
		for _, shift := range shifts {
			edge := Rectangle{X: patch.X + shift.X, Z: patch.Z + shift.Z, Width: patch.Width, Height: patch.Height}
			switch {
			case shift.X > 0:
				edge.Width = 1
			case shift.X < 0:
				edge.X, edge.Width = edge.X+edge.Width-1, 1
			case shift.Z > 0:
				edge.Height = 1
			default:
				edge.Z, edge.Height = edge.Z+edge.Height-1, 1
			}
			partner := ""
			for _, c := range rectCells(edge) {
				id := ""
				if taken[c] {
					id = "plan"
				} else if s, ok := census[c]; ok {
					if zone, known := s.ZoneID.Value(); known && compatible[zone] {
						id = zone
					}
				}
				if id == "" || partner != "" && partner != id {
					partner = ""
					break
				}
				partner = id
			}
			if partner != "" && (best == "" || partner < best) {
				best = partner
			}
		}
		return best
	}
	// contiguityTerms are the terms selection re-scores: touching fields
	// (blight, firebreak) and, on the grid, the row partner and fragment.
	contiguityTerms := func(patch Rectangle) ([]FarmSiteTerm, string, bool) {
		shared, firebreak := 0, 0
		for _, c := range rectCells(patch) {
			if taken[c] {
				return nil, "", false
			}
			shared += edges[c]
			firebreak += breaks[c]
		}
		n := float64(patch.Width * patch.Height)
		terms := []FarmSiteTerm{{"blight", -w.Blight * unit * float64(shared)}, {"firebreak", w.Firebreak * unit * float64(firebreak)}}
		adjacent := ""
		if aligned {
			adjacent = rowPartner(patch)
			if adjacent == "" {
				terms = append(terms, FarmSiteTerm{"row", 0}, FarmSiteTerm{"fragment", -w.Fragment * unit})
			} else {
				terms = append(terms, FarmSiteTerm{"row", w.Contiguity * unit * n})
			}
		} else if adjacent = adjacentZone(patch); adjacent == "" {
			terms = append(terms, FarmSiteTerm{"fragment", -w.Fragment * unit})
		} else {
			terms = append(terms, FarmSiteTerm{"contiguity", w.Contiguity * unit * n})
		}
		return terms, adjacent, true
	}
	// fixedTerms is how many leading terms of a candidate selection never
	// re-scores: yield, travel, hauling, perimeter and, on the grid,
	// alignment.
	fixedTerms := 4
	if aligned {
		fixedTerms = 5
	}
	score := func(patch Rectangle) (FarmSiteCandidate, bool) {
		cells := rectCells(patch)
		reward, walk, carry := 0.0, 0.0, 0.0
		for _, c := range cells {
			soil, ok := free[c]
			if !ok {
				return FarmSiteCandidate{}, false
			}
			reward += rate(soil)
			walk += float64(travel[c])
			carry += float64(haul[c])
		}
		n := float64(len(cells))
		terms := []FarmSiteTerm{{"yield", reward}, {"travel", -w.Travel * unit * walk}, {"hauling", -w.Hauling * unit * carry}, {"perimeter", -w.Perimeter * unit * float64(2*(patch.Width+patch.Height))}}
		if aligned {
			// Every grid candidate is snapped, so the alignment charge
			// is moot; the row term explains the grid instead.
			terms = append(terms, FarmSiteTerm{"alignment", 0})
		}
		rest, adjacent, ok := contiguityTerms(patch)
		if !ok {
			return FarmSiteCandidate{}, false
		}
		terms = append(terms, rest...)
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
	inRect := func(p Rectangle) bool {
		return p.X >= 0 && p.Z >= 0 && p.X+p.Width <= r.Bounds.Width && p.Z+p.Height <= r.Bounds.Height
	}
	order := func(out []FarmSiteCandidate) []FarmSiteCandidate {
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
	// Grid candidates (#608): every module some free cell falls in, as its
	// whole interior or its two 11x5 halves; ladder patches at these tiers
	// start only on a module's sub-cell corners.
	subCells := func() []Rectangle {
		seen := map[Rectangle]bool{}
		var out []Rectangle
		for _, c := range ordered {
			module := grid.Module(c)
			if seen[module] {
				continue
			}
			seen[module] = true
			out = append(out, grid.SubCells(module)...)
		}
		return out
	}
	moduleCandidates := func(half bool) []FarmSiteCandidate {
		var out []FarmSiteCandidate
		seen := map[Rectangle]bool{}
		for i, p := range subCells() {
			if k := i % 9; half && (k < 3 || k > 4) || !half && k != 0 {
				continue
			}
			if seen[p] || !inRect(p) {
				continue
			}
			seen[p] = true
			if cand, ok := score(p); ok {
				out = append(out, cand)
			}
		}
		return order(out)
	}
	candidates := func(sizes []int32) []FarmSiteCandidate {
		origins := ordered
		if aligned {
			seen := map[domain.Cell]bool{}
			origins = nil
			for _, p := range subCells() {
				if c := (domain.Cell{X: p.X, Z: p.Z}); !seen[c] {
					seen[c] = true
					origins = append(origins, c)
				}
			}
			sort.Slice(origins, func(i, j int) bool { return cellLess(origins[i], origins[j]) })
		}
		var out []FarmSiteCandidate
		for _, size := range sizes {
			for _, c := range origins {
				if c.X+size > r.Bounds.Width || c.Z+size > r.Bounds.Height {
					continue
				}
				if cand, ok := score(Rectangle{c.X, c.Z, size, size}); ok {
					out = append(out, cand)
				}
			}
		}
		return order(out)
	}
	plan := FarmSitePlan{Target: target}
	pick := func(pool []FarmSiteCandidate) {
		for len(pool) > 0 {
			if plan.Cells >= target || len(plan.Patches) >= farmSitePatchLimit {
				return
			}
			// Re-score after each selection: a second patch also pays for
			// touching a field admitted in this same batch, and on the grid
			// earns the row term for lining up with one.
			best := -1
			for i := range pool {
				if r.StrictTarget && int(pool[i].Patch.Width*pool[i].Patch.Height) > target-plan.Cells {
					continue
				}
				rest, adjacent, clear := contiguityTerms(pool[i].Patch)
				if !clear {
					continue
				}
				candidate := &pool[i]
				candidate.Terms = append(candidate.Terms[:fixedTerms:fixedTerms], rest...)
				candidate.Adjacent = adjacent
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
			for _, c := range cells {
				taken[c] = true
				addField(c)
			}
			plan.Patches = append(plan.Patches, cand.Patch)
			plan.Selected = append(plan.Selected, cand)
			plan.Cells += len(cells)
		}
	}
	if aligned {
		plan.Module = Rectangle{Width: ColonyGridInterior, Height: ColonyGridInterior}
		if target < FieldModuleCellCount {
			plan.Module.Height = ColonyGridSubCell
		} else {
			pick(moduleCandidates(false))
		}
		if plan.Cells < target {
			pick(moduleCandidates(true))
		}
	}
	// Fallback is the size ladder on the grid, or lone cells off it.
	before := len(plan.Patches)
	if plan.Cells < target {
		pick(candidates([]int32{4, 3, 2}))
	}
	if !aligned {
		before = len(plan.Patches)
	}
	if plan.Cells < target {
		pick(candidates([]int32{1}))
	}
	plan.Fallback = len(plan.Patches) > before
	plan.Unplanted = max(0, target-plan.Cells)
	return plan
}
