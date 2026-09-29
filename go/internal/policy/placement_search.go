package policy

import (
	"errors"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type PlacementEnvironment string

const (
	PlacementAnywhere PlacementEnvironment = "anywhere"
	PlacementIndoors  PlacementEnvironment = "indoors"
	PlacementOutdoors PlacementEnvironment = "outdoors"
)

type PlacementSearchRequest struct {
	Snapshot    domain.GenerationSnapshot
	Tick        domain.Tick
	Bounds      Bounds
	Center      domain.Cell
	Cells       []SiteCell
	Protected   []domain.Cell
	Environment PlacementEnvironment
	Radius      int32
	Limit       int
	// Anchors, when non-nil, limits the sites to these cells; footprints
	// may still cover any free cell.
	Anchors []domain.Cell
}

// PlacementSearch is a bounded native-grounded proposal set. It owns a copy
// of free geometry; callers cannot widen it after native previews are requested.
type PlacementSearch struct {
	snapshot domain.GenerationSnapshot
	tick     domain.Tick
	center   domain.Cell
	sites    []domain.Cell
	free     map[domain.Cell]bool
}

// PlacementScore explains why Select chose a footprint: Distance is the
// anchor's distance from the search center in cells, and Score equals it.
type PlacementScore struct {
	Anchor   domain.Cell
	Distance float64
	Score    float64
}

// NewPlacementSearch ports development.placement's nearest-cell ordering and
// bounded search. Unknown occupancy/zone/room facts never become free space.
func NewPlacementSearch(r PlacementSearchRequest) (PlacementSearch, error) {
	if r.Snapshot.Validate() != nil || r.Tick < 0 || r.Bounds.Width <= 0 || r.Bounds.Height <= 0 || r.Bounds.Width > 4096 || r.Bounds.Height > 4096 || r.Radius < 0 || r.Radius > 4096 || r.Limit < 1 || r.Limit > 64 {
		return PlacementSearch{}, errors.New("invalid placement search")
	}
	if r.Environment != PlacementAnywhere && r.Environment != PlacementIndoors && r.Environment != PlacementOutdoors {
		return PlacementSearch{}, errors.New("invalid placement environment")
	}
	inside := func(c domain.Cell) bool { return c.X >= 0 && c.Z >= 0 && c.X < r.Bounds.Width && c.Z < r.Bounds.Height }
	if !inside(r.Center) {
		return PlacementSearch{}, errors.New("invalid placement center")
	}
	blocked := map[domain.Cell]bool{}
	for _, c := range r.Protected {
		if !inside(c) {
			return PlacementSearch{}, errors.New("protected placement cell out of bounds")
		}
		blocked[c] = true
	}
	s := PlacementSearch{snapshot: r.Snapshot, tick: r.Tick, center: r.Center, free: map[domain.Cell]bool{}}
	var anchors map[domain.Cell]bool
	if r.Anchors != nil {
		anchors = map[domain.Cell]bool{}
		for _, c := range r.Anchors {
			anchors[c] = true
		}
	}
	seen := map[domain.Cell]bool{}
	for _, row := range r.Cells {
		c := row.Cell
		if !inside(c) || seen[c] {
			return PlacementSearch{}, errors.New("invalid placement cell census")
		}
		seen[c] = true
		occupied, occupiedKnown := row.Occupied.Value()
		zone, zoneKnown := row.Zone.Value()
		if blocked[c] || !positive(row.Walkable) || !occupiedKnown || occupied || !zoneKnown || zone {
			continue
		}
		if r.Environment != PlacementAnywhere {
			indoors, known := row.Indoors.Value()
			if !known || indoors != (r.Environment == PlacementIndoors) {
				continue
			}
		}
		s.free[c] = true
		if anchors != nil && !anchors[c] {
			continue
		}
		if c.X >= r.Center.X-r.Radius && c.X <= r.Center.X+r.Radius && c.Z >= r.Center.Z-r.Radius && c.Z <= r.Center.Z+r.Radius {
			s.sites = append(s.sites, c)
		}
	}
	sort.Slice(s.sites, func(i, j int) bool {
		a, b := s.sites[i], s.sites[j]
		da, db := squaredDistance(a, r.Center), squaredDistance(b, r.Center)
		if da != db {
			return da < db
		}
		return cellLess(a, b)
	})
	if len(s.sites) > r.Limit {
		s.sites = s.sites[:r.Limit]
	}
	return s, nil
}

// DoorwayAisles lists the four cells beside every observed doorway so indoor
// furnishing never blocks a room's entrance from either side; interaction
// cells of the furniture itself are the native preview's to check.
func DoorwayAisles(bounds Bounds, cells []SiteCell) []domain.Cell {
	var aisles []domain.Cell
	for _, row := range cells {
		if !positive(row.Doorway) {
			continue
		}
		c := row.Cell
		for _, n := range []domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}} {
			if n.X >= 0 && n.Z >= 0 && n.X < bounds.Width && n.Z < bounds.Height {
				aisles = append(aisles, n)
			}
		}
	}
	return aisles
}

