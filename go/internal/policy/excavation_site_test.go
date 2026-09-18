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
	targets, _ := ExcavationSites(mountainSite())
	target := targets[0]
	states := func(cleared, visible int) []ExcavationCellState { return excavationStates(target, cleared, visible) }
	// Only the two visible corridor cells are known; both are adjacent (the
	// second through the first) and the rest is unknown.
	r := ReviewExcavation(target, states(0, 2), 8)
	if len(r.Stage) != 2 || r.Stage[0] != target.Corridor[0] || r.Stage[1] != target.Door || r.Remaining != 49 || !r.Unknown || !r.Corridor || r.Complete {
		t.Fatal(r)
	}
	// Corridor cleared, first interior column visible: the stage cap holds.
	r = ReviewExcavation(target, states(2, 30), 8)
	if len(r.Stage) != 8 || r.Remaining != 41 || !r.Unknown || r.Complete {
		t.Fatal(r)
	}
	for _, c := range r.Stage {
		if c.X != 12 && c.X != 13 {
			t.Fatal("stage not frontier-adjacent", r.Stage)
		}
	}
	// Everything visible: no unknown left; cells not adjacent to open space
	// wait for a later stage.
	r = ReviewExcavation(target, states(2, 51), 8)
	if len(r.Stage) != 8 || r.Remaining != 41 || r.Unknown || r.Complete {
		t.Fatal(r)
	}
	// Fully cleared: complete, the door is owed.
	r = ReviewExcavation(target, states(51, 51), 8)
	if len(r.Stage) != 0 || r.Remaining != 0 || r.Unknown || !r.Complete || !r.Corridor {
		t.Fatal(r)
	}
	// Missing rows count as unknown.
	r = ReviewExcavation(target, nil, 8)
	if len(r.Stage) != 0 || r.Remaining != 51 || !r.Unknown || r.Complete {
		t.Fatal(r)
	}
}

func TestExcavationReviewKeepsRevealedHazards(t *testing.T) {
	targets, _ := ExcavationSites(mountainSite())
	target := targets[0]
	cells := target.Cells()
	// Everything visible and dug except two interior cells that unfogged
	// into a structure (an ancient wall) and one visible rock cell native
	// refuses: they are kept as part of the walls and the room completes
	// around them, corridor open, door owed.
	s := excavationStates(target, 51, 51)
	s[20].Cleared, s[20].Blocked = false, true
	s[21].Cleared, s[21].Blocked = false, true
	s[40].Cleared, s[40].Eligible = false, false
	r := ReviewExcavation(target, s, 8)
	if len(r.Stage) != 0 || len(r.Kept) != 3 || r.Kept[0] != cells[20] || r.Kept[2] != cells[40] || r.Remaining != 0 || r.Unknown || !r.Corridor || !r.Complete {
		t.Fatal(r)
	}
	// A hazard next to a diggable cell never enters the stage; digging
	// continues around it.
	s = excavationStates(target, 3, 51)
	s[3].Eligible, s[3].Blocked = false, true
	r = ReviewExcavation(target, s, 8)
	if len(r.Stage) != 8 || len(r.Kept) != 1 || r.Kept[0] != cells[3] || r.Complete || !r.Corridor {
		t.Fatal(r)
	}
	for _, c := range r.Stage {
		if c == cells[3] {
			t.Fatal("hazard staged", r.Stage)
		}
	}
	// An interior cell sealed off behind kept cells is neither staged nor
	// unknown: the room is complete without it.
	s = excavationStates(target, 51, 51)
	// The far corner and both its interior neighbours.
	corner := cells[len(cells)-1]
	for i, c := range cells {
		if c == corner {
			s[i].Cleared = false
		} else if (c.X == corner.X && (c.Z == corner.Z-1 || c.Z == corner.Z+1)) || (c.Z == corner.Z && (c.X == corner.X-1 || c.X == corner.X+1)) {
			s[i].Cleared, s[i].Blocked = false, true
		}
	}
	s[len(cells)-1].Eligible = true
	r = ReviewExcavation(target, s, 8)
	if len(r.Stage) != 0 || r.Remaining != 1 || r.Unknown || !r.Complete {
		t.Fatal(r)
	}
	// Visible but ineligible next cell with fogged remainder: nothing to
	// dig and still unknown, so the caller re-observes rather than fails.
	s = excavationStates(target, 0, 1)
	s[0].Eligible = false
	r = ReviewExcavation(target, s, 8)
	if len(r.Stage) != 0 || r.Remaining != 50 || !r.Unknown || r.Corridor || r.Complete {
		t.Fatal(r)
	}
}

