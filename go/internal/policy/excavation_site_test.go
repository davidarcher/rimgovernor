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
	return ExcavationSiteRequest{Bounds: Bounds{Width: 30, Height: 30}, Region: Rectangle{X: 0, Z: 0, Width: 30, Height: 30}, Anchor: domain.Cell{X: 5, Z: 15}, Cells: cells, Interior: Bounds{Width: 7, Height: 7}, MinCorridor: 2, MaxCorridor: 4}
}

func TestExcavationSitesFindsFaceNearAnchor(t *testing.T) {
	targets, err := ExcavationSites(mountainSite())
	if err != nil {
		t.Fatal(err)
	}
	if len(targets) == 0 {
		t.Fatal("no targets")
	}
	best := targets[0]
	if best.Access != (domain.Cell{X: 9, Z: 15}) || best.Direction != (domain.Cell{X: 1, Z: 0}) || len(best.Corridor) != 2 {
		t.Fatal(best)
	}
	if best.Corridor[0] != (domain.Cell{X: 10, Z: 15}) || best.Door != (domain.Cell{X: 11, Z: 15}) {
		t.Fatal(best)
	}
	if best.Interior != (Rectangle{X: 12, Z: 12, Width: 7, Height: 7}) {
		t.Fatal(best.Interior)
	}
	cells := best.Cells()
	if len(cells) != 51 || cells[0] != best.Corridor[0] || cells[1] != best.Door || cells[2] != (domain.Cell{X: 12, Z: 15}) {
		t.Fatal(cells[:3], len(cells))
	}
	if len(targets) > 12 {
		t.Fatal(len(targets))
	}
	for i := 1; i < len(targets); i++ {
		if targets[i].Score < targets[i-1].Score {
			t.Fatal("unsorted", targets[i-1].Score, targets[i].Score)
		}
	}
}

func TestExcavationKeyRoundTrip(t *testing.T) {
	targets, _ := ExcavationSites(mountainSite())
	for _, target := range targets {
		parsed, err := ParseExcavationKey(target.Key())
		if err != nil {
			t.Fatal(target.Key(), err)
		}
		parsed.Score = target.Score
		if parsed.Key() != target.Key() || parsed.Door != target.Door || len(parsed.Corridor) != len(target.Corridor) || parsed.Interior != target.Interior {
			t.Fatal(parsed, target)
		}
	}
	for _, bad := range []string{"", "1.2.3", "9.15.1.0.2.12.12.7.7.x", "9.15.1.1.2.12.12.7.7", "9.15.1.0.0.12.12.7.7", "9.15.1.0.2.12.12.12.7", "9.15.1.0.2.13.12.7.7", "-1.15.1.0.2.12.12.7.7", "09.15.1.0.2.12.12.7.7"} {
		if _, err := ParseExcavationKey(bad); err == nil {
			t.Fatal("accepted", bad)
		}
	}
}

func TestExcavationSitesRejectsKnownOpenCellInsideOrBeside(t *testing.T) {
	// A known walkable pocket inside the intended interior rejects that target.
	r := mountainSite()
	r.Cells = append(r.Cells, SiteCell{Cell: domain.Cell{X: 14, Z: 15}, Walkable: domain.Known(true), Occupied: domain.Known(false)})
	targets, err := ExcavationSites(r)
	if err != nil {
		t.Fatal(err)
	}
	for _, target := range targets {
		for _, c := range target.Cells() {
			if c == (domain.Cell{X: 14, Z: 15}) {
				t.Fatal("target through open cell", target)
			}
		}
	}
	// A known walkable cell touching the room from outside also rejects.
	r = mountainSite()
	r.Cells = append(r.Cells, SiteCell{Cell: domain.Cell{X: 19, Z: 15}, Walkable: domain.Known(true), Occupied: domain.Known(false)})
	targets, _ = ExcavationSites(r)
	for _, target := range targets {
		if target.Access == (domain.Cell{X: 9, Z: 15}) && len(target.Corridor) == 2 {
			t.Fatal("room borders an open cell", target)
		}
	}
	// Protected cells are never dug.
	r = mountainSite()
	r.Protected = []domain.Cell{{X: 10, Z: 15}}
	targets, _ = ExcavationSites(r)
	for _, target := range targets {
		if target.Corridor[0] == (domain.Cell{X: 10, Z: 15}) {
			t.Fatal("protected face used", target)
		}
	}
}

func TestExcavationSitesRequiresRockRoof(t *testing.T) {
	r := mountainSite()
	for i := range r.Cells {
		if r.Cells[i].Cell.X >= 10 {
			r.Cells[i].Roof = domain.Known("RoofConstructed")
		}
	}
	targets, err := ExcavationSites(r)
	if err != nil || len(targets) != 0 {
		t.Fatal(targets, err)
	}
}

func TestExcavationSitesStaysInsideObservedRegion(t *testing.T) {
	r := mountainSite()
	r.Region = Rectangle{X: 0, Z: 0, Width: 16, Height: 30}
	targets, err := ExcavationSites(r)
	if err != nil {
		t.Fatal(err)
	}
	for _, target := range targets {
		for _, c := range target.Cells() {
			if c.X >= 15 {
				t.Fatal("target touches unobserved cells", target)
			}
		}
	}
	r.Region = Rectangle{X: 0, Z: 0, Width: 40, Height: 30}
	if _, err := ExcavationSites(r); err == nil {
		t.Fatal("region beyond bounds accepted")
	}
}

