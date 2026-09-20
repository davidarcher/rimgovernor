package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TidyLayout re-sites a settled colony's early off-grid sprawl one item at
// a time (#611): a managed field patch smaller than the module or off the
// grid is re-zoned onto the nearest free module in Fields keeping its crop,
// a managed stockpile off the grid is re-sited, and a Camp shell whose
// same-role replacement module stands complete and empty is deconstructed.
// It ranks below every production, upkeep and defense goal (maintenance
// priority, no deficit) and is active only at tier >= Masonry, with a known
// grid, when the colony has no unfilled construction or hauling work. It
// never dissolves a room in use, never touches an unmanaged (player) zone
// or building, holds one re-site in flight and never re-sites an item
// already tidied.
const TidyLayout GoalID = "TidyLayout"

// tidyPriority ranks TidyLayout last: the lowest goal rank, with no deficit
// so it never outscores a deficit-bearing goal for a slot.
const tidyPriority = 4

// tidyDeficit is the deficit fraction a standing proposal ranks with: under
// any partial deficit of a goal doing real work, above zero so the slot an
// idle colony leaves free still falls to the tidy.
const tidyDeficit = 0.05

// TidyKind is the kind of item one re-site moves.
type TidyKind string

const (
	TidyField     TidyKind = "field"
	TidyStockpile TidyKind = "stockpile"
	TidyShell     TidyKind = "shell"
)

// TidyItem is one managed item the review measures against the grid: a
// zone by native id with its bounding footprint and cell count, or a Camp
// shell by room id with its exterior footprint. Managed marks an item this
// colony created (a zone from a completed zone_create, a shell of claimed
// walls); an unmanaged item is never a candidate. InUse marks a shell room
// holding beds or contents; Replaced marks a shell whose same-role
// replacement module stands complete and empty.
type TidyItem struct {
	Kind      TidyKind
	ID        string
	Footprint Rectangle
	Cells     int
	Crop      string
	Managed   bool
	InUse     bool
	Replaced  bool
}

// TidyRequest is the review's input: the tier and grid gates, whether the
// colony is busy (unknown counts as busy), the items, the ids already
// tidied or in flight, and the observed cells a new site is chosen from.
type TidyRequest struct {
	Tier  domain.Fact[BuildTier]
	Grid  domain.Fact[ColonyGrid]
	Busy  domain.Fact[bool]
	Items []TidyItem
	// Tidied lists item ids a re-site already moved or is moving; InFlight
	// reports one still moving, which holds every new proposal.
	Tidied   []string
	InFlight bool
	Bounds   Bounds
	Cells    []SiteCell
	// Protected cells are never zoned: accepted footprints, aisles, routes.
	Protected []domain.Cell
	// Extent scopes the districts; unknown uses the grid's default ring.
	Extent domain.Fact[ColonyExtent]
}

// TidyProposal is the one re-site the review proposes: the item, the
// on-grid rectangle it moves to (empty for a shell, which is deconstructed),
// the alignment gain (C2 CornerError before minus after) and the distance
// the site moves, with a one-line explanation.
type TidyProposal struct {
	Item        TidyItem
	Target      Rectangle
	Gain        int
	Distance    int32
	Explanation string
}

// TidyReview is the review outcome: Known once the gates were evaluated
// against known facts, Active while a proposal stands, and Reason naming
// why none does.
type TidyReview struct {
	Known    bool
	Active   bool
	Proposal domain.Fact[TidyProposal]
	Reason   string
	// Candidates counts the managed items measured off the grid.
	Candidates int
}

// tidyAlignment is the C2 CornerError an item carries: a shell's exterior
// corner measured to the grid lines, a zone's corner measured to the
// sub-cell corners of the module interior it should fill (offsets 1 and 7
// from a grid line, the interior's own corner and its half-module split),
// so a zone filling a module interior or one of its sub-cells scores zero.
func tidyAlignment(grid ColonyGrid, item TidyItem) int {
	if item.Kind == TidyShell {
		return grid.CornerError(item.Footprint)
	}
	if !grid.Valid() {
		return 0
	}
	u, v := grid.local(domain.Cell{X: item.Footprint.X, Z: item.Footprint.Z})
	corner := func(a int32) int {
		r := floorMod(a, grid.Pitch)
		const split = ColonyGridSubCell + 2
		return int(min(absInt32(r-1), absInt32(r-split), grid.Pitch+1-r))
	}
	return corner(u) + corner(v)
}

