package policy

import (
	"reflect"
	"runtime"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// asciiCut builds a cut problem from rows: '#' terrain barrier, '.' ground
// that may be walled, 'B' the base, 'M' the opening's channel (the first is
// its mouth), 'L' the lane outside it. Ground on the map border is a source,
// as is the lane.
func asciiCut(rows []string, hp float64) cutInput {
	h, w := int32(len(rows)), int32(len(rows[0]))
	g := newCutGrid(w, h)
	in := cutInput{hp: make([]float64, w*h), hardToWall: make([]bool, w*h), keepOffWalls: make([]bool, w*h)}
	for z, row := range rows {
		for x, ch := range row {
			i := int32(z)*w + int32(x)
			in.hp[i] = hp
			switch ch {
			case '#':
				g.barrier[i] = true
			case 'B':
				g.sink[i] = true
			case 'M':
				g.barrier[i], in.keepOffWalls[i] = true, true
				if !in.hasOpening {
					in.mouth, in.hasOpening = i, true
				}
			case 'L':
				g.source[i], in.keepOffWalls[i] = true, true
			default:
				g.cost[i] = 1
				g.source[i] = x == 0 || z == 0 || int32(x) == w-1 || int32(z) == h-1
			}
		}
	}
	in.grid = g
	return in
}

// reachesBase floods from the sources over passable cells, the walls closed
// (and the channel too unless open), and reports whether the base is reached.
func reachesBase(in cutInput, walls []domain.Cell, openChannel bool) bool {
	g := in.grid
	closed := map[int32]bool{}
	for _, c := range walls {
		i, _ := g.idx(c)
		closed[i] = true
	}
	var queue []int32
	seen := map[int32]bool{}
	for i := int32(0); i < g.w*g.h; i++ {
		if g.source[i] && !g.barrier[i] && !closed[i] {
			seen[i] = true
			queue = append(queue, i)
		}
	}
	for q := 0; q < len(queue); q++ {
		u := queue[q]
		if g.sink[u] {
			return true
		}
		g.neighbours4(u, func(j int32) {
			passable := !g.barrier[j] || openChannel && in.keepOffWalls[j]
			if passable && !closed[j] && !seen[j] {
				seen[j] = true
				queue = append(queue, j)
			}
		})
	}
	return false
}

func field(w, h int, base [4]int, channel [4]int, lane [4]int) []string {
	rows := make([][]byte, h)
	for z := range rows {
		rows[z] = []byte(strings.Repeat(".", w))
	}
	fill := func(r [4]int, ch byte) {
		if r == [4]int{} {
			return
		}
		for z := r[1]; z <= r[3]; z++ {
			for x := r[0]; x <= r[2]; x++ {
				rows[z][x] = ch
			}
		}
	}
	fill(base, 'B')
	fill(lane, 'L')
	fill(channel, 'M')
	out := make([]string, h)
	for z, r := range rows {
		out[z] = string(r)
	}
	return out
}

// The base stands in open ground with its opening on the west side: x 22..24
// is the channel, z 20 the lane running west from it.
func openField(hp float64) cutInput {
	return asciiCut(field(70, 40, [4]int{25, 15, 35, 25}, [4]int{22, 19, 24, 21}, [4]int{10, 20, 21, 20}), hp)
}

func TestCutLeavesTheOpeningTheOnlyRoute(t *testing.T) {
	in := openField(510)
	cut := designCut(in)
	if len(cut.Cut) == 0 || len(cut.Walls) < len(cut.Cut) {
		t.Fatal("no cut", len(cut.Cut), len(cut.Walls))
	}
	if reachesBase(in, cut.Walls, false) {
		t.Fatal("raiders reach the base round the wall")
	}
	if !reachesBase(in, cut.Walls, true) {
		t.Fatal("the opening does not reach the base")
	}
	// Only walls close: the cut is a chain no pawn can step through, even
	// diagonally, and the walls never stand on the lane or the channel.
	for _, c := range cut.Walls {
		i, _ := in.grid.idx(c)
		if in.keepOffWalls[i] || in.grid.sink[i] || in.grid.barrier[i] {
			t.Fatal("wall on ground that is not walled", c)
		}
	}
}

func TestCutUsesTerrainBeforeWallCells(t *testing.T) {
	// A rock ring round the base with a three-wide gap in its north side, no
	// opening: the cheapest cut closes the gap, not a loop round the base.
	rows := field(60, 40, [4]int{28, 18, 31, 21}, [4]int{0, 0, 0, 0}, [4]int{0, 0, 0, 0})
	rows[0] = strings.Repeat(".", 60)
	grid := make([][]byte, len(rows))
	for z := range rows {
		grid[z] = []byte(rows[z])
	}
	for z := 10; z <= 30; z++ {
		for x := 20; x <= 40; x++ {
			if z == 10 || z == 30 || x == 20 || x == 40 {
				grid[z][x] = '#'
			}
		}
	}
	for x := 29; x <= 31; x++ {
		grid[10][x] = '.'
	}
	for z := range grid {
		rows[z] = string(grid[z])
	}
	in := asciiCut(rows, 510)
	in.hasOpening = false
	cut := designCut(in)
	if len(cut.Cut) != 3 {
		t.Fatalf("cut %d cells, want the 3-cell gap: %v", len(cut.Cut), cut.Cut)
	}
	for _, c := range cut.Cut {
		if c.X < 29 || c.X > 31 {
			t.Fatal("cut off the gap", c)
		}
	}
	if reachesBase(in, cut.Walls, false) {
		t.Fatal("base reachable")
	}
}

// thicknessAt is the thickness of the segment holding the cut cell nearest
// the given cell.
func thicknessAt(cut PerimeterCut, at domain.Cell) int {
	best, n := -1, 0
	for _, seg := range cut.Segments {
		for _, c := range seg.Cells {
			if d := int(abs32(c.X-at.X) + abs32(c.Z-at.Z)); best < 0 || d < best {
				best, n = d, seg.Thickness
			}
		}
	}
	return n
}

func TestCutThicknessFollowsTheDetourAndTheStuff(t *testing.T) {
	wood, granite := designCut(openField(195)), designCut(openField(510))
	near, far := domain.Cell{X: 21, Z: 19}, domain.Cell{X: 36, Z: 20}
	if thicknessAt(wood, near) != 1 || thicknessAt(granite, near) != 1 {
		t.Fatal("thickness beside the opening", thicknessAt(wood, near), thicknessAt(granite, near))
	}
	if thicknessAt(wood, far) <= thicknessAt(wood, near) || thicknessAt(granite, far) <= thicknessAt(granite, near) {
		t.Fatal("thickness does not grow with the detour", thicknessAt(wood, far), thicknessAt(granite, far))
	}
	if thicknessAt(wood, far) <= thicknessAt(granite, far) {
		t.Fatal("weaker stuff is not thicker", thicknessAt(wood, far), thicknessAt(granite, far))
	}
	// The thickness is the sapper formula: breaching N cells costs at least
	// the detour, one cell fewer less.
	for _, cut := range []PerimeterCut{wood, granite} {
		for _, seg := range cut.Segments {
			if seg.Reason != ReasonThicknessDetour {
				continue
			}
			step := SapperStepTicks(seg.HP)
			if float64(seg.Thickness)*step < seg.DetourTicks || seg.Thickness > 1 && float64(seg.Thickness-1)*step >= seg.DetourTicks {
				t.Fatal("thickness off the formula", seg.Thickness, seg.DetourTicks, step)
			}
		}
	}
	if len(wood.Walls) <= len(wood.Cut) {
		t.Fatal("walls are not thickened", len(wood.Walls), len(wood.Cut))
	}
}

func TestCutThicknessCapsAtTheBudget(t *testing.T) {
	// A long detour: a base 60 wide, its opening on the west side.
	in := asciiCut(field(240, 70, [4]int{150, 10, 210, 50}, [4]int{147, 29, 149, 31}, [4]int{100, 30, 146, 30}), 195)
	cut := designCut(in)
	capped, limit := false, SapperThickness(195, perimeterTable.DetourBudgetTicks)
	for _, seg := range cut.Segments {
		if seg.Thickness > limit {
			t.Fatal("thickness past the budget", seg.Thickness, limit)
		}
		capped = capped || seg.Reason == ReasonThicknessCapped
	}
	if !capped {
		t.Fatal("no segment reached the budget")
	}
}

func TestCutIsDeterministicAtAnyThreadCount(t *testing.T) {
	prev := runtime.GOMAXPROCS(1)
	defer runtime.GOMAXPROCS(prev)
	one := designCut(openField(300))
	runtime.GOMAXPROCS(8)
	for range 3 {
		if again := designCut(openField(300)); !reflect.DeepEqual(one, again) {
			t.Fatal("cut differs between runs")
		}
	}
}

func TestCutUnknownWallLeavesOneCell(t *testing.T) {
	cut := designCut(openField(0))
	for _, seg := range cut.Segments {
		if seg.Thickness != 1 || seg.Reason != ReasonThicknessUnknown {
			t.Fatal("unknown hit points thickened", seg)
		}
	}
	if len(cut.Walls) != len(cut.Cut) {
		t.Fatal("walls thickened without hit points")
	}
}

func TestCutPricesUnclosableGroundAndSaysSo(t *testing.T) {
	// Ground nothing can wall, all round the base: the cut must cross it.
	in := openField(300)
	g := in.grid
	for i := range g.cost {
		if g.cost[i] > 0 {
			g.cost[i], in.hardToWall[i] = cutGap, true
		}
	}
	cut := designCut(in)
	if len(cut.Unclosable) == 0 || cut.Decisions[0].Reason != ReasonCutOpen {
		t.Fatal("unclosable ground not reported", cut.Decisions)
	}
}

func TestCutSourceTouchingBaseHasNoCut(t *testing.T) {
	rows := field(20, 20, [4]int{8, 8, 11, 11}, [4]int{0, 0, 0, 0}, [4]int{0, 0, 0, 0})
	in := asciiCut(rows, 300)
	in.grid.source[8*20+8-1] = true
	in.grid.cost[8*20+8-1] = 0
	cut := designCut(in)
	if len(cut.Cut) != 0 || cut.Decisions[0].Reason != ReasonCutOpen {
		t.Fatal(cut.Decisions)
	}
}

func TestSapperThickness(t *testing.T) {
	// A wall of 510 hit points costs 70 + 102 + 13 per cell.
	if got := SapperStepTicks(510); got != 185 {
		t.Fatal(got)
	}
	for _, c := range []struct {
		hp, detour float64
		want       int
	}{{510, 0, 1}, {510, 185, 1}, {510, 186, 2}, {510, 370, 2}, {510, 371, 3}, {195, 1e9, SapperThickness(195, perimeterTable.DetourBudgetTicks)}, {0, 500, 1}} {
		if got := SapperThickness(c.hp, c.detour); got != c.want {
			t.Fatal(c, got)
		}
	}
}

func TestPlanPerimeterCutOverTheRecordedPlan(t *testing.T) {
	cell := func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} }
	s := zoningSurvey(200, cell)
	p := tiledPlan(s, 3)
	cut := PlanPerimeterCut(p, s, PerimeterWall{HP: domain.Known(510.0), LightHP: domain.Known(195.0)})
	if len(cut.Cut) == 0 || len(cut.Decisions) == 0 || cut.Decisions[0].Reason != ReasonCutTraced {
		t.Fatal("no cut", cut.Decisions)
	}
	// With the cut standing, the base is reached through the killbox opening
	// and nowhere else.
	k, ok := KillboxOpening(p)
	if !ok {
		t.Fatal("no opening")
	}
	walls := map[domain.Cell]bool{}
	for _, c := range cut.Walls {
		walls[c] = true
	}
	channel := map[domain.Cell]bool{}
	for t0 := int32(0); t0 < perimeterThick; t0++ {
		for a := int32(-1); a <= 1; a++ {
			channel[addCell(addCell(k.Entry, scale(directionOf(k.Toward), t0)), scale(perpendicular(directionOf(k.Toward)), a))] = true
		}
	}
	reach := func(openChannel bool) bool {
		enc := coreEnclosure(p, 200, 200)
		seen := map[domain.Cell]bool{}
		var queue []domain.Cell
		for x := int32(0); x < 200; x++ {
			for z := int32(0); z < 200; z++ {
				if x == 0 || z == 0 || x == 199 || z == 199 {
					c := domain.Cell{X: x, Z: z}
					seen[c] = true
					queue = append(queue, c)
				}
			}
		}
		for len(queue) > 0 {
			c := queue[0]
			queue = queue[1:]
			if enc.inside(c) {
				return true
			}
			for _, d := range directions {
				n := addCell(c, d)
				if n.X < 0 || n.Z < 0 || n.X >= 200 || n.Z >= 200 || seen[n] || walls[n] || channel[n] && !openChannel {
					continue
				}
				seen[n] = true
				queue = append(queue, n)
			}
		}
		return false
	}
	if reach(false) {
		t.Fatal("raiders reach the base round the cut")
	}
	if !reach(true) {
		t.Fatal("the opening does not lead to the base")
	}
}

func TestPlanPerimeterCutWithoutRoomsIsEmpty(t *testing.T) {
	s := zoningSurvey(50, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true} })
	if cut := PlanPerimeterCut(LayoutPlan{}, s, PerimeterWall{}); len(cut.Cut) != 0 || len(cut.Decisions) != 0 {
		t.Fatal(cut)
	}
}
