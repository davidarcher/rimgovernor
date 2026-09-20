package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type ExtentOrigin string

const (
	ExtentFacility         ExtentOrigin = "facility"
	ExtentEnclosedInterior ExtentOrigin = "enclosed_interior"
	ExtentCorridor         ExtentOrigin = "corridor"
	ExtentMargin           ExtentOrigin = "margin"
)

// HomeExtentGeometry is complete, unbatched geometry for one building, using
// the same enclosed roofed interiors and internal-door connectivity as Home.
// A known empty value means only the occupied footprint belongs. Corridors
// must be observed connecting geometry, never interpolated between facilities.
type HomeExtentGeometry struct {
	EnclosedInterior []domain.Cell
	Corridor         []domain.Cell
}

type ExtentProvenance struct {
	Origin   ExtentOrigin
	Facility string
	Plan     domain.PlanID
	Action   domain.ActionID
	Goal     domain.GoalID
}

type ExtentCell struct {
	Cell       domain.Cell
	Provenance []ExtentProvenance
}

// ColonyExtent regions are four-neighbor components of observed geometry.
// Margins belong to their original component: overlapping margins never merge
// regions. Cells and provenance are sorted; no returned slice aliases inputs.
type ColonyExtent struct{ Regions []ExtentRegion }
type ExtentRegion struct{ Cells []ExtentCell }

type ColonyExtentRequest struct {
	Bounds       domain.Fact[Bounds]
	Construction domain.Fact[CurrentConstruction]
	Claims       domain.Fact[[]ConstructionClaim]
	Stockpiles   domain.Fact[[]OwnedStockpile]
	Home         domain.Fact[HomeCoverageObservation]
	// Margin is a Chebyshev radius, from zero through eight cells.
	Margin int32
}

const extentCellLimit = 65536