func TestExcavationSitesAvoidsMapEdge(t *testing.T) {
	r := mountainSite()
	r.Bounds = Bounds{Width: 16, Height: 30}
	r.Region = Rectangle{X: 0, Z: 0, Width: 16, Height: 30}
	targets, err := ExcavationSites(r)
	if err != nil {
		t.Fatal(err)
	}
	for _, target := range targets {
		for _, c := range target.Cells() {
			if c.X >= 15 || c.Z <= 0 || c.Z >= 29 {
				t.Fatal("edge cell", target)
			}
		}
	}
}

func TestExcavationSitesValidation(t *testing.T) {
	r := mountainSite()
	r.Interior = Bounds{Width: 12, Height: 7}
	if _, err := ExcavationSites(r); err == nil {
		t.Fatal("support span accepted")
	}
	r = mountainSite()
	r.Interior = Bounds{Width: 8, Height: 8}
	if _, err := ExcavationSites(r); err == nil {
		t.Fatal("oversized target accepted")
	}
	r = mountainSite()
	r.Interior = Bounds{Width: 11, Height: 5}
	r.MaxCorridor = 9
	if _, err := ExcavationSites(r); err != nil {
		t.Fatal(err)
	}
	r = mountainSite()
	r.Cells = append(r.Cells, r.Cells[0])
	if _, err := ExcavationSites(r); err == nil {
		t.Fatal("duplicate accepted")
	}
}

func TestChooseExcavation(t *testing.T) {
	anchor := domain.Cell{X: 5, Z: 15}
	site := &ExcavationTarget{Interior: Rectangle{X: 12, Z: 12, Width: 7, Height: 7}}
	if ChooseExcavation(anchor, nil, nil) {
		t.Fatal("nothing to choose")
	}
	if !ChooseExcavation(anchor, nil, site) {
		t.Fatal("no shell means dig")
	}
	near := &StarterLayout{Room: Rectangle{X: 2, Z: 12, Width: 9, Height: 9}}
	far := &StarterLayout{Room: Rectangle{X: 60, Z: 60, Width: 9, Height: 9}}
	if !ChooseExcavation(anchor, near, site) {
		t.Fatal("a rock room within reach should win over any shell")
	}
	distant := &ExcavationTarget{Interior: Rectangle{X: 40, Z: 40, Width: 7, Height: 7}}
	if ChooseExcavation(anchor, near, distant) {
		t.Fatal("nearer shell should win over a distant dig")
	}
	if !ChooseExcavation(anchor, far, distant) {
		t.Fatal("nearer dig should win over a distant shell")
	}
	if !ChooseExcavation(anchor, nil, distant) {
		t.Fatal("no shell means dig")
	}
}

func TestExcavationFrontier(t *testing.T) {
	targets, _ := ExcavationSites(mountainSite())
	target := targets[0]
	states := func(cleared, visible int) []ExcavationCellState {
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
	// Only the two visible corridor cells are known; both are adjacent (the
	// second through the first) and the rest is unknown.
	stage, remaining, unknown := ExcavationFrontier(target, states(0, 2), 8)
	if len(stage) != 2 || stage[0] != target.Corridor[0] || stage[1] != target.Door || remaining != 51 || !unknown {
		t.Fatal(stage, remaining, unknown)
	}
	// Corridor cleared, first interior column visible: the stage cap holds.
	stage, remaining, unknown = ExcavationFrontier(target, states(2, 30), 8)
	if len(stage) != 8 || remaining != 49 || !unknown {
		t.Fatal(stage, remaining, unknown)
	}
	for _, c := range stage {
		if c.X != 12 && c.X != 13 {
			t.Fatal("stage not frontier-adjacent", stage)
		}
	}
	// Everything visible: no unknown left; cells not adjacent to open space
	// wait for a later stage.
	stage, remaining, unknown = ExcavationFrontier(target, states(2, 51), 8)
	if len(stage) != 8 || remaining != 49 || unknown {
		t.Fatal(stage, remaining, unknown)
	}
	// Fully cleared.
	stage, remaining, unknown = ExcavationFrontier(target, states(51, 51), 8)
	if len(stage) != 0 || remaining != 0 || unknown {
		t.Fatal(stage, remaining, unknown)
	}
	// Visible but ineligible next cell with fogged remainder: nothing to
	// dig and still unknown, so the caller re-observes rather than fails.
	s := states(0, 1)
	s[0].Eligible = false
	stage, remaining, unknown = ExcavationFrontier(target, s, 8)
	if len(stage) != 0 || remaining != 51 || !unknown {
		t.Fatal(stage, remaining, unknown)
	}
	// Missing rows count as unknown.
	stage, remaining, unknown = ExcavationFrontier(target, nil, 8)
	if len(stage) != 0 || remaining != 51 || !unknown {
		t.Fatal(stage, remaining, unknown)
	}
}
