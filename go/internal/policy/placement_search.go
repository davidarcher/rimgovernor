package policy

import (
	"errors"
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
}

// PlacementSearch is a bounded native-grounded proposal set. It owns a copy
// of free geometry; callers cannot widen it after native previews are requested.
type PlacementSearch struct {
	snapshot domain.GenerationSnapshot
	tick     domain.Tick
	sites    []domain.Cell
	free     map[domain.Cell]bool
}

// NewPlacementSearch ports development.placement's nearest-cell ordering and
// bounded search. Unknown occupancy/zone/room facts never become free space.
func NewPlacementSearch(r PlacementSearchRequest) (PlacementSearch, error) {
	if r.Snapshot.Validate() != nil || r.Tick < 0 || r.Bounds.Width <= 0 || r.Bounds.Height <= 0 || r.Bounds.Width > 4096 || r.Bounds.Height > 4096 || r.Radius < 0 || r.Radius > 4096 || r.Limit < 1 || r.Limit > 64 || len(r.Cells) > 65536 || len(r.Protected) > 65536 {
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
	s := PlacementSearch{snapshot: r.Snapshot, tick: r.Tick, free: map[domain.Cell]bool{}}
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

func (s PlacementSearch) Candidates() []domain.Cell { return append([]domain.Cell(nil), s.sites...) }

// Select chooses the nearest fully inspected footprint for the requested native
// definition/stuff. Resource admission and authoritative competing holds still
// belong to the shared method store; a selected preview grants no execution.
func (s PlacementSearch) Select(definition, stuff string, previews []Preview) (Preview, bool, error) {
	if s.free == nil || len(previews) > 256 {
		return Preview{}, false, errors.New("invalid placement preview set")
	}
	if _, err := domain.NewBuilding(definition, domain.Cell{}, domain.North, stuff); err != nil {
		return Preview{}, false, err
	}
	ranks := map[domain.Cell]int{}
	for i, c := range s.sites {
		ranks[c] = i
	}
	seen := map[domain.ActionID]bool{}
	var selected Preview
	best := len(s.sites)
	for _, p := range previews {
		b, building := p.Action.Building()
		if !building || b.Definition() != definition || b.Stuff() != stuff || seen[p.Action.ID()] || !p.Snapshot.Matches(s.snapshot) || p.Tick != s.tick {
			return Preview{}, false, errors.New("placement preview differs from search")
		}
		seen[p.Action.ID()] = true
		rank, proposed := ranks[b.Cell()]
		if !proposed {
			return Preview{}, false, errors.New("unrequested placement anchor")
		}
		cells, known := p.Footprint.Value()
		if !positive(p.CanPlace) || !positive(p.SafeToPlace) || !known || len(cells) == 0 {
			continue
		}
		if len(cells) > 4096 {
			return Preview{}, false, errors.New("placement footprint exceeds bound")
		}
		valid, anchor := true, false
		used := map[domain.Cell]bool{}
		for _, c := range cells {
			if used[c] {
				return Preview{}, false, errors.New("duplicate placement footprint cell")
			}
			used[c] = true
			valid = valid && s.free[c]
			anchor = anchor || c == b.Cell()
		}
		if !valid || !anchor {
			continue
		}
		if rank < best || rank == best && p.Action.ID() < selected.Action.ID() {
			selected = p
			best = rank
		}
	}
	if best == len(s.sites) {
		return Preview{}, false, nil
	}
	cells, _ := selected.Footprint.Value()
	selected.Footprint = domain.Known(append([]domain.Cell(nil), cells...))
	if costs, known := selected.Costs.Value(); known {
		selected.Costs = domain.Known(append([]Amount(nil), costs...))
	}
	return selected, true, nil
}
