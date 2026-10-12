package combatsim

import (
	"errors"
	"math"
	"reflect"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Test palette. Rows of a map are z = 0, 1, ...; columns are x. Glyphs:
//
//	. open   # wall   D closed door   O open door   S sandbag   F fence
//	~ deep water   m mud   r roofed open   a-l pawns standing on open ground
const (
	fillSandbag = 0.55
	mudCost     = 14
)

var (
	testTerrain = []TerrainDef{
		{Name: "soil", PathCost: 0, Passable: true},
		{Name: "water", Passable: false},
		{Name: "mud", PathCost: mudCost, Passable: true},
	}
	testThings = []ThingDef{
		{Name: "wall", Fillage: FillFull, FillPercent: 1, Passability: Impassable},
		{Name: "door", Fillage: FillFull, FillPercent: 1, Passability: PassThroughOnly, Door: true, PathCost: 0},
		{Name: "sandbag", Fillage: FillPartial, FillPercent: fillSandbag, Passability: PassThroughOnly, PathCost: 14},
		{Name: "fence", Fillage: FillNone, Passability: Impassable},
	}
)

const (
	thWall = iota + 1
	thDoor
	thSandbag
	thFence
)

// scenario builds a Scenario from rows. speed is every pawn's MoveSpeed.
func scenario(t testing.TB, speed float64, rows ...string) Scenario {
	t.Helper()
	sc := Scenario{
		Width: len(rows[0]), Height: len(rows),
		Terrain: testTerrain, Things: testThings,
		WeatherMoveSpeed: domain.Known(1.0),
		StepTicks:        1,
	}
	n := sc.Width * sc.Height
	sc.TerrainIDs = make([]uint8, n)
	sc.ThingIDs = make([]uint8, n)
	sc.Roofed = make([]bool, n)
	for z, row := range rows {
		if len(row) != sc.Width {
			t.Fatalf("row %d has width %d, want %d", z, len(row), sc.Width)
		}
		for x, ch := range row {
			i := z*sc.Width + x
			switch {
			case ch == '#':
				sc.ThingIDs[i] = thWall
			case ch == 'D':
				sc.ThingIDs[i] = thDoor
			case ch == 'O':
				sc.ThingIDs[i] = thDoor
				sc.OpenDoors = append(sc.OpenDoors, domain.Cell{X: int32(x), Z: int32(z)})
			case ch == 'S':
				sc.ThingIDs[i] = thSandbag
			case ch == 'F':
				sc.ThingIDs[i] = thFence
			case ch == '~':
				sc.TerrainIDs[i] = 1
			case ch == 'm':
				sc.TerrainIDs[i] = 2
			case ch == 'r':
				sc.Roofed[i] = true
			case ch >= 'a' && ch <= 'l':
				sc.Pawns = append(sc.Pawns, PawnSpec{
					ID: domain.PawnID(string(ch)), Pos: domain.Cell{X: int32(x), Z: int32(z)}, MoveSpeed: domain.Known(speed),
				})
			}
		}
	}
	return sc
}

func newSim(t testing.TB, sc Scenario, seed uint64) *Sim {
	t.Helper()
	s := &Sim{}
	if _, err := s.Reset(sc, seed); err != nil {
		t.Fatal(err)
	}
	return s
}

func cell(x, z int32) domain.Cell { return domain.Cell{X: x, Z: z} }

func TestLineOfSight(t *testing.T) {
	rows := []string{
		".....",
		".#...",
		".....",
		"..D..",
		".....",
	}
	s := newSim(t, scenario(t, 6, rows...), 1)
	cases := []struct {
		name         string
		from, to     domain.Cell
		skipFirst    bool
		want         bool
		halfX, halfZ int
	}{
		{"open row", cell(0, 0), cell(4, 0), true, true, 0, 0},
		{"wall in the way", cell(0, 1), cell(4, 1), true, false, 0, 0},
		{"wall cell as the end is never tested", cell(0, 1), cell(1, 1), true, true, 0, 0},
		{"wall start without skipFirst blocks", cell(1, 1), cell(4, 1), false, false, 0, 0},
		{"wall start with skipFirst is skipped", cell(1, 1), cell(4, 1), true, true, 0, 0},
		{"closed door blocks", cell(2, 4), cell(2, 2), true, false, 0, 0},
		{"out of bounds start", cell(-1, 0), cell(2, 0), true, false, 0, 0},
	}
	for _, c := range cases {
		if got := s.g.lineOfSight(c.from, c.to, c.skipFirst, c.halfX, c.halfZ); got != c.want {
			t.Errorf("%s: lineOfSight(%v,%v) = %v, want %v", c.name, c.from, c.to, got, c.want)
		}
	}
}

func TestOpenDoorPassesSight(t *testing.T) {
	closed := newSim(t, scenario(t, 6, ".....", "##D##", "....."), 1)
	open := newSim(t, scenario(t, 6, ".....", "##O##", "....."), 1)
	if closed.CellCanSeeCell(cell(2, 0), cell(2, 2)) {
		t.Error("closed door should block sight")
	}
	if !open.CellCanSeeCell(cell(2, 0), cell(2, 2)) {
		t.Error("open door should pass sight")
	}
}

func TestCellCanSeeCell(t *testing.T) {
	// A single wall at (2,1). Straight across it is blocked, but the shooter
	// at (1,1) can lean north to (1,2) and see the target at (3,1) leaning to (3,2).
	s := newSim(t, scenario(t, 6,
		".....",
		"..#..",
		".....",
	), 1)
	if s.g.lineOfSight(cell(1, 1), cell(3, 1), true, 0, 0) {
		t.Fatal("setup: the wall should block the straight line")
	}
	if !s.CellCanSeeCell(cell(1, 1), cell(3, 1)) {
		t.Error("lean around the wall corner should give line of fire")
	}
	if !s.CellCanSeeCell(cell(3, 1), cell(1, 1)) {
		t.Error("the reverse lean should work too")
	}

	// A full wall row with no corner to lean past.
	wall := newSim(t, scenario(t, 6,
		".....",
		"#####",
		".....",
	), 1)
	if wall.CellCanSeeCell(cell(2, 0), cell(2, 2)) {
		t.Error("a solid wall row blocks all lines")
	}
	if wall.CellCanSeeCell(cell(1, 1), cell(2, 2)) {
		t.Error("a pawn cannot be seen from a wall cell")
	}
	if wall.CellCanSeeCell(cell(-1, 0), cell(2, 0)) {
		t.Error("out of bounds sees nothing")
	}
}

func TestDiagonalSightOnOpenGround(t *testing.T) {
	s := newSim(t, scenario(t, 6, ".....", ".....", ".....", ".....", "....."), 1)
	if !s.g.lineOfSight(cell(0, 4), cell(4, 0), true, 0, 0) || !s.CellCanSeeCell(cell(0, 4), cell(4, 0)) {
		t.Error("open ground has sight along the diagonal")
	}
}

func TestLeanSourcesOrder(t *testing.T) {
	s := newSim(t, scenario(t, 6,
		".....",
		"..#..",
		".....",
	), 1)
	got := s.g.leanSources(cell(1, 1), cell(3, 1), nil)
	// Wall to the east, target to the east: lean south then north; then the shooter's own
	// cell. No cover cell is leanable (the wall cell is blocked).
	want := []domain.Cell{cell(1, 0), cell(1, 2), cell(1, 1)}
	if !reflect.DeepEqual(got, want) {
		t.Errorf("leanSources = %v, want %v", got, want)
	}
}

func approx(a, b float64) bool { return math.Abs(a-b) < 1e-9 }

func TestCoverBlockChance(t *testing.T) {
	// Row z=1 with a sandbag beside the target at (3,1), shooter at (0,1).
	s := newSim(t, scenario(t, 6,
		".....",
		"..S..",
		".....",
	), 1)
	cases := []struct {
		name            string
		target, shooter domain.Cell
		benefits        bool
		want            float64
	}{
		{"cardinal cover on the line, shooter two away", cell(3, 1), cell(0, 1), true, fillSandbag * 0.66666},
		{"cover behind the target does not help", cell(1, 1), cell(0, 1), true, 0},
		{"race that cannot benefit", cell(3, 1), cell(0, 1), false, 0},
		{"shooter standing on the cover cell is skipped", cell(3, 1), cell(2, 1), true, 0},
		{"diagonal cover 45 degrees off is outside the arc", cell(3, 0), cell(0, 0), true, 0},
	}
	for _, c := range cases {
		if got := s.CoverBlockChance(c.target, c.shooter, c.benefits); !approx(got, c.want) {
			t.Errorf("%s: got %v, want %v", c.name, got, c.want)
		}
	}
}

func TestCoverAnglesAndDistance(t *testing.T) {
	// Sandbag at (2,2); target (3,1) is diagonal to it. Shooter at (0,2) sees
	// the bag 26.57 degrees off the firing line; diagonal cells scale the angle
	// by 1.75 (46.5 degrees), the 0.4 band, and the shooter is 2 cells from the
	// bag (x0.66666).
	s := newSim(t, scenario(t, 6,
		".....",
		".....",
		"..S..",
	), 1)
	got := s.CoverBlockChance(cell(3, 1), cell(0, 2), true)
	if want := fillSandbag * 0.4 * 0.66666; !approx(got, want) {
		t.Errorf("diagonal cover = %v, want %v", got, want)
	}
	// Shooter next to the bag: distance 1 scales by 0.3333.
	near := s.CoverBlockChance(cell(3, 1), cell(1, 2), true)
	if want := fillSandbag * 0.6 * 0.3333; !approx(near, want) {
		t.Errorf("close shooter = %v, want %v (32 degrees, x0.6)", near, want)
	}
}

func TestOpenDoorGivesNoCover(t *testing.T) {
	open := newSim(t, scenario(t, 6, ".....", "..O..", "....."), 1)
	closed := newSim(t, scenario(t, 6, ".....", "..D..", "....."), 1)
	if got := open.CoverBlockChance(cell(3, 1), cell(0, 1), true); got != 0 {
		t.Errorf("open door cover = %v, want 0", got)
	}
	if got := closed.CoverBlockChance(cell(3, 1), cell(0, 1), true); !approx(got, 0.75*0.66666) {
		t.Errorf("closed door cover = %v", got)
	}
}

// run steps until pred(events) or the tick limit; returns all events.
func run(t *testing.T, s *Sim, orders []Order, limit int, done func(Event) bool) []Event {
	t.Helper()
	var all []Event
	for range limit {
		_, ev, err := s.Step(orders)
		if err != nil {
			t.Fatal(err)
		}
		orders = nil
		for _, e := range ev {
			all = append(all, e)
			if done(e) {
				return all
			}
		}
	}
	t.Fatalf("not done after %d steps; events: %v", limit, all)
	return nil
}

func arrived(e Event) bool { return e.Kind == EventArrived }

func arrivalTick(ev []Event) int {
	for _, e := range ev {
		if e.Kind == EventArrived {
			return e.Tick
		}
	}
	return -1
}

func etaOf(ev []Event) int {
	for _, e := range ev {
		if e.Kind == EventMoveStarted {
			return e.ETA
		}
	}
	return -1
}

func TestMovementCostsAndArrival(t *testing.T) {
	// Speed 6 cells/s: 10 ticks per cardinal cell on open ground.
	cases := []struct {
		name    string
		rows    []string
		urgency Urgency
		to      domain.Cell
		want    int
	}{
		{"five cardinal cells", []string{"a....."}, Jog, cell(5, 0), 50},
		{"terrain path cost adds", []string{"ammm.."}, Jog, cell(3, 0), 3 * (10 + mudCost)},
		{"walk is double with a floor of 50", []string{"a...."}, Walk, cell(3, 0), 3 * 50},
		{"amble is triple with a floor of 60", []string{"a...."}, Amble, cell(2, 0), 2 * 60},
		{"sprint is three quarters, rounded to even", []string{"a...."}, Sprint, cell(4, 0), 4 * 8},
		{"sandbag path cost is the larger of terrain and thing", []string{"aS.."}, Jog, cell(2, 0), (10 + 14) + 10},
	}
	for _, c := range cases {
		s := newSim(t, scenario(t, 6, c.rows...), 1)
		ev := run(t, s, []Order{{Pawn: "a", To: c.to, Urgency: c.urgency}}, 1000, arrived)
		// The path request is claimed a tick late (A37): the cost is paid
		// from the second tick on.
		c.want += pathLatency
		if got := arrivalTick(ev); got != c.want {
			t.Errorf("%s: arrived at tick %d, want %d", c.name, got, c.want)
		}
		if eta := etaOf(ev); eta != c.want {
			t.Errorf("%s: ETA %d, want %d", c.name, eta, c.want)
		}
	}
}

func TestDiagonalAndCarryOverETA(t *testing.T) {
	// Diagonal steps cost 14.1421 ticks; the overshoot of each carries into the
	// next, so the planned arrival must equal the simulated arrival exactly.
	s := newSim(t, scenario(t, 6,
		"a.....",
		"......",
		"......",
		"......",
	), 1)
	ev := run(t, s, []Order{{Pawn: "a", To: cell(3, 3)}}, 1000, arrived)
	if etaOf(ev) != arrivalTick(ev) {
		t.Errorf("ETA %d != arrival %d", etaOf(ev), arrivalTick(ev))
	}
	// 3 diagonal steps of 14.1421: 42.4263 ticks of cost, 43 ticks with carry.
	if got := arrivalTick(ev); got != 43+pathLatency {
		t.Errorf("arrival %d, want %d", got, 43+pathLatency)
	}
	moved := 0
	for _, e := range ev {
		if e.Kind == EventMoved {
			moved++
		}
	}
	if moved != 3 {
		t.Errorf("entered %d cells, want 3 (the diagonal)", moved)
	}
}

func TestWeatherAndRoof(t *testing.T) {
	sc := scenario(t, 6, "arrr..")
	sc.WeatherMoveSpeed = domain.Known(0.5) // unroofed ground costs 20 ticks
	s := newSim(t, sc, 1)
	// Leaving the unroofed start costs 20 (the cost is priced from where the
	// pawn stands); leaving roofed cells costs 10.
	ev := run(t, s, []Order{{Pawn: "a", To: cell(3, 0)}}, 1000, arrived)
	if got, want := arrivalTick(ev), 20+10+10+pathLatency; got != want {
		t.Errorf("arrival %d, want %d", got, want)
	}
}

func TestPathsAroundBlockers(t *testing.T) {
	s := newSim(t, scenario(t, 6,
		"a.#..",
		"..#..",
		"..#..",
		".....",
	), 1)
	ev := run(t, s, []Order{{Pawn: "a", To: cell(4, 0)}}, 1000, arrived)
	for _, e := range ev {
		if e.Kind == EventMoved && s.g.thing(e.Cell).Passability == Impassable {
			t.Errorf("walked into a wall at %v", e.Cell)
		}
	}
	if eta, arr := etaOf(ev), arrivalTick(ev); eta != arr || arr <= 40 {
		t.Errorf("ETA %d arrival %d: the detour must cost more than the 4-cell straight line", eta, arr)
	}
}

func TestNoCornerCutting(t *testing.T) {
	// The only diagonal step from (0,0) to (1,1) passes between two walls: not allowed.
	s := newSim(t, scenario(t, 6,
		"a#",
		"#.",
	), 1)
	ev := run(t, s, []Order{{Pawn: "a", To: cell(1, 1)}}, 5, func(e Event) bool { return e.Kind == EventPathFailed })
	if ev[len(ev)-1].Kind != EventPathFailed {
		t.Errorf("expected path_failed, got %v", ev)
	}
}

func TestPathFailures(t *testing.T) {
	// Column x=2 walls off the right side of this two-row map.
	for name, to := range map[string]domain.Cell{
		"wall":          cell(2, 0),
		"deep water":    cell(3, 0),
		"fence":         cell(4, 0),
		"sandbag":       cell(4, 1),
		"out of bounds": cell(9, 9),
		"walled off":    cell(5, 0),
	} {
		s := newSim(t, scenario(t, 6, "a.#~F.", "..#.S."), 1)
		_, ev, err := s.Step([]Order{{Pawn: "a", To: to}})
		if err != nil {
			t.Fatal(err)
		}
		if len(ev) != 1 || ev[0].Kind != EventPathFailed {
			t.Errorf("%s: events %v, want one path_failed", name, ev)
		}
	}
}

func TestBlockedAndStop(t *testing.T) {
	// a's only way east runs through hostile b's cell: it waits there (a
	// friendly would not stop it, see collision_test.go).
	s := newSim(t, duel(t, testRifle, nil, "a.b.."), 1)
	ev := run(t, s, []Order{{Pawn: "a", To: cell(4, 0)}}, 200, func(e Event) bool { return e.Kind == EventBlocked })
	if last := ev[len(ev)-1]; last.Pawn != "a" || last.Cell != cell(2, 0) {
		t.Errorf("blocked event %v, want pawn a at (2,0)", last)
	}
	st, ev2, err := s.Step([]Order{{Pawn: "a", Kind: OrderStop}})
	if err != nil {
		t.Fatal(err)
	}
	if len(ev2) != 1 || ev2[0].Kind != EventStopped {
		t.Errorf("stop events %v", ev2)
	}
	if st.Pawns[0].Moving {
		t.Error("pawn still moving after stop")
	}
	if _, _, err := s.Step([]Order{{Pawn: "zz", To: cell(0, 0)}}); err == nil {
		t.Error("order for an unknown pawn must fail")
	}
}

func TestCoverCombinesAndFarShooter(t *testing.T) {
	// Two covers on one target combine as 1-(1-a)(1-b). Target (3,1), shooter
	// (0,2): the bag at (2,1) is 18 degrees off (x0.8), the bag at (2,2) is
	// diagonal (46.5 degrees, x0.4); both shooter distances are under 2.9.
	s := newSim(t, scenario(t, 6,
		".....",
		"..S..",
		"..S..",
	), 1)
	a := fillSandbag * 0.8 * 0.66666
	b := fillSandbag * 0.4 * 0.66666
	if got, want := s.CoverBlockChance(cell(3, 1), cell(0, 2), true), 1-(1-a)*(1-b); !approx(got, want) {
		t.Errorf("combined cover = %v, want %v", got, want)
	}
	// A shooter 4 cells from the bag gets no distance reduction.
	far := newSim(t, scenario(t, 6, ".......", "....S..", "......."), 1)
	if got := far.CoverBlockChance(cell(5, 1), cell(0, 1), true); !approx(got, fillSandbag) {
		t.Errorf("far shooter cover = %v, want %v", got, fillSandbag)
	}
	wall := newSim(t, scenario(t, 6, ".....", "..#..", "....."), 1)
	if got := wall.CoverBlockChance(cell(3, 1), cell(0, 1), true); !approx(got, 0.75*0.66666) {
		t.Errorf("wall base chance = %v, want %v", got, 0.75*0.66666)
	}
}

func TestDoorOpensOnApproach(t *testing.T) {
	s := newSim(t, scenario(t, 6, "a.D.."), 1)
	if s.CellCanSeeCell(cell(1, 0), cell(3, 0)) {
		t.Fatal("closed door should block sight before approach")
	}
	ev := run(t, s, []Order{{Pawn: "a", To: cell(4, 0)}}, 500, arrived)
	opened := 0
	for _, e := range ev {
		if e.Kind == EventDoorOpened && e.Cell == cell(2, 0) {
			opened++
		}
	}
	if opened != 1 {
		t.Errorf("door opened %d times, want 1", opened)
	}
	if !s.CellCanSeeCell(cell(1, 0), cell(3, 0)) {
		t.Error("open door should pass sight")
	}
}

func TestUnsupportedScenarios(t *testing.T) {
	base := func() Scenario { return scenario(t, 6, "a...", "....") }
	cases := map[string]func(*Scenario){
		"unknown speed":      func(s *Scenario) { s.Pawns[0].MoveSpeed = domain.Unknown[float64]() },
		"unknown weather":    func(s *Scenario) { s.WeatherMoveSpeed = domain.Unknown[float64]() },
		"zero weather":       func(s *Scenario) { s.WeatherMoveSpeed = domain.Known(0.0) },
		"no step":            func(s *Scenario) { s.StepTicks = 0 },
		"empty map":          func(s *Scenario) { s.Width = 0 },
		"grid size":          func(s *Scenario) { s.ThingIDs = s.ThingIDs[:3] },
		"palette index":      func(s *Scenario) { s.ThingIDs[1] = 99 },
		"pawn on a wall":     func(s *Scenario) { s.ThingIDs[0] = thWall },
		"pawn out of bounds": func(s *Scenario) { s.Pawns[0].Pos = cell(9, 9) },
		"duplicate id": func(s *Scenario) {
			s.Pawns = append(s.Pawns, PawnSpec{ID: "a", Pos: cell(2, 1), MoveSpeed: domain.Known(6.0)})
		},
		"open door that is not a door": func(s *Scenario) { s.OpenDoors = []domain.Cell{cell(1, 1)} },
	}
	for name, mutate := range cases {
		sc := base()
		mutate(&sc)
		if _, err := (&Sim{}).Reset(sc, 1); !errors.Is(err, ErrUnsupported) {
			t.Errorf("%s: err = %v, want ErrUnsupported", name, err)
		}
	}
	if _, err := (&Sim{}).Reset(base(), 1); err != nil {
		t.Errorf("base scenario: %v", err)
	}
}

func TestResetDoesNotMutateScenario(t *testing.T) {
	sc := scenario(t, 6, "a.D..")
	ids := append([]uint8(nil), sc.ThingIDs...)
	s := newSim(t, sc, 1)
	run(t, s, []Order{{Pawn: "a", To: cell(4, 0)}}, 500, arrived)
	if !reflect.DeepEqual(ids, sc.ThingIDs) || len(sc.OpenDoors) != 0 {
		t.Error("the scenario changed while the simulator ran")
	}
	// A second Reset returns to the start.
	st, err := s.Reset(sc, 1)
	if err != nil || st.Tick != 0 || st.Pawns[0].Pos != cell(0, 0) || s.CellCanSeeCell(cell(1, 0), cell(3, 0)) {
		t.Errorf("Reset did not restore the start: %+v %v", st, err)
	}
}

func fingerprint(t *testing.T, seed uint64) (events []Event, states []State, draws [][2]uint64) {
	t.Helper()
	sc := scenario(t, 5.3,
		"a..#....",
		"b.D.....",
		"..#..mm.",
		"c...S...",
	)
	sc.StepTicks = 3
	s := newSim(t, sc, seed)
	orders := []Order{
		{Pawn: "a", To: cell(7, 3)},
		{Pawn: "b", To: cell(6, 0), Urgency: Sprint},
		{Pawn: "c", To: cell(7, 0), Urgency: Walk},
	}
	for range 60 {
		st, ev, err := s.Step(orders)
		if err != nil {
			t.Fatal(err)
		}
		orders = nil
		events = append(events, ev...)
		st.Pawns = append([]PawnState(nil), st.Pawns...)
		states = append(states, st)
		strm := s.Streams()
		draws = append(draws, [2]uint64{strm.Physical.Uint64(), strm.Enemy.Uint64()})
	}
	return events, states, draws
}

func TestDeterminism(t *testing.T) {
	e1, s1, d1 := fingerprint(t, 42)
	e2, s2, d2 := fingerprint(t, 42)
	if len(e1) == 0 {
		t.Fatal("scenario produced no events")
	}
	if !reflect.DeepEqual(e1, e2) || !reflect.DeepEqual(s1, s2) || !reflect.DeepEqual(d1, d2) {
		t.Fatal("same seed and inputs must give identical events, states and draws")
	}
	_, _, d3 := fingerprint(t, 43)
	if reflect.DeepEqual(d1, d3) {
		t.Error("different seeds must give different random streams")
	}
	phys, enemy := 0, 0
	for i := range d1 {
		if d1[i][0] != d3[i][0] {
			phys++
		}
		if d1[i][1] != d3[i][1] {
			enemy++
		}
		if d1[i][0] == d1[i][1] {
			t.Fatalf("draw %d: the physical and enemy streams coincide", i)
		}
	}
	if phys != len(d1) || enemy != len(d1) {
		t.Errorf("seeds 42 and 43 agreed on %d physical and %d enemy draws", len(d1)-phys, len(d1)-enemy)
	}
}

func TestStreamsIndependent(t *testing.T) {
	// Extra enemy draws must not shift the physical stream.
	a := newSim(t, scenario(t, 6, "a..."), 7)
	b := newSim(t, scenario(t, 6, "a..."), 7)
	for range 100 {
		a.Streams().Enemy.Uint64()
	}
	for i := range 20 {
		if x, y := a.Streams().Physical.Uint64(), b.Streams().Physical.Uint64(); x != y {
			t.Fatalf("physical draw %d shifted by enemy draws", i)
		}
	}
}

func TestStepTicksAdvancesClock(t *testing.T) {
	sc := scenario(t, 6, "a....")
	sc.StepTicks = 7
	s := newSim(t, sc, 1)
	st, _, _ := s.Step(nil)
	st, _, _ = s.Step(nil)
	if st.Tick != 14 || s.Tick() != 14 {
		t.Errorf("tick = %d, want 14", st.Tick)
	}
}

// BenchmarkOpenMapMovement is the movement-only step rate for 12 pawns on an
// open 60x60 map: every pawn walks corner to corner and turns round on
// arrival. It reports game ticks per second.
func BenchmarkOpenMapMovement(b *testing.B) {
	rows := make([]string, 60)
	for i := range rows {
		rows[i] = strings.Repeat(".", 60)
	}
	sc := scenario(b, 4.6, rows...)
	for i := range 12 {
		sc.Pawns = append(sc.Pawns, PawnSpec{
			ID: domain.PawnID(string(rune('A' + i))), Pos: cell(int32(i*5), 0), MoveSpeed: domain.Known(4.6),
		})
	}
	s := newSim(b, sc, 1)
	far := make(map[domain.PawnID]bool)
	order := func(id domain.PawnID, pos domain.Cell, flip bool) Order {
		to := cell(pos.X, 59)
		if flip {
			to = cell(pos.X, 0)
		}
		return Order{Pawn: id, To: to}
	}
	var orders []Order
	for _, p := range sc.Pawns {
		orders = append(orders, order(p.ID, p.Pos, false))
		far[p.ID] = true
	}
	b.ReportAllocs()
	b.ResetTimer()
	for range b.N {
		st, ev, err := s.Step(orders)
		if err != nil {
			b.Fatal(err)
		}
		orders = orders[:0]
		for _, e := range ev {
			if e.Kind == EventArrived {
				far[e.Pawn] = !far[e.Pawn]
				orders = append(orders, order(e.Pawn, e.Cell, !far[e.Pawn]))
			}
		}
		_ = st
	}
	b.ReportMetric(float64(b.N)/b.Elapsed().Seconds(), "ticks/s")
}