func (s PlacementSearch) Candidates() []domain.Cell { return append([]domain.Cell(nil), s.sites...) }

// Select chooses the fully inspected footprint for the requested native
// definition/stuff nearest the search center;
// ties fall to the nearest-site rank, then the lowest action id. Resource
// admission and authoritative competing holds still belong to the shared
// method store; a selected preview grants no execution.
func (s PlacementSearch) Select(definition, stuff string, previews []Preview) (Preview, bool, error) {
	selected, _, ok, err := s.SelectScored(definition, stuff, previews)
	return selected, ok, err
}

// Score is the score Select gives a footprint anchored at the cell, for
// logs and tests.
func (s PlacementSearch) Score(anchor domain.Cell, footprint []domain.Cell) PlacementScore {
	score := PlacementScore{Anchor: anchor, Distance: math.Sqrt(float64(squaredDistance(anchor, s.center)))}
	score.Score = score.Distance
	return score
}

// SelectScored is Select with the chosen footprint's score.
func (s PlacementSearch) SelectScored(definition, stuff string, previews []Preview) (Preview, PlacementScore, bool, error) {
	if s.free == nil {
		return Preview{}, PlacementScore{}, false, errors.New("invalid placement preview set")
	}
	if _, err := domain.NewBuilding(definition, domain.Cell{}, domain.North, stuff); err != nil {
		return Preview{}, PlacementScore{}, false, err
	}
	ranks := map[domain.Cell]int{}
	for i, c := range s.sites {
		ranks[c] = i
	}
	seen := map[domain.ActionID]bool{}
	var selected Preview
	var chosen PlacementScore
	best := len(s.sites)
	for _, p := range previews {
		b, building := p.Action.Building()
		if !building || b.Definition() != definition || b.Stuff() != stuff || seen[p.Action.ID()] || !p.Snapshot.Matches(s.snapshot) {
			return Preview{}, PlacementScore{}, false, errors.New("placement preview differs from search")
		}
		seen[p.Action.ID()] = true
		rank, proposed := ranks[b.Cell()]
		if !proposed {
			return Preview{}, PlacementScore{}, false, errors.New("unrequested placement anchor")
		}
		cells, known := p.Footprint.Value()
		if !positive(p.CanPlace) || !positive(p.SafeToPlace) || !known || len(cells) == 0 {
			continue
		}
		valid, anchor := true, false
		used := map[domain.Cell]bool{}
		for _, c := range cells {
			if used[c] {
				return Preview{}, PlacementScore{}, false, errors.New("duplicate placement footprint cell")
			}
			used[c] = true
			valid = valid && s.free[c]
			anchor = anchor || c == b.Cell()
		}
		if !valid || !anchor {
			continue
		}
		score := s.Score(b.Cell(), cells)
		if best == len(s.sites) || score.Score < chosen.Score || score.Score == chosen.Score && (rank < best || rank == best && p.Action.ID() < selected.Action.ID()) {
			selected, chosen = p, score
			best = rank
		}
	}
	if best == len(s.sites) {
		return Preview{}, PlacementScore{}, false, nil
	}
	cells, _ := selected.Footprint.Value()
	selected.Footprint = domain.Known(append([]domain.Cell(nil), cells...))
	if costs, known := selected.Costs.Value(); known {
		selected.Costs = domain.Known(append([]Amount(nil), costs...))
	}
	return selected, chosen, true, nil
}

// cellsRectangle is the bounding rectangle of a non-empty cell set.
func cellsRectangle(cells []domain.Cell) Rectangle {
	minX, minZ, maxX, maxZ := cells[0].X, cells[0].Z, cells[0].X, cells[0].Z
	for _, c := range cells[1:] {
		minX, maxX, minZ, maxZ = min(minX, c.X), max(maxX, c.X), min(minZ, c.Z), max(maxZ, c.Z)
	}
	return Rectangle{X: minX, Z: minZ, Width: maxX - minX + 1, Height: maxZ - minZ + 1}
}