func TestExcavationReviewBlockedCorridorNeedsResiting(t *testing.T) {
	targets, _ := ExcavationSites(mountainSite())
	target := targets[0]
	// The door cell unfogged into a structure: the interior is visible and
	// diggable but can never be entered through this corridor.
	s := excavationStates(target, 1, 51)
	s[1].Eligible, s[1].Blocked = false, true
	r := ReviewExcavation(target, s, 8)
	if r.Corridor || r.Complete || len(r.Kept) != 1 || r.Kept[0] != target.Door {
		t.Fatal(r)
	}
	// The same with the whole interior dug from elsewhere is still not a
	// finished project: the door cannot close a room it cannot reach.
	s = excavationStates(target, 51, 51)
	s[0].Cleared, s[0].Blocked = false, true
	r = ReviewExcavation(target, s, 8)
	if r.Corridor || r.Complete {
		t.Fatal(r)
	}
	// The entrance past the door is part of the way in.
	s = excavationStates(target, 2, 51)
	s[2].Eligible, s[2].Blocked = false, true
	r = ReviewExcavation(target, s, 8)
	if r.Corridor || r.Complete || len(r.Stage) != 0 {
		t.Fatal(r)
	}
}

func TestExcavationSitesReadoptsPartlyDugTarget(t *testing.T) {
	// A corridor and the first interior column already dug (open under
	// rock roof, no room yet) are re-planned as the same target, ranked
	// first, so an invalidated goal resumes the half-dug room.
	r := mountainSite()
	dug := map[domain.Cell]bool{{X: 10, Z: 15}: true, {X: 11, Z: 15}: true, {X: 12, Z: 14}: true, {X: 12, Z: 15}: true, {X: 12, Z: 16}: true}
	for i := range r.Cells {
		if dug[r.Cells[i].Cell] {
			r.Cells[i] = SiteCell{Cell: r.Cells[i].Cell, Walkable: domain.Known(true), Occupied: domain.Known(false), Roofed: domain.Known(true), Roof: domain.Known("RoofRockThick"), Indoors: domain.Known(false)}
		}
	}
	// The anchor drifts with the pawns; sunk work still wins.
	r.Anchor = domain.Cell{X: 5, Z: 20}
	targets, err := ExcavationSites(r)
	if err != nil {
		t.Fatal(err)
	}
	if len(targets) == 0 || targets[0].Key() != "9.15.1.0.2.12.12.7.7" {
		t.Fatal(targets)
	}
	// Open ground that already forms a proper room is somebody's building,
	// never a target cell.
	for i := range r.Cells {
		if dug[r.Cells[i].Cell] {
			r.Cells[i].Indoors = domain.Known(true)
		}
	}
	targets, _ = ExcavationSites(r)
	for _, target := range targets {
		if target.Key() == "9.15.1.0.2.12.12.7.7" {
			t.Fatal("re-planned through a proper room")
		}
	}
	// A target with nothing left to dig is not an excavation.
	r = mountainSite()
	for i := range r.Cells {
		if r.Cells[i].Cell.X >= 10 {
			r.Cells[i] = SiteCell{Cell: r.Cells[i].Cell, Walkable: domain.Known(true), Occupied: domain.Known(false), Roofed: domain.Known(true), Roof: domain.Known("RoofRockThick"), Indoors: domain.Known(false)}
		}
	}
	r.Region = Rectangle{X: 0, Z: 0, Width: 12, Height: 30}
	targets, _ = ExcavationSites(r)
	if len(targets) != 0 {
		t.Fatal("open pocket planned as excavation", targets[0])
	}
}

