package policy

import (
	"fmt"
	"sort"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
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
		if ca, cb := doorsOf(a, ra[role]), doorsOf(b, rb[role]); fmt.Sprint(ca) != fmt.Sprint(cb) {
			d.Connectivity = append(d.Connectivity, fmt.Sprintf("%s door counts %v -> %v", role, ca, cb))
		}
	}
	aa, ab := adjacencyOf(a), adjacencyOf(b)
	for _, k := range unionKeys(aa, ab) {
		if aa[k] != ab[k] {
			d.Adjacency = append(d.Adjacency, fmt.Sprintf("%s shares a wall: %v -> %v", k, aa[k], ab[k]))
		}
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
	for _, r := range p.Rooms {
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

func doorsOf(p LayoutPlan, rooms []PlannedRoom) []int {
	out := make([]int, 0, len(rooms))
	for _, r := range rooms {
		out = append(out, 1+len(p.LinkCells(r.ID)))
	}
	sort.Ints(out)
	return out
}

// adjacencyOf is the set of role pairs ("a|b", a <= b) with a room of each
// whose wall rings meet.
func adjacencyOf(p LayoutPlan) map[string]bool {
	rooms := p.Rooms
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

func TestStructuralDiff(t *testing.T) {
	a := LayoutPlan{Rooms: []PlannedRoom{
		{Role: PlannedBedroom, Region: RectRegion(Rectangle{X: 0, Z: 0, Width: 4, Height: 4})},
		{Role: PlannedStorage, Region: RectRegion(Rectangle{X: 5, Z: 0, Width: 4, Height: 4})},
	}}
	if d := StructuralDiff(a, a); !d.Empty() {
		t.Fatal("a plan differs from itself:", d)
	}
	a = a.WithRoomIDs()
	b := LayoutPlan{Rooms: []PlannedRoom{
		{Role: PlannedBedroom, Region: RectRegion(Rectangle{X: 0, Z: 0, Width: 5, Height: 4})},
		{Role: PlannedStorage, Region: RectRegion(Rectangle{X: 20, Z: 0, Width: 4, Height: 4})},
		{Role: PlannedNursery, Region: RectRegion(Rectangle{X: 30, Z: 0, Width: 3, Height: 3})},
	}, Links: []RoomLink{{A: 1, B: 3, Cell: domain.Cell{X: 5, Z: 1}}}}.WithRoomIDs()
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
