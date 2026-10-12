package policy

import (
	"fmt"
	"sort"
	"strings"
	"testing"
)

// StructDiff is what StructuralDiff reports: the room set by role and size,
// which rooms share a wall, each role's region shape, and door and route
// connectivity. Every line names one difference; an empty diff means the
// plans are structurally the same.
type StructDiff struct {
	Rooms, Adjacency, Shapes, Connectivity []string
}

func (d StructDiff) Empty() bool {
	return len(d.Rooms)+len(d.Adjacency)+len(d.Shapes)+len(d.Connectivity) == 0
}

func (d StructDiff) String() string {
	if d.Empty() {
		return "structurally identical"
	}
	var out []string
	for _, g := range []struct {
		name  string
		lines []string
	}{{"rooms", d.Rooms}, {"adjacency", d.Adjacency}, {"shapes", d.Shapes}, {"connectivity", d.Connectivity}} {
		for _, l := range g.lines {
			out = append(out, g.name+": "+l)
		}
	}
	return strings.Join(out, "\n")
}

// StructuralDiff compares two plans by shape rather than score. RunParityGate
// logs it beside the per-term score deltas so a planner change reads as a
// layout change, not only a number.
func StructuralDiff(a, b LayoutPlan) StructDiff {
	var d StructDiff
	ra, rb := roomsByRole(a), roomsByRole(b)
	for _, role := range unionKeys(ra, rb) {
		if sa, sb := areasOf(ra[role]), areasOf(rb[role]); fmt.Sprint(sa) != fmt.Sprint(sb) {
			d.Rooms = append(d.Rooms, fmt.Sprintf("%s areas %v -> %v", role, sa, sb))
		}
		if fa, fb := shapesOf(ra[role]), shapesOf(rb[role]); fmt.Sprint(fa) != fmt.Sprint(fb) {
			d.Shapes = append(d.Shapes, fmt.Sprintf("%s %v -> %v", role, fa, fb))
		}
		if ca, cb := doorsOf(ra[role]), doorsOf(rb[role]); fmt.Sprint(ca) != fmt.Sprint(cb) {
			d.Connectivity = append(d.Connectivity, fmt.Sprintf("%s door counts %v -> %v", role, ca, cb))
		}
	}
	aa, ab := adjacencyOf(a), adjacencyOf(b)
	for _, k := range unionKeys(aa, ab) {
		if aa[k] != ab[k] {
			d.Adjacency = append(d.Adjacency, fmt.Sprintf("%s shares a wall: %v -> %v", k, aa[k], ab[k]))
		}
	}
	if ha, hb := len(a.Hallways()), len(b.Hallways()); ha != hb {
		d.Connectivity = append(d.Connectivity, fmt.Sprintf("hallways %d -> %d", ha, hb))
	}
	if ea, eb := len(a.Entrances), len(b.Entrances); ea != eb {
		d.Connectivity = append(d.Connectivity, fmt.Sprintf("entrances %d -> %d", ea, eb))
	}
	if ea, eb := routesVerdict(a), routesVerdict(b); ea != eb {
		d.Connectivity = append(d.Connectivity, fmt.Sprintf("routes %q -> %q", ea, eb))
	}
	return d
}

func routesVerdict(p LayoutPlan) string {
	if _, err := CheckRoutes(p); err != nil {
		return err.Error()
	}
	return ""
}

func roomsByRole(p LayoutPlan) map[PlannedRole][]PlannedRoom {
	out := map[PlannedRole][]PlannedRoom{}
	for _, r := range p.AllRooms() {
		out[r.Role] = append(out[r.Role], r)
	}
	return out
}

func areasOf(rooms []PlannedRoom) []int {
	out := make([]int, 0, len(rooms))
	for _, r := range rooms {
		out = append(out, r.Region.Area())
	}
	sort.Ints(out)
	return out
}

// shapesOf names each room's bounding size and whether it is a plain
// rectangle, so a rectangle swapped for an L-shape shows even at equal area.
func shapesOf(rooms []PlannedRoom) []string {
	out := make([]string, 0, len(rooms))
	for _, r := range rooms {
		b := r.Region.Bounds()
		out = append(out, fmt.Sprintf("%dx%d rect=%v", b.Width, b.Height, r.Region.IsRect()))
	}
	sort.Strings(out)
	return out
}

func doorsOf(rooms []PlannedRoom) []int {
	out := make([]int, 0, len(rooms))
	for _, r := range rooms {
		n := 1 + len(r.Doors)
		if r.Link != nil {
			n++
		}
		out = append(out, n)
	}
	sort.Ints(out)
	return out
}

// adjacencyOf is the set of role pairs ("a|b", a <= b) with a room of each
// whose wall rings meet.
func adjacencyOf(p LayoutPlan) map[string]bool {
	rooms := p.AllRooms()
	walls := make([]Region, len(rooms))
	for i, r := range rooms {
		walls[i] = r.Region.Walls()
	}
	out := map[string]bool{}
	for i := range rooms {
		for j := i + 1; j < len(rooms); j++ {
			if walls[i].Overlaps(walls[j]) {
				x, y := string(rooms[i].Role), string(rooms[j].Role)
				if x > y {
					x, y = y, x
				}
				out[x+"|"+y] = true
			}
		}
	}
	return out
}

func unionKeys[K ~string, V any](a, b map[K]V) []K {
	seen := map[K]bool{}
	var out []K
	for _, m := range []map[K]V{a, b} {
		for k := range m {
			if !seen[k] {
				seen[k] = true
				out = append(out, k)
			}
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i] < out[j] })
	return out
}