func TestExcavationSitesRoundInterior(t *testing.T) {
	// Shapes are tried in preference order at every face: a neolithic
	// colony asks for the circle first and gets a 49-cell round room whose
	// bounding box sits where the 9×9 rectangle would.
	r := mountainSite()
	r.Interior = Bounds{}
	r.Shapes = []ExcavationShape{EllipseShape(4, 4, domain.EllipseNorthSouth), RectangleShape(7, 7)}
	targets, err := ExcavationSites(r)
	if err != nil {
		t.Fatal(err)
	}
	if len(targets) == 0 {
		t.Fatal("no targets")
	}
	best := targets[0]
	if best.Shape.Kind != ExcavationEllipse || best.Access != (domain.Cell{X: 9, Z: 15}) || best.Door != (domain.Cell{X: 11, Z: 15}) || best.Interior != (Rectangle{X: 12, Z: 11, Width: 9, Height: 9}) {
		t.Fatal(best)
	}
	room := best.InteriorCells()
	cells := best.Cells()
	if len(room) != 49 || len(cells) != 51 || cells[2] != (domain.Cell{X: 12, Z: 15}) || best.Center() != (domain.Cell{X: 16, Z: 15}) {
		t.Fatal(len(room), cells[:3], best.Center())
	}
	inside := map[domain.Cell]bool{}
	for _, c := range room {
		inside[c] = true
	}
	// Round: the box corners are rock, the axis extremes are interior.
	if inside[domain.Cell{X: 12, Z: 11}] || inside[domain.Cell{X: 20, Z: 19}] || !inside[domain.Cell{X: 20, Z: 15}] || !inside[domain.Cell{X: 16, Z: 11}] || !inside[best.Center()] {
		t.Fatal(room)
	}
	if !excavationSupported(cells) {
		t.Fatal("round room outside support")
	}
	if best.Key() != "9.15.1.0.2.12.11.9.9.e.4.4.north_south" {
		t.Fatal(best.Key())
	}
	parsed, err := ParseExcavationKey(best.Key())
	if err != nil || parsed.Key() != best.Key() || parsed.Shape != best.Shape || len(parsed.Cells()) != 51 || parsed.Cells()[50] != cells[50] {
		t.Fatal(parsed, err)
	}
	for _, bad := range []string{"9.15.1.0.2.12.11.9.9.e.4.4.sideways", "9.15.1.0.2.12.11.9.9.x.4.4.north_south", "9.15.1.0.2.12.11.9.9.e.6.4.north_south", "9.15.1.0.2.12.11.9.9.e.0.4.north_south", "9.15.1.0.2.12.12.7.7.e.4.4.north_south", "9.15.1.0.2.12.11.9.9.e.4"} {
		if _, err := ParseExcavationKey(bad); err == nil {
			t.Fatal("accepted", bad)
		}
	}
	// A pocket on the circle's rim but outside the 7×7 rectangle rejects
	// the preferred shape at that face and the rectangle takes its place
	// at the same corridor.
	r.Cells = append(r.Cells, SiteCell{Cell: domain.Cell{X: 20, Z: 15}, Walkable: domain.Known(true), Occupied: domain.Known(false)})
	targets, err = ExcavationSites(r)
	if err != nil {
		t.Fatal(err)
	}
	for _, target := range targets {
		if target.Access == (domain.Cell{X: 9, Z: 15}) && len(target.Corridor) == 2 {
			if target.Shape.Kind != ExcavationRectangle || target.Interior != (Rectangle{X: 12, Z: 12, Width: 7, Height: 7}) || target.Key() != "9.15.1.0.2.12.12.7.7" {
				t.Fatal(target)
			}
			return
		}
	}
	t.Fatal("no fallback rectangle at the blocked face", targets)
}

func TestExcavationShapeValidation(t *testing.T) {
	r := mountainSite()
	r.Shapes = []ExcavationShape{EllipseShape(5, 5, domain.EllipseNorthSouth)}
	if _, err := ExcavationSites(r); err == nil {
		t.Fatal("81-cell circle accepted")
	}
	r.Shapes = []ExcavationShape{EllipseShape(6, 3, domain.EllipseEastWest)}
	if _, err := ExcavationSites(r); err == nil {
		t.Fatal("oversized radius accepted")
	}
	r.Shapes = []ExcavationShape{{Kind: "blob"}}
	if _, err := ExcavationSites(r); err == nil {
		t.Fatal("unknown shape accepted")
	}
	r.Shapes = []ExcavationShape{EllipseShape(5, 3, domain.EllipseEastWest), RectangleShape(3, 11)}
	r.MaxCorridor = 9
	if _, err := ExcavationSites(r); err != nil {
		t.Fatal(err)
	}
	// Every cell of a 13-wide block is not within six of untouched rock.
	if excavationSupported(rectCells(Rectangle{X: 5, Z: 5, Width: 13, Height: 13})) {
		t.Fatal("13×13 supported")
	}
	if !excavationSupported(rectCells(Rectangle{X: 5, Z: 5, Width: 11, Height: 11})) {
		t.Fatal("11×11 unsupported")
	}
	// A diagonal oval whose axis extreme misses the door cell is skipped
	// rather than dug with a corridor that opens into rock.
	if _, _, opens := EllipseShape(5, 2, domain.EllipseNorthEast).place(domain.Cell{X: 11, Z: 15}, domain.Cell{X: 1, Z: 0}); opens {
		t.Fatal("diagonal oval reported open at its box midpoint")
	}
}