// tidyCandidate reports whether an item is worth re-siting: a managed
// item off its grid alignment, or a managed field patch smaller than a
// half module, that is not tidied, not in use and (for a shell) replaced.
func tidyCandidate(grid ColonyGrid, item TidyItem, tidied map[string]bool) bool {
	if !item.Managed || item.ID == "" || tidied[item.ID] || item.Footprint.Width <= 0 || item.Footprint.Height <= 0 {
		return false
	}
	offGrid := tidyAlignment(grid, item) > 0
	switch item.Kind {
	case TidyField:
		return offGrid || item.Cells > 0 && item.Cells < FieldHalfModuleCellCount
	case TidyStockpile:
		return offGrid
	case TidyShell:
		return offGrid && !item.InUse && item.Replaced
	}
	return false
}

// tidyModuleSize is the sub-cell a re-sited zone takes: the C5 field rule
// (a half module under FieldHalfModuleBelow cells, else the whole interior)
// for a field; the smallest sub-cell holding the stockpile's cells.
func tidyModuleSize(item TidyItem) (width, height int32) {
	if item.Kind == TidyField {
		if item.Cells < FieldHalfModuleBelow {
			return ColonyGridInterior, ColonyGridSubCell
		}
		return ColonyGridInterior, ColonyGridInterior
	}
	switch {
	case item.Cells <= int(ColonyGridSubCell*ColonyGridSubCell):
		return ColonyGridSubCell, ColonyGridSubCell
	case item.Cells <= FieldHalfModuleCellCount:
		return ColonyGridInterior, ColonyGridSubCell
	}
	return ColonyGridInterior, ColonyGridInterior
}

// PlanTidyLayout measures the items against the grid and proposes at most
// one re-site: the candidate with the largest alignment gain, ties to the
// nearest free module (its own district first, then any) and then the
// lowest id. A shell proposal has no target. Deterministic over its input.
func PlanTidyLayout(r TidyRequest) TidyReview {
	tier, tk := r.Tier.Value()
	grid, gk := r.Grid.Value()
	if !tk {
		return TidyReview{Reason: "build tier unknown"}
	}
	if tier < BuildTierMasonry {
		return TidyReview{Known: true, Reason: "camp tier keeps its layout"}
	}
	if !gk || !grid.Valid() {
		return TidyReview{Reason: "no colony grid"}
	}
	tidied := map[string]bool{}
	for _, id := range r.Tidied {
		tidied[id] = true
	}
	var candidates []TidyItem
	for _, item := range r.Items {
		if tidyCandidate(grid, item, tidied) {
			candidates = append(candidates, item)
		}
	}
	review := TidyReview{Known: true, Candidates: len(candidates)}
	if len(candidates) == 0 {
		review.Reason = "nothing off grid"
		return review
	}
	if r.InFlight {
		review.Reason = "re-site in flight"
		return review
	}
	busy, bk := r.Busy.Value()
	if !bk || busy {
		review.Reason = "colony busy"
		return review
	}
	sort.Slice(candidates, func(i, j int) bool { return candidates[i].ID < candidates[j].ID })
	districts := DistrictsFor(grid, r.Extent)
	sites := newTidySites(grid, r)
	var best *TidyProposal
	for _, item := range candidates {
		proposal, ok := tidyProposal(grid, districts, sites, item)
		if !ok {
			continue
		}
		if best == nil || proposal.Gain > best.Gain || proposal.Gain == best.Gain && proposal.Distance < best.Distance {
			p := proposal
			best = &p
		}
	}
	if best == nil {
		review.Reason = "no free module"
		return review
	}
	review.Active, review.Proposal = true, domain.Known(*best)
	return review
}

// tidySites indexes the observed cells a re-sited zone may take.
type tidySites struct {
	cells     map[domain.Cell]SiteCell
	protected map[domain.Cell]bool
	bounds    Bounds
}

func newTidySites(grid ColonyGrid, r TidyRequest) tidySites {
	s := tidySites{cells: make(map[domain.Cell]SiteCell, len(r.Cells)), protected: map[domain.Cell]bool{}, bounds: r.Bounds}
	for _, c := range r.Cells {
		s.cells[c.Cell] = c
	}
	for _, c := range r.Protected {
		s.protected[c] = true
	}
	return s
}

