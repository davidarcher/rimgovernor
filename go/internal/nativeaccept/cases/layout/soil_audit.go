package layout

import (
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The layout/rich-soil audit (#1291) over a recorded layout plan, the map
// survey it was planned on and the colony's growing zones.

// richFertility is the fertility above which soil is rich (#1284).
const richFertility = 1.0

// richOverlapBudget is the fraction of the map's rich cells planned rooms
// and hallways may cover (#1291): the planner prices rich soil as a cost,
// not a ban, so a small overlap is allowed.
const richOverlapBudget = 0.02

// ringThick is the perimeter wall's thickness: a ring along the edge
// margin line occupies the band LayoutEdgeMargin..LayoutEdgeMargin+ringThick
// from the map edge.
const ringThick int32 = 3

// soilAudit is the three plan assertions' findings; each slice lists the
// violations found (capped), empty when the assertion holds.
type soilAudit struct {
	RichCells     int      `json:"rich_cells"`
	RichBuiltN    int      `json:"rich_built_cells"`
	RichOverlap   float64  `json:"rich_overlap_fraction"`
	Patches       int      `json:"patches"`
	Inside        int      `json:"patches_inside"`
	Outside       int      `json:"patches_outside"`
	MarginSplit   int      `json:"patches_split_on_margin"`
	WallCells     int      `json:"wall_cells"`
	CropZones     int      `json:"crop_zones"`
	RichBuilt     []string `json:"rich_built,omitempty"`
	PatchSplit    []string `json:"patch_split,omitempty"`
	WallOnFertile []string `json:"wall_on_fertile,omitempty"`
	CropGaps      []string `json:"crop_gaps,omitempty"`
}

func (a soilAudit) err() error {
	if a.RichOverlap > richOverlapBudget {
		return fmt.Errorf("planned rooms and hallways cover %s, over the %.0f%% budget: %v",
			a.richOverlap(), richOverlapBudget*100, a.RichBuilt)
	}
	for _, v := range []struct {
		what string
		list []string
	}{{"fertile patch split by the wall", a.PatchSplit},
		{"wall on fertile soil", a.WallOnFertile}, {"crop zones in a patch not adjacent", a.CropGaps}} {
		if len(v.list) > 0 {
			return fmt.Errorf("%s: %v", v.what, v.list)
		}
	}
	return nil
}

// richOverlap reports the rich cells rooms and hallways cover.
func (a soilAudit) richOverlap() string {
	return fmt.Sprintf("%d of %d rich cells (%.2f%%)", a.RichBuiltN, a.RichCells, a.RichOverlap*100)
}

const auditCap = 8

func note(list *[]string, format string, args ...any) {
	if len(*list) < auditCap {
		*list = append(*list, fmt.Sprintf(format, args...))
	}
}

func rectCells(r policy.Rectangle) []domain.Cell {
	var out []domain.Cell
	for z := r.Z; z < r.Z+r.Height; z++ {
		for x := r.X; x < r.X+r.Width; x++ {
			out = append(out, domain.Cell{X: x, Z: z})
		}
	}
	return out
}

var four = [4]domain.Cell{{X: 1}, {X: -1}, {Z: 1}, {Z: -1}}

func step(c, d domain.Cell) domain.Cell { return domain.Cell{X: c.X + d.X, Z: c.Z + d.Z} }

// connected reports whether set is one 4-connected component.
func connected(set map[domain.Cell]bool) bool {
	for start := range set {
		seen := map[domain.Cell]bool{start: true}
		q := []domain.Cell{start}
		for len(q) > 0 {
			c := q[0]
			q = q[1:]
			for _, d := range four {
				if n := step(c, d); set[n] && !seen[n] {
					seen[n] = true
					q = append(q, n)
				}
			}
		}
		return len(seen) == len(set)
	}
	return true
}

// builtCells is every cell a planned room (walls included) or hallway
// covers.
func builtCells(plan policy.LayoutPlan) map[domain.Cell]string {
	out := map[domain.Cell]string{}
	for _, r := range plan.AllRooms() {
		in := r.Interior
		for _, c := range rectCells(policy.Rectangle{X: in.X - 1, Z: in.Z - 1, Width: in.Width + 2, Height: in.Height + 2}) {
			out[c] = "room " + string(r.Role)
		}
	}
	half := policy.SpineWidth / 2
	for _, s := range plan.Hallways() {
		lo := domain.Cell{X: min(s.From.X, s.To.X) - half, Z: min(s.From.Z, s.To.Z) - half}
		hi := domain.Cell{X: max(s.From.X, s.To.X) + half, Z: max(s.From.Z, s.To.Z) + half}
		for _, c := range rectCells(policy.Rectangle{X: lo.X, Z: lo.Z, Width: hi.X - lo.X + 1, Height: hi.Z - lo.Z + 1}) {
			out[c] = "hallway"
		}
	}
	return out
}

// auditSoil checks the plan and crop zones against the survey:
//  1. planned rooms and hallways cover at most richOverlapBudget of the
//     rich cells;
//  2. each rich patch of a field zone is wholly inside or wholly outside
//     the traced ring, and no wall cell sits on it, except where the ring
//     runs along the edge margin line (the band LayoutEdgeMargin..
//     LayoutEdgeMargin+ringThick from the edge), where it may cross and
//     split the patch;
//  3. the growing zones inside each patch form one 4-connected block.
func auditSoil(plan policy.LayoutPlan, s policy.MapSurvey, crops [][]domain.Cell) soilAudit {
	var a soilAudit
	w, h := s.Bounds.Width, s.Bounds.Height
	rich, open := map[domain.Cell]bool{}, map[domain.Cell]bool{}
	for _, c := range s.Cells {
		if c.Fertility > richFertility {
			rich[c.Cell] = true
		}
		open[c.Cell] = c.Walkable && !c.Rock
	}
	a.RichCells = len(rich)
	built := builtCells(plan)
	for _, c := range sortedCells(built) {
		if rich[c] {
			a.RichBuiltN++
			note(&a.RichBuilt, "%s at %v", built[c], c)
		}
	}
	if a.RichCells > 0 {
		a.RichOverlap = float64(a.RichBuiltN) / float64(a.RichCells)
	}

	band := func(c domain.Cell) bool {
		return min(c.X, c.Z, w-1-c.X, h-1-c.Z) < policy.LayoutEdgeMargin+ringThick
	}
	wall := map[domain.Cell]bool{}
	barrier := map[domain.Cell]bool{}
	for _, r := range plan.Reservations {
		switch r.Kind {
		case policy.ReservePerimeter, policy.ReservePerimeterLight, policy.ReserveBridge, policy.ReservePerimeterGap, policy.ReserveGate:
			for _, c := range rectCells(r.Area) {
				wall[c], barrier[c] = true, true
			}
		case policy.ReserveKillbox:
			for _, c := range rectCells(r.Area) {
				barrier[c] = true
			}
		}
	}
	a.WallCells = len(wall)
	// Inside is what the room interiors reach without crossing the ring,
	// rock or impassable ground.
	inside := map[domain.Cell]bool{}
	var q []domain.Cell
	seed := func(c domain.Cell) {
		if c.X >= 0 && c.Z >= 0 && c.X < w && c.Z < h && open[c] && !barrier[c] && !inside[c] {
			inside[c] = true
			q = append(q, c)
		}
	}
	if len(wall) > 0 {
		for _, r := range plan.AllRooms() {
			for _, c := range rectCells(r.Interior) {
				seed(c)
			}
		}
	}
	for len(q) > 0 {
		c := q[0]
		q = q[1:]
		for _, d := range four {
			seed(step(c, d))
		}
	}

	// A turbine lane is zoned as a field of its own under the blades; it
	// is a utility, not a fertile patch, and is left out.
	lane := map[domain.Cell]bool{}
	for _, r := range plan.Reservations {
		if r.Kind == policy.ReserveTurbineLane {
			for _, c := range rectCells(r.Area) {
				lane[c] = true
			}
		}
	}
	var patches []map[domain.Cell]bool
	owner := map[domain.Cell]int{}
	for _, z := range plan.Zones {
		if z.Kind != policy.ZoneField || len(z.Runs) == 0 || lane[domain.Cell{X: z.Runs[0].X, Z: z.Runs[0].Z}] {
			continue
		}
		// The wall takes in rich soil only: each 4-connected rich part of
		// the zone is one patch.
		left := map[domain.Cell]bool{}
		for _, r := range z.Runs {
			for x := r.X; x < r.X+r.Length; x++ {
				if c := (domain.Cell{X: x, Z: r.Z}); rich[c] {
					left[c] = true
				}
			}
		}
		for _, start := range sortedCells(left) {
			if !left[start] {
				continue
			}
			p := map[domain.Cell]bool{start: true}
			delete(left, start)
			for q := []domain.Cell{start}; len(q) > 0; q = q[1:] {
				for _, d := range four {
					if n := step(q[0], d); left[n] {
						delete(left, n)
						p[n] = true
						q = append(q, n)
					}
				}
			}
			for c := range p {
				owner[c] = len(patches) + 1
			}
			patches = append(patches, p)
		}
	}
	a.Patches = len(patches)
	for i, p := range patches {
		first := sortedCells(p)[0]
		if !connected(p) {
			note(&a.PatchSplit, "field zone %d at %v is not one patch", i, first)
		}
		in, out, onMargin, offMargin := 0, 0, 0, 0
		for c := range p {
			if wall[c] {
				if band(c) {
					onMargin++
				} else {
					offMargin++
				}
				continue
			}
			for _, d := range four {
				if n := step(c, d); owner[n] != 0 && owner[n] != i+1 {
					note(&a.PatchSplit, "field zones %d and %d touch at %v", i, owner[n]-1, c)
				}
				if n := step(c, d); wall[n] && !p[n] {
					if band(n) {
						onMargin++
					} else {
						offMargin++
					}
				}
			}
			if inside[c] {
				in++
			} else {
				out++
			}
		}
		for _, c := range sortedCells(p) {
			if wall[c] && !band(c) {
				note(&a.WallOnFertile, "field zone %d at %v", i, c)
			}
		}
		switch {
		case in > 0 && out > 0 && offMargin == 0 && onMargin > 0:
			a.MarginSplit++
		case in > 0 && out > 0:
			note(&a.PatchSplit, "field zone %d at %v: %d cells inside, %d outside, %d wall cells off the margin line", i, first, in, out, offMargin)
		case in > 0:
			a.Inside++
		default:
			a.Outside++
		}
	}

	a.CropZones = len(crops)
	perPatch := map[int]map[domain.Cell]bool{}
	for _, zone := range crops {
		for _, c := range zone {
			if i := owner[c]; i != 0 {
				if perPatch[i] == nil {
					perPatch[i] = map[domain.Cell]bool{}
				}
				perPatch[i][c] = true
			}
		}
	}
	for i, set := range perPatch {
		if !connected(set) {
			note(&a.CropGaps, "field zone %d: %d crop cells in more than one block", i-1, len(set))
		}
	}
	return a
}

func sortedCells[V any](m map[domain.Cell]V) []domain.Cell {
	out := make([]domain.Cell, 0, len(m))
	for c := range m {
		out = append(out, c)
	}
	slices.SortFunc(out, func(a, b domain.Cell) int {
		if a.Z != b.Z {
			return int(a.Z - b.Z)
		}
		return int(a.X - b.X)
	})
	return out
}
