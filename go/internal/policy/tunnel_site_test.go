package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// mountainSite builds a 30×30 map: x < 10 is open ground, x >= 10 is visible
// granite for two cells deep, and everything beyond is fogged (absent).
func mountainSite() ExcavationSiteRequest {
	var cells []SiteCell
	for x := int32(0); x < 12; x++ {
		for z := int32(0); z < 30; z++ {
			c := domain.Cell{X: x, Z: z}
			if x < 10 {
				cells = append(cells, SiteCell{Cell: c, Walkable: domain.Known(true), Occupied: domain.Known(false), Roofed: domain.Known(false)})
			} else {
				cells = append(cells, SiteCell{Cell: c, Walkable: domain.Known(false), Occupied: domain.Known(true), Roofed: domain.Known(true), Roof: domain.Known("RoofRockThick")})
			}
		}
	}
	return ExcavationSiteRequest{Bounds: Bounds{Width: 30, Height: 30}, Region: Rectangle{X: 0, Z: 0, Width: 30, Height: 30}, Anchor: domain.Cell{X: 5, Z: 15}, Cells: cells, MinCorridor: 1, MaxCorridor: 4}
}

var mountainOre = domain.Cell{X: 14, Z: 15}

func mountainTunnel(t *testing.T) ExcavationTarget {
	t.Helper()
	targets, err := CorridorExcavationSites(mountainSite(), mountainOre)
	if err != nil || len(targets) == 0 {
		t.Fatal(targets, err)
	}
	return targets[0]
}

func TestCorridorExcavationSitesReachesBuriedOre(t *testing.T) {
	r := mountainSite()
	targets, err := CorridorExcavationSites(r, mountainOre)
	if err != nil {
		t.Fatal(err)
	}
	if len(targets) == 0 {
		t.Fatal("no corridor")
	}
	best := targets[0]
	if best.Access != (domain.Cell{X: 9, Z: 15}) || len(best.Corridor) != 4 || best.Door != (domain.Cell{X: 13, Z: 15}) || len(best.Cells()) != 4 {
		t.Fatal(best)
	}
	for _, target := range targets {
		dx, dz := target.Door.X-mountainOre.X, target.Door.Z-mountainOre.Z
		if dx*dx+dz*dz != 1 || !excavationSupported(target.Cells()) {
			t.Fatal("not adjacent or unsupported", target)
		}
		for _, c := range target.Corridor {
			if c == mountainOre {
				t.Fatal("corridor through ore", target)
			}
		}
		parsed, err := ParseExcavationKey(target.Key())
		if err != nil {
			t.Fatal(target.Key(), err)
		}
		if parsed.Key() != target.Key() || parsed.Door != target.Door || len(parsed.Corridor) != len(target.Corridor) {
			t.Fatal(parsed, target)
		}
	}
	// Out of corridor reach: nothing is proposed.
	if far, err := CorridorExcavationSites(r, domain.Cell{X: 25, Z: 15}); err != nil || len(far) != 0 {
		t.Fatal(far, err)
	}
	for _, bad := range []string{"9.15.1.0.4.c", "9.15.1.1.4", "9.15.1.0.0", "9.15.1.0", "-1.15.1.0.4"} {
		if _, err := ParseExcavationKey(bad); err == nil {
			t.Fatal("accepted", bad)
		}
	}
}

func TestCorridorExcavationSitesRejectsOpenNeighbour(t *testing.T) {
	r := mountainSite()
	// A visible open pocket beside the corridor unseals it.
	for i, c := range r.Cells {
		if c.Cell == (domain.Cell{X: 11, Z: 16}) {
			r.Cells[i].Walkable, r.Cells[i].Occupied = domain.Known(true), domain.Known(false)
		}
	}
	targets, err := CorridorExcavationSites(r, mountainOre)
	if err != nil {
		t.Fatal(err)
	}
	for _, target := range targets {
		if target.Access == (domain.Cell{X: 9, Z: 15}) {
			t.Fatal("unsealed corridor proposed", target)
		}
	}
}

func TestCorridorExcavationSitesValidation(t *testing.T) {
	r := mountainSite()
	r.MaxCorridor = 0
	if _, err := CorridorExcavationSites(r, mountainOre); err == nil {
		t.Fatal("accepted an empty corridor range")
	}
	r = mountainSite()
	r.Bounds = Bounds{}
	if _, err := CorridorExcavationSites(r, mountainOre); err == nil {
		t.Fatal("accepted empty bounds")
	}
}

func TestCorridorExcavationSupport(t *testing.T) {
	var long []domain.Cell
	for x := int32(0); x < 40; x++ {
		long = append(long, domain.Cell{X: x})
	}
	if !excavationSupported(long) {
		t.Fatal("1-wide corridor must be supported per cell")
	}
}

func excavationStates(target ExcavationTarget, cleared, visible int) []ExcavationCellState {
	var out []ExcavationCellState
	for i, c := range target.Cells() {
		s := ExcavationCellState{Cell: c, Rock: "Granite"}
		switch {
		case i < cleared:
			s.Cleared = true
		case i < visible:
			s.Eligible = true
		default:
			s.Fogged = true
		}
		out = append(out, s)
	}
	return out
}

func TestExcavationReviewStagesFrontier(t *testing.T) {
	target := mountainTunnel(t)
	r := ReviewExcavation(target, excavationStates(target, 0, 2), 8)
	if len(r.Stage) != 2 || r.Stage[0] != target.Corridor[0] || r.Remaining != 2 || !r.Unknown || !r.Corridor || r.Complete {
		t.Fatal(r)
	}
	// The stage cap holds.
	r = ReviewExcavation(target, excavationStates(target, 0, 4), 3)
	if len(r.Stage) != 3 || r.Remaining != 1 || r.Unknown || r.Complete {
		t.Fatal(r)
	}
	r = ReviewExcavation(target, excavationStates(target, 4, 4), 8)
	if len(r.Stage) != 0 || r.Remaining != 0 || r.Unknown || !r.Complete || !r.Corridor {
		t.Fatal(r)
	}
	if r = ReviewExcavation(target, nil, 8); len(r.Stage) != 0 || r.Remaining != 4 || !r.Unknown || r.Complete {
		t.Fatal(r)
	}
}

func TestExcavationReviewBlockedCorridorNeedsResiting(t *testing.T) {
	target := mountainTunnel(t)
	s := excavationStates(target, 1, 4)
	s[2].Eligible, s[2].Blocked = false, true
	r := ReviewExcavation(target, s, 8)
	if r.Corridor || r.Complete || len(r.Kept) != 1 || r.Kept[0] != target.Corridor[2] {
		t.Fatal(r)
	}
}