// ReplayDeltas is the per-term change cur makes to old (cur - old), for
// every term that moved.
func ReplayDeltas(old, cur replayRow) map[string]int {
	out := map[string]int{}
	for name, v := range map[string][2]int{
		"Total": {old.Total, cur.Total}, "RichCells": {old.RichCells, cur.RichCells},
		"Soil": {old.Soil, cur.Soil}, "Footprint": {old.Footprint, cur.Footprint},
		"Wall": {old.Wall, cur.Wall}, "Edge": {old.Edge, cur.Edge}, "Centre": {old.Centre, cur.Centre},
		"Defense": {old.Defense, cur.Defense}, "Expansion": {old.Expansion, cur.Expansion},
		"Rooms": {old.Rooms, cur.Rooms}, "Missing": {old.Missing, cur.Missing},
	} {
		if v[0] != v[1] {
			out[name] = v[1] - v[0]
		}
	}
	return out
}

// ParityVerdict is the gate a replacement planner must clear on one fixture:
// its hard tier passes whenever the old generator's did, and its total is at
// least the old one's. Nil when it clears.
func ParityVerdict(old, cur replayRow) error {
	if old.Pass && !cur.Pass {
		return fmt.Errorf("hard tier fails (missing=%d routes=%q rich=%d) where the old generator passed", cur.Missing, cur.RoutesErr, cur.RichCells)
	}
	if cur.Total < old.Total {
		return fmt.Errorf("total %d is below the old generator's %d", cur.Total, old.Total)
	}
	return nil
}

// RunParityGate plans every replayFixtures survey with plan and checks it
// against the recorded old-generator rows: one subtest and one verdict per
// fixture, with the score deltas and the structural diff against the old
// generator's plan logged. A planner that cannot plan a survey returns an
// error, which counts as not clearing the gate.
//
// The gate is a measurement until the cutover (#2931): expectedFail names, by
// fixture, the reason it does not yet clear ParityVerdict. A listed fixture
// that fails is logged; a listed fixture that clears, or an unlisted one that
// fails, is an error, so a regression and an unexpected pass both show.
func RunParityGate(t *testing.T, plan func(f replayFixture, s MapSurvey) (LayoutPlan, error), expectedFail map[string]string) {
	t.Helper()
	recorded := loadReplayScores(t)
	for _, f := range replayFixtures {
		t.Run(f.name, func(t *testing.T) {
			old, ok := recorded[f.name]
			if !ok {
				t.Fatal("no recorded row for the fixture")
			}
			s := f.survey(t)
			p, err := plan(f, s)
			if err == nil {
				row := newReplayRow(Score(p, s), len(p.AllRooms()))
				ref := SiteCore(LayoutPlan{Zones: Zone(s)}, s, f.pawns, 1, TechTierCamp)
				t.Logf("%s deltas vs old: %v\n%s", f.name, ReplayDeltas(old, row), StructuralDiff(ref, p))
				err = ParityVerdict(old, row)
			}
			reason, expected := expectedFail[f.name]
			switch {
			case err != nil && expected:
				t.Logf("%s does not clear the gate (expected: %s): %v", f.name, reason, err)
			case err != nil:
				t.Errorf("%s regressed from clearing the gate: %v", f.name, err)
			case expected:
				t.Errorf("%s clears the gate but is listed as failing (%s): remove it from the list", f.name, reason)
			}
		})
	}
}
func TestStructuralDiff(t *testing.T) {
	a := LayoutPlan{Rooms: []PlannedRoom{
		{Role: PlannedBedroom, Region: RectRegion(Rectangle{X: 0, Z: 0, Width: 4, Height: 4})},
		{Role: PlannedStorage, Region: RectRegion(Rectangle{X: 5, Z: 0, Width: 4, Height: 4})},
	}}
	if d := StructuralDiff(a, a); !d.Empty() {
		t.Fatal("a plan differs from itself:", d)
	}
	b := LayoutPlan{Rooms: []PlannedRoom{
		{Role: PlannedBedroom, Region: RectRegion(Rectangle{X: 0, Z: 0, Width: 5, Height: 4}), Doors: []Door{{}}},
		{Role: PlannedStorage, Region: RectRegion(Rectangle{X: 20, Z: 0, Width: 4, Height: 4})},
		{Role: PlannedNursery, Region: RectRegion(Rectangle{X: 30, Z: 0, Width: 3, Height: 3})},
	}}
	d := StructuralDiff(a, b)
	// Bedroom and nursery change area and shape; storage no longer touches the bedroom;
	// the bedroom gains a door and the nursery appears.
	if len(d.Rooms) != 2 || len(d.Shapes) != 2 || len(d.Adjacency) != 1 || len(d.Connectivity) != 2 {
		t.Fatalf("unexpected diff:\n%s", d)
	}
	if !strings.Contains(d.Adjacency[0], "bedroom|storage") {
		t.Fatal("adjacency line:", d.Adjacency[0])
	}
}

func TestReplayDeltasAndParity(t *testing.T) {
	old := replayRow{Pass: true, Total: -100, Soil: -10}
	if err := ParityVerdict(old, replayRow{Pass: true, Total: -100, Soil: -5}); err != nil {
		t.Fatal(err)
	}
	if ParityVerdict(old, replayRow{Pass: true, Total: -101}) == nil {
		t.Fatal("a lower total passed")
	}
	if ParityVerdict(old, replayRow{Pass: false, Total: 0}) == nil {
		t.Fatal("a hard-tier loss passed")
	}
	if ParityVerdict(replayRow{Total: -9}, replayRow{Total: -9}) != nil {
		t.Fatal("an equal failing fixture failed")
	}
	if d := ReplayDeltas(old, replayRow{Pass: true, Total: -90}); len(d) != 2 || d["Total"] != 10 || d["Soil"] != 10 {
		t.Fatal(d)
	}
}