func DeriveColonyExtent(r ColonyExtentRequest) (domain.Fact[ColonyExtent], error) {
	unknown := domain.Unknown[ColonyExtent]()
	invalid := errors.New("invalid colony extent geometry or bounds")
	bounds, bk := r.Bounds.Value()
	census, ck := r.Construction.Value()
	zones, zk := r.Stockpiles.Value()
	home, hk := r.Home.Value()
	if r.Margin < 0 || r.Margin > 8 {
		return unknown, invalid
	}
	if !bk || !ck || !zk || !hk || !census.Colony {
		return unknown, nil
	}
	if bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > 4096 || bounds.Height > 4096 {
		return unknown, invalid
	}
	// Missing footprints are incomplete evidence, not a known empty colony.
	for _, b := range census.Buildings {
		if len(b.Cells) == 0 {
			return unknown, nil
		}
	}
	for _, z := range zones {
		if len(z.Cells) == 0 {
			return unknown, nil
		}
	}
	// Ambiguous optional history cannot select provenance by input order.
	history, _ := r.Claims.Value()
	claimIDs := map[string]bool{}
	for _, claim := range history {
		if claimIDs[claim.Identity.Current] {
			return unknown, errors.New("ambiguous colony extent construction provenance")
		}
		claimIDs[claim.Identity.Current] = true
	}
	owned, err := OwnedConstructions(r.Claims, r.Construction)
	if err != nil {
		return unknown, err
	}
	if _, err := ReviewHomeCoverage(owned, r.Stockpiles, r.Home); err != nil {
		return unknown, err
	}
	buildings, _ := owned.Value()
	if home.Revision < 0 || len(home.Targets) > 256 {
		return unknown, invalid
	}
	targets := map[string]HomeCoverageTarget{}
	for _, target := range home.Targets {
		if !foodID(target.ID) {
			return unknown, invalid
		}
		if _, exists := targets[target.ID]; exists {
			return unknown, invalid
		}
		targets[target.ID] = target
	}
	inBounds := func(c domain.Cell) bool { return c.X >= 0 && c.Z >= 0 && c.X < bounds.Width && c.Z < bounds.Height }
	cells := map[domain.Cell]map[ExtentProvenance]bool{}
	add := func(c domain.Cell, p ExtentProvenance) bool {
		if !inBounds(c) {
			return false
		}
		if cells[c] == nil {
			cells[c] = map[ExtentProvenance]bool{}
		}
		cells[c][p] = true
		return len(cells) <= extentCellLimit
	}
	for _, b := range buildings {
		target, exists := targets[b.Identity.Current]
		geometry, known := target.ExtentGeometry.Value()
		if !exists || !known || target.Blocker != "" {
			return unknown, nil
		}
		p := ExtentProvenance{Origin: ExtentFacility, Facility: b.Identity.Current, Plan: b.Plan, Action: b.Action, Goal: b.Goal}
		local := map[domain.Cell]bool{}
		for _, group := range []struct {
			origin ExtentOrigin
			cells  []domain.Cell
		}{
			{ExtentFacility, b.Cells}, {ExtentEnclosedInterior, geometry.EnclosedInterior}, {ExtentCorridor, geometry.Corridor},
		} {
			if len(group.cells) > extentCellLimit {
				return unknown, invalid
			}
			p.Origin = group.origin
			seen := map[domain.Cell]bool{}
			for _, c := range group.cells {
				if seen[c] || !add(c, p) {
					return unknown, invalid
				}
				seen[c], local[c] = true, true
			}
		}
		// A target cannot smuggle an unconnected island into its interior.
		queue := append([]domain.Cell{}, b.Cells...)
		for _, c := range queue {
			delete(local, c)
		}
		for i := 0; i < len(queue); i++ {
			for _, next := range extentNeighbors(queue[i]) {
				if local[next] {
					delete(local, next)
					queue = append(queue, next)
				}
			}
		}
		if len(local) != 0 {
			return unknown, invalid
		}
	}
	for _, z := range zones {
		for _, c := range z.Cells {
			if !add(c, ExtentProvenance{Origin: ExtentFacility, Facility: z.ID}) {
				return unknown, invalid
			}
		}
	}
	ordered := make([]domain.Cell, 0, len(cells))
	for c := range cells {
		ordered = append(ordered, c)
	}
	sort.Slice(ordered, func(i, j int) bool { return extentCellLess(ordered[i], ordered[j]) })
	visited := map[domain.Cell]bool{}
	result := ColonyExtent{Regions: []ExtentRegion{}}
	total := 0
	for _, seed := range ordered {
		if visited[seed] {
			continue
		}
		queue := []domain.Cell{seed}
		visited[seed] = true
		for i := 0; i < len(queue); i++ {
			for _, next := range extentNeighbors(queue[i]) {
				if cells[next] != nil && !visited[next] {
					visited[next] = true
					queue = append(queue, next)
				}
			}
		}
		region := map[domain.Cell]map[ExtentProvenance]bool{}
		for _, c := range queue {
			region[c] = cells[c]
		}
		for _, c := range queue {
			for dx := -r.Margin; dx <= r.Margin; dx++ {
				for dz := -r.Margin; dz <= r.Margin; dz++ {
					next := domain.Cell{X: c.X + dx, Z: c.Z + dz}
					if !inBounds(next) || cells[next] != nil {
						continue
					}
					if region[next] == nil {
						region[next] = map[ExtentProvenance]bool{{Origin: ExtentMargin}: true}
					}
				}
			}
			if total+len(region) > extentCellLimit {
				return unknown, invalid
			}
		}
		out := ExtentRegion{Cells: make([]ExtentCell, 0, len(region))}
		for c, reasons := range region {
			row := ExtentCell{Cell: c, Provenance: make([]ExtentProvenance, 0, len(reasons))}
			for p := range reasons {
				row.Provenance = append(row.Provenance, p)
			}
			sort.Slice(row.Provenance, func(i, j int) bool {
				a, b := row.Provenance[i], row.Provenance[j]
				if a.Origin != b.Origin {
					return a.Origin < b.Origin
				}
				return a.Facility < b.Facility
			})
			out.Cells = append(out.Cells, row)
		}
		sort.Slice(out.Cells, func(i, j int) bool { return extentCellLess(out.Cells[i].Cell, out.Cells[j].Cell) })
		result.Regions = append(result.Regions, out)
		total += len(region)
	}
	return domain.Known(result), nil
}

func extentCellLess(a, b domain.Cell) bool { return a.X < b.X || a.X == b.X && a.Z < b.Z }
func extentNeighbors(c domain.Cell) [4]domain.Cell {
	return [4]domain.Cell{{X: c.X - 1, Z: c.Z}, {X: c.X + 1, Z: c.Z}, {X: c.X, Z: c.Z - 1}, {X: c.X, Z: c.Z + 1}}
}