// open reports a cell a zone may take: observed, walkable, unoccupied,
// unzoned, unprotected and, for a field, of known positive fertility.
func (s tidySites) open(p domain.Cell, field bool) bool {
	c, ok := s.cells[p]
	if !ok || s.protected[p] || p.X < 0 || p.Z < 0 || p.X >= s.bounds.Width || p.Z >= s.bounds.Height {
		return false
	}
	walkable, wk := c.Walkable.Value()
	occupied, ok := c.Occupied.Value()
	zone, zk := c.Zone.Value()
	if !(wk && walkable && ok && !occupied && zk && !zone) {
		return false
	}
	if field {
		fertility, fk := c.Fertility.Value()
		return fk && fertility > 0
	}
	return true
}

func (s tidySites) free(rect Rectangle, field bool) bool {
	for x := rect.X; x < rect.X+rect.Width; x++ {
		for z := rect.Z; z < rect.Z+rect.Height; z++ {
			if !s.open(domain.Cell{X: x, Z: z}, field) {
				return false
			}
		}
	}
	return true
}

// tidyProposal chooses the item's new site: for a zone the nearest free
// sub-cell of the wanted size in the item's district, then in any
// district; for a replaced shell the deconstruction itself.
func tidyProposal(grid ColonyGrid, districts Districts, sites tidySites, item TidyItem) (TidyProposal, bool) {
	before := tidyAlignment(grid, item)
	if item.Kind == TidyShell {
		return TidyProposal{Item: item, Gain: before, Explanation: fmt.Sprintf("%s %s (%dx%d at %d,%d, corner error %d): replacement module complete and empty, deconstruct; alignment gain %d", item.Kind, item.ID, item.Footprint.Width, item.Footprint.Height, item.Footprint.X, item.Footprint.Z, before, before)}, true
	}
	width, height := tidyModuleSize(item)
	district := DistrictFields
	if item.Kind == TidyStockpile {
		district = DistrictStorage
	}
	centre := domain.Cell{X: item.Footprint.X + item.Footprint.Width/2, Z: item.Footprint.Z + item.Footprint.Height/2}
	type option struct {
		rect     Rectangle
		distance int32
		own      bool
	}
	var options []option
	seen := map[Rectangle]bool{}
	for c := range sites.cells {
		module := grid.Module(c)
		if seen[module] {
			continue
		}
		seen[module] = true
		for _, sub := range grid.SubCells(module) {
			sized := sub.Width == width && sub.Height == height || sub.Width == height && sub.Height == width
			if !sized || !sites.free(sub, item.Kind == TidyField) {
				continue
			}
			mid := domain.Cell{X: sub.X + sub.Width/2, Z: sub.Z + sub.Height/2}
			d := absInt32(mid.X-centre.X) + absInt32(mid.Z-centre.Z)
			options = append(options, option{sub, d, districts.District(mid) == district})
		}
	}
	if len(options) == 0 {
		return TidyProposal{}, false
	}
	sort.Slice(options, func(i, j int) bool {
		a, b := options[i], options[j]
		if a.own != b.own {
			return a.own
		}
		if a.distance != b.distance {
			return a.distance < b.distance
		}
		return cellLess(domain.Cell{X: a.rect.X, Z: a.rect.Z}, domain.Cell{X: b.rect.X, Z: b.rect.Z})
	})
	pick := options[0]
	after := tidyAlignment(grid, TidyItem{Kind: item.Kind, Footprint: pick.rect})
	// A field patch under a half module gains a module's worth of cells
	// too; the corner error is the alignment evidence either way.
	gain := before - after
	where := "in " + string(district)
	if !pick.own {
		where = "outside " + string(district) + " (district full)"
	}
	crop := ""
	if item.Crop != "" {
		crop = ", crop " + item.Crop + " kept"
	}
	explanation := fmt.Sprintf("%s %s (%dx%d at %d,%d, %d cells, corner error %d) -> %dx%d at %d,%d on grid (error %d) %s: alignment gain %d, %d cells away%s",
		item.Kind, item.ID, item.Footprint.Width, item.Footprint.Height, item.Footprint.X, item.Footprint.Z, item.Cells, before, pick.rect.Width, pick.rect.Height, pick.rect.X, pick.rect.Z, after, where, gain, pick.distance, crop)
	return TidyProposal{Item: item, Target: pick.rect, Gain: gain, Distance: pick.distance, Explanation: explanation}, true
}

func absInt32(v int32) int32 {
	if v < 0 {
		return -v
	}
	return v
}
