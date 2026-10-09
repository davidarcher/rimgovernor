package layout

import (
	"fmt"
	"maps"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The layout/rich-soil audit over a recorded layout plan, the map
// survey it was planned on and the colony's growing zones.

// richFertility is the fertility above which soil is rich.
const richFertility = 1.0

// richOverlapBudget is the fraction of the map's rich cells planned rooms
// and hallways may cover: the planner prices rich soil as a cost,
// not a ban, so a small overlap is allowed.
const richOverlapBudget = 0.02

// soilAudit is the three plan assertions' findings; each slice lists the
// violations found (capped), empty when the assertion holds.
type soilAudit struct {
	RichCells   int      `json:"rich_cells"`
	RichBuiltN  int      `json:"rich_built_cells"`
	RichOverlap float64  `json:"rich_overlap_fraction"`
	Patches     int      `json:"patches"`
	WallCells   int      `json:"wall_cells"`
	CropZones   int      `json:"crop_zones"`
	RichBuilt   []string `json:"rich_built,omitempty"`
	PatchSplit  []string `json:"patch_split,omitempty"`
	CropGaps    []string `json:"crop_gaps,omitempty"`
}

func (a soilAudit) err() error {
	if a.RichOverlap > richOverlapBudget {
		return fmt.Errorf("planned rooms and hallways cover %s, over the %.0f%% budget: %v",
			a.richOverlap(), richOverlapBudget*100, a.RichBuilt)
	}
	for _, v := range []struct {
		what string
		list []string
	}{{"fertile patch not one block", a.PatchSplit},
		{"crop zones in a patch not adjacent", a.CropGaps}} {
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
//  2. each rich patch of a field zone is one block and touches no other,
//     and a rich patch of any footprint (an L, a plus, a courtyard that is
//     no rectangle) is farmed as one 4-connected block;
//  3. the growing zones inside each field zone form one 4-connected
//     block.
func auditSoil(plan policy.LayoutPlan, s policy.MapSurvey, crops [][]domain.Cell) soilAudit {
	var a soilAudit
	rich := map[domain.Cell]bool{}
	for _, c := range s.Cells {
		if c.Fertility > richFertility {
			rich[c.Cell] = true
		}
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

	wall := map[domain.Cell]bool{}
	for _, r := range plan.Reservations {
		switch r.Kind {
		case policy.ReservePerimeter, policy.ReservePerimeterLight, policy.ReserveBridge, policy.ReservePerimeterGap, policy.ReserveGate:
			for _, c := range rectCells(r.Area) {
				wall[c] = true
			}
		}
	}
	a.WallCells = len(wall)

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
	owner, zoneOf := map[domain.Cell]int{}, map[domain.Cell]int{}
	for zi, z := range plan.Zones {
		if z.Kind != policy.ZoneField || len(z.Runs) == 0 || lane[domain.Cell{X: z.Runs[0].X, Z: z.Runs[0].Z}] {
			continue
		}
		// The wall takes in rich soil only: each 4-connected rich part of
		// the zone is one patch.
		left := map[domain.Cell]bool{}
		for _, r := range z.Runs {
			for x := r.X; x < r.X+r.Length; x++ {
				c := domain.Cell{X: x, Z: r.Z}
				zoneOf[c] = zi + 1
				if rich[c] {
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
		for c := range p {
			for _, d := range four {
				if n := step(c, d); owner[n] != 0 && owner[n] != i+1 {
					note(&a.PatchSplit, "field zones %d and %d touch at %v", i, owner[n]-1, c)
				}
			}
		}
	}

	// A rich patch of any shape (the survey's 4-connected rich cells, an L,
	// a plus or a ring as much as a rectangle) is farmed as one block: the
	// field cells over it form one 4-connected set, so a room or hallway
	// cut through it splits it into two and is caught here even inside the
	// overlap budget.
	left := maps.Clone(rich)
	for _, start := range sortedCells(left) {
		if !left[start] {
			continue
		}
		patch := map[domain.Cell]bool{start: true}
		delete(left, start)
		for q := []domain.Cell{start}; len(q) > 0; q = q[1:] {
			for _, d := range four {
				if n := step(q[0], d); left[n] {
					delete(left, n)
					patch[n] = true
					q = append(q, n)
				}
			}
		}
		farmed := map[domain.Cell]bool{}
		for c := range patch {
			if owner[c] != 0 {
				farmed[c] = true
			}
		}
		if !connected(farmed) {
			note(&a.PatchSplit, "rich patch at %v is farmed in more than one block", sortedCells(patch)[0])
		}
	}

	a.CropZones = len(crops)
	// Crops are laid per field zone, plain soil included.
	perPatch := map[int]map[domain.Cell]bool{}
	for _, zone := range crops {
		for _, c := range zone {
			if i := zoneOf[c]; i != 0 {
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
