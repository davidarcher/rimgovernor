package combatsim

import (
	"errors"
	"fmt"
	"math"
	"reflect"
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

// factsFor gives every pawn of sc the facts the policy reads that the sim does
// not hold. The values are test inputs.
func factsFor(sc Scenario) map[domain.PawnID]PawnFacts {
	out := map[domain.PawnID]PawnFacts{}
	for _, p := range sc.Pawns {
		out[p.ID] = PawnFacts{Kind: "TestKind", Armor: domain.Known(0.2), MeleePower: domain.Known(3.3)}
	}
	return out
}

// adapterFor Resets a sim from the scenario and wraps it.
func adapterFor(t testing.TB, sc Scenario) *Adapter {
	t.Helper()
	s := newSim(t, sc, 1)
	a, err := NewAdapter(Fight{Name: "t", Scenario: sc, Facts: factsFor(sc)}, s)
	if err != nil {
		t.Fatal(err)
	}
	return a
}

// blank is an open map of w by h cells.
func blank(w, h int) []string {
	rows := make([]string, h)
	for i := range rows {
		rows[i] = strings.Repeat(".", w)
	}
	return rows
}

func withRow(rows []string, z int, x int, glyphs string) []string {
	out := slices.Clone(rows)
	out[z] = out[z][:x] + glyphs + out[z][x+len(glyphs):]
	return out
}

func TestViewMirrorsSimState(t *testing.T) {
	sc := duel(t, testRifle, nil, "a.b...")
	a := adapterFor(t, sc)
	v := a.View()
	if got, _ := v.Population.Value(); got != 1 {
		t.Fatalf("population = %v", v.Population)
	}
	if !reflect.DeepEqual(v.Orderable, []domain.PawnID{"a"}) {
		t.Fatalf("orderable = %v", v.Orderable)
	}
	if len(v.Pawns) != 2 || len(v.Defenders) != 1 || len(v.Threats) != 1 || len(v.Positional) != 1 {
		t.Fatalf("view sizes: %d pawns %d defenders %d threats %d positional", len(v.Pawns), len(v.Defenders), len(v.Threats), len(v.Positional))
	}
	if _, ok := v.Layout.Value(); ok {
		t.Fatal("layout is unknown without one")
	}
	pa := v.Pawns[0]
	if c, _ := pa.Cell.Value(); c != cell(0, 0) || pa.FireMode != policy.FireAtWill || pa.Weapon == "" || pa.Stance != policy.StanceIdle {
		t.Fatalf("colonist state = %+v", pa)
	}
	if v.Pawns[1].FireMode != "" {
		t.Fatalf("a hostile has no fire mode: %q", v.Pawns[1].FireMode)
	}
	if d, _ := v.Positional[0].NearestColonistDistance.Value(); d != 2 {
		t.Fatalf("nearest colonist distance = %v", d)
	}
	if dps, _ := v.Defenders[0].RangedDPS.Value(); dps <= 0 || math.IsNaN(dps) {
		t.Fatalf("ranged dps = %v", dps)
	}
	if h, _ := pa.Health.Value(); h != 1 {
		t.Fatalf("health = %v", h)
	}

	_, applied := a.Apply([]policy.CombatOrder{
		{Pawn: "a", Kind: policy.OrderHoldPosition},
		{Pawn: "a", Kind: policy.OrderFireMode, FireMode: policy.HoldFire},
	})
	if len(applied) != 1 {
		t.Fatalf("hold_position is one sim stop, fire_mode none: %v", applied)
	}
	pa = a.View().Pawns[0]
	if pa.Job != holdJob || pa.FireMode != policy.HoldFire {
		t.Fatalf("after hold: job %q fire mode %q", pa.Job, pa.FireMode)
	}
}

func TestApplyAdmission(t *testing.T) {
	sc := duel(t, testRifle, nil, "a.b#..", "c..#..")
	sc.Pawns[2].Side = combatmetrics.Colonist
	a := adapterFor(t, sc)
	a.orderable = map[domain.PawnID]bool{"a": true}
	cases := []struct {
		name  string
		order policy.CombatOrder
		want  string
	}{
		{"attack a hostile", policy.CombatOrder{Pawn: "a", Kind: policy.OrderAttack, Target: "b"}, ""},
		{"attack a friend", policy.CombatOrder{Pawn: "a", Kind: policy.OrderAttack, Target: "c"}, RefusedCannotHit},
		{"attack nobody", policy.CombatOrder{Pawn: "a", Kind: policy.OrderAttack, Target: "zz"}, RefusedNotFound},
		{"unowned pawn", policy.CombatOrder{Pawn: "c", Kind: policy.OrderStop}, RefusedNotDrafted},
		{"hostile pawn", policy.CombatOrder{Pawn: "b", Kind: policy.OrderStop}, RefusedNotDrafted},
		{"missing pawn", policy.CombatOrder{Pawn: "zz", Kind: policy.OrderStop}, RefusedNotFound},
		{"move onto a wall", policy.CombatOrder{Pawn: "a", Kind: policy.OrderMove, Cell: cell(3, 0)}, RefusedUnreachable},
		{"move off the map", policy.CombatOrder{Pawn: "a", Kind: policy.OrderMove, Cell: cell(9, 9)}, RefusedUnreachable},
		{"move", policy.CombatOrder{Pawn: "a", Kind: policy.OrderMove, Cell: cell(1, 1)}, ""},
		{"stop", policy.CombatOrder{Pawn: "a", Kind: policy.OrderStop}, ""},
		{"a kind the ranged slice does not run", policy.CombatOrder{Pawn: "a", Kind: "rescue"}, RefusedNativeRefuse},
	}
	for _, tc := range cases {
		results, out := a.Apply([]policy.CombatOrder{tc.order})
		if len(results) != 1 || results[0].Refusal != tc.want || results[0].Applied != (tc.want == "") {
			t.Errorf("%s: result = %+v, want refusal %q", tc.name, results, tc.want)
		}
		if tc.want != "" && len(out) != 0 {
			t.Errorf("%s: a refused order reached the sim: %v", tc.name, out)
		}
	}
}

func TestApplyRefusesBlockedLineOfFire(t *testing.T) {
	sc := duel(t, testRifle, nil, "a#b...")
	a := adapterFor(t, sc)
	results, _ := a.Apply([]policy.CombatOrder{{Pawn: "a", Kind: policy.OrderAttack, Target: "b"}})
	if results[0].Refusal != RefusedCannotHit {
		t.Fatalf("a wall between shooter and target: %+v", results[0])
	}
	m := a.Remember(policy.CombatMemory{}, results)
	if len(m.CannotHit) != 1 || m.CannotHit[0].From != cell(0, 0) {
		t.Fatalf("memory = %+v", m)
	}
}

func TestStopRaisesDownedAndRange(t *testing.T) {
	w := testRifle
	w.Range = 5
	sc := duel(t, w, nil, "a.........b")
	a := adapterFor(t, sc)
	a.scanRange() // baseline: nobody in reach
	if _, ok := a.Stop(nil); ok {
		t.Fatal("no stop with nothing in reach")
	}
	// Walk the hostile to within reach of the colonist.
	st, _ := runUntil(t, a.sim, []Order{{Pawn: "b", Kind: OrderMove, To: cell(5, 0)}}, 600, func(s State) bool {
		return s.Pawns[1].Pos == cell(5, 0)
	})
	stop, ok := a.Stop(nil)
	if !ok || stop.Kind != stopEnteredRange || stop.Pawn != "b" {
		t.Fatalf("stop = %+v %v at %v", stop, ok, st.Pawns)
	}
	if _, ok := a.Stop(nil); ok {
		t.Fatal("a hostile in reach stops once")
	}
	stop, ok = a.Stop([]Event{{Kind: EventDowned, Pawn: "a"}})
	if !ok || stop.Kind != policy.StopDowned || stop.Pawn != "a" {
		t.Fatalf("downed stop = %+v %v", stop, ok)
	}
	if _, ok = a.Stop([]Event{{Kind: EventDowned, Pawn: "b"}}); ok {
		t.Fatal("a downed hostile is not a stop")
	}
	// One hit of at least SeriousSingleHitDamage on a colonist is a serious
	// injury (A39); a lighter one, or one on a hostile, is not; a downing in
	// the same step wins.
	hit := func(target domain.PawnID, amount float64) Event {
		return Event{Kind: EventHit, Target: target, Amount: amount}
	}
	stop, ok = a.Stop([]Event{hit("a", float64(policy.SeriousSingleHitDamage))})
	if !ok || stop.Kind != policy.StopSeriousInjury || stop.Pawn != "a" {
		t.Fatalf("serious stop = %+v %v", stop, ok)
	}
	if _, ok = a.Stop([]Event{hit("a", float64(policy.SeriousSingleHitDamage)-1), hit("b", 30)}); ok {
		t.Fatal("a light hit or a hit on a hostile is not a stop")
	}
	stop, _ = a.Stop([]Event{hit("a", 25), {Kind: EventDowned, Pawn: "a"}})
	if stop.Kind != policy.StopDowned {
		t.Fatalf("downing and hit stop = %+v", stop)
	}
}

func lineSet(r policy.GeometryReply) map[string]bool {
	out := map[string]bool{}
	for _, l := range r.Lines {
		out[fmt.Sprintf("%v/%s/%v/%v", l.Cell, l.Hostile, l.LineOfFire, l.ColonistInPath)] = true
	}
	return out
}

func TestGeometryCoverBehindLine(t *testing.T) {
	rows := withRow(blank(9, 9), 3, 3, "SSS")
	rows = withRow(rows, 0, 4, "b")
	rows = withRow(rows, 5, 4, "a")
	sc := duel(t, testRifle, nil, rows...)
	a := adapterFor(t, sc)
	ask := &policy.GeometryRequest{Propose: policy.RoleCoverBehindLine, Cells: []domain.Cell{cell(4, 4)}, Line: []domain.Cell{cell(4, 4)}, Hostiles: []domain.PawnID{"b"}}
	r := a.Geometry(ask)
	if !r.Answered || r.Role != policy.RoleCoverBehindLine {
		t.Fatalf("reply = %+v", r)
	}
	if len(r.Proposals) == 0 {
		t.Fatal("cells behind the sandbags have cover")
	}
	if !slices.Contains(r.Proposals, cell(3, 4)) || !slices.Contains(r.Proposals, cell(5, 4)) {
		t.Fatalf("proposals = %v", r.Proposals)
	}
	for _, c := range r.Proposals {
		if c == cell(4, 4) || !a.sim.g.standable(c) {
			t.Errorf("proposal %v is on the line or not standable", c)
		}
	}
	for _, sc := range r.Scored {
		if slices.Contains(r.Proposals, sc.Cell) && sc.Lines[0].Cover <= 0 {
			t.Errorf("proposal %v has no cover: %+v", sc.Cell, sc)
		}
	}
	// Sorted: best cover first.
	prev := math.Inf(1)
	for _, sc := range r.Scored[1:] {
		if sc.Lines[0].Cover > prev+1e-9 {
			t.Errorf("proposals not best-cover-first: %+v", r.Scored)
		}
		prev = sc.Lines[0].Cover
	}
	if got := a.sim.CoverBlockChance(cell(3, 4), cell(4, 0), true); got <= 0 {
		t.Fatalf("sim cover = %v", got)
	}
	// The same ask twice answers the same: nothing is carried between calls.
	if again := a.Geometry(ask); !reflect.DeepEqual(r, again) {
		t.Fatal("geometry is not repeatable")
	}
}

func TestGeometryAdjacentToChoke(t *testing.T) {
	sc := duel(t, testRifle, nil, withRow(withRow(blank(21, 12), 0, 10, "b"), 11, 10, "a")...)
	a := adapterFor(t, sc)
	r := a.Geometry(&policy.GeometryRequest{Propose: policy.RoleAdjacentToChoke, Choke: cell(10, 5), OurSide: cell(10, 9), Hostiles: []domain.PawnID{"b"}})
	want := []domain.Cell{cell(10, 6), cell(9, 6), cell(11, 6)}
	if !reflect.DeepEqual(r.Proposals, want) {
		t.Fatalf("proposals = %v, want %v", r.Proposals, want)
	}
	if !reflect.DeepEqual(r.Standable, want) {
		t.Fatalf("standable = %v", r.Standable)
	}
	if n := len(r.Lines); n != len(want) {
		t.Fatalf("one hostile and three proposals give three lines, got %d", n)
	}
}

func TestGeometryFiringCells(t *testing.T) {
	rows := blank(30, 20)
	for z := 5; z < 15; z++ {
		rows = withRow(rows, z, 10, "#")
	}
	rows = withRow(rows, 10, 20, "b")
	rows = withRow(rows, 10, 2, "a")
	sc := duel(t, testRifle, nil, rows...)
	a := adapterFor(t, sc)
	target := cell(20, 10)
	from := cell(14, 10)
	r := a.Geometry(&policy.GeometryRequest{Propose: policy.RoleFiringCells, From: from, Targets: []domain.Cell{target}, Hostiles: []domain.PawnID{"b"}})
	if len(r.Proposals) == 0 {
		t.Fatal("no firing cells")
	}
	for _, c := range r.Proposals {
		if distSq(c, from) > 144 || !a.sim.g.lineOfSight(c, target, true, 0, 0) || !a.sim.g.standable(c) {
			t.Errorf("proposal %v cannot fire from here", c)
		}
	}
	if slices.Contains(r.Proposals, cell(9, 10)) {
		t.Error("a cell behind the wall cannot see the target")
	}
	if len(r.Proposals) > geometryMaxCells {
		t.Errorf("%d proposals over the cap", len(r.Proposals))
	}
}

func TestGeometryRefusalsAreEmptyAnswers(t *testing.T) {
	sc := duel(t, testRifle, nil, "a.....b")
	a := adapterFor(t, sc)
	cases := map[string]*policy.GeometryRequest{
		"unknown hostile": {Cells: []domain.Cell{cell(1, 0)}, Hostiles: []domain.PawnID{"zz"}},
		"cell off map":    {Cells: []domain.Cell{cell(40, 0)}, Hostiles: []domain.PawnID{"b"}},
		"repeated id":     {Cells: []domain.Cell{cell(1, 0)}, Hostiles: []domain.PawnID{"b", "b"}},
		"choke is ours":   {Propose: policy.RoleAdjacentToChoke, Choke: cell(2, 0), OurSide: cell(2, 0), Hostiles: []domain.PawnID{"b"}},
		"no hostiles":     {Propose: policy.RoleFiringCells, From: cell(1, 0), Targets: []domain.Cell{cell(5, 0)}},
	}
	for name, ask := range cases {
		r := a.Geometry(ask)
		if !r.Answered || len(r.Lines) != 0 || len(r.Proposals) != 0 || len(r.Standable) != 0 {
			t.Errorf("%s: reply = %+v", name, r)
		}
	}
	if r := a.Geometry(nil); !r.Answered || len(r.Lines) != 0 {
		t.Errorf("nil ask: %+v", r)
	}
	// Named cells score without a proposal role.
	r := a.Geometry(&policy.GeometryRequest{Cells: []domain.Cell{cell(1, 0), cell(3, 0)}, Hostiles: []domain.PawnID{"b"}})
	if len(r.Lines) != 2 || len(r.Standable) != 2 || len(r.Proposals) != 0 {
		t.Errorf("named cells: %+v", r)
	}
}

func TestGeometryFollowsTheWorld(t *testing.T) {
	rows := blank(9, 7)
	for z := 0; z <= 2; z++ {
		rows = withRow(rows, z, 4, "#")
	}
	rows = withRow(rows, 1, 6, "b")
	rows = withRow(rows, 1, 2, "a")
	sc := duel(t, testRifle, nil, rows...)
	a := adapterFor(t, sc)
	ask := &policy.GeometryRequest{Cells: []domain.Cell{cell(2, 1)}, Hostiles: []domain.PawnID{"b"}}
	if r := a.Geometry(ask); r.Lines[0].LineOfFire {
		t.Fatal("the wall blocks the line")
	}
	runUntil(t, a.sim, []Order{{Pawn: "b", Kind: OrderMove, To: cell(6, 6)}}, 2000, func(s State) bool { return s.Pawns[1].Pos == cell(6, 6) })
	if r := a.Geometry(ask); !r.Lines[0].LineOfFire {
		t.Fatal("the hostile came round the wall and the line is open")
	}
}

func TestOnPath(t *testing.T) {
	colonists := map[domain.Cell]bool{cell(3, 0): true}
	if !onPath(cell(0, 0), cell(6, 0), colonists) {
		t.Error("a colonist in the middle of the line is in the path")
	}
	if onPath(cell(3, 0), cell(6, 0), colonists) || onPath(cell(0, 0), cell(3, 0), colonists) {
		t.Error("the ends of the line do not count")
	}
	if onPath(cell(0, 2), cell(6, 2), colonists) {
		t.Error("a colonist off the line is not in the path")
	}
}

func TestGeometryRescuePath(t *testing.T) {
	rows := blank(10, 3)
	rows = withRow(rows, 0, 4, "#")
	rows = withRow(rows, 2, 4, "#")
	rows = withRow(rows, 1, 4, "D")
	rows = withRow(rows, 1, 0, "a")
	rows = withRow(rows, 1, 9, "b")
	sc := duel(t, testRifle, nil, rows...)
	a := adapterFor(t, sc)
	r := a.Geometry(&policy.GeometryRequest{Propose: policy.RoleRescuePath, Pawn: "a", To: cell(7, 1), Hostiles: []domain.PawnID{"b"}})
	if !r.Answered || len(r.Route) == 0 {
		t.Fatalf("reply = %+v", r)
	}
	last := r.Route[len(r.Route)-1].Cell
	if max(abs32(last.X-7), abs32(last.Z-1)) > 1 {
		t.Errorf("the route ends at %v, not touching the goal", last)
	}
	doors := 0
	for _, rc := range r.Route {
		if rc.Door {
			doors++
			if rc.Cell != cell(4, 1) {
				t.Errorf("door flagged at %v", rc.Cell)
			}
		}
	}
	if doors != 1 {
		t.Errorf("the route crosses the door once, flagged %d times", doors)
	}
	if bad := a.Geometry(&policy.GeometryRequest{Propose: policy.RoleRescuePath, Pawn: "zz", To: cell(7, 1), Hostiles: []domain.PawnID{"b"}}); len(bad.Route) != 0 {
		t.Errorf("unknown walker: %+v", bad)
	}
}

// laneFight is the 4v4 ranged fight: four colonists on the left behind a
// sandbag line, four hostiles on the right that close on the nearest colonist.
func laneFight(t testing.TB, name string) Fight {
	t.Helper()
	rows := blank(40, 12)
	for z := 1; z < 9; z += 2 {
		rows = withRow(rows, z, 6, "S")
	}
	sc := duel(t, testRifle, nil, rows...)
	for i := range 4 {
		for _, side := range []string{combatmetrics.Colonist, combatmetrics.Hostile} {
			x, prefix := int32(5), "c"
			if side == combatmetrics.Hostile {
				x, prefix = 30, "h"
			}
			w := testRifle
			sc.Pawns = append(sc.Pawns, PawnSpec{
				ID: domain.PawnID(fmt.Sprintf("%s%d", prefix, i)), Pos: cell(x, int32(1+i*2)),
				MoveSpeed: domain.Known(4.6), Side: side, Combat: testCombat(&w),
			})
		}
	}
	sc.StepTicks = 6
	side := sides(sc)
	return Fight{
		Name: name, Scenario: sc, Facts: factsFor(sc), MaxTicks: 6000, BackstopTicks: 120,
		Enemy: func(st State) []Order {
			var hostile []Order
			for _, o := range nearestEnemy(st, side, nil) {
				if side[o.Pawn] == combatmetrics.Hostile {
					hostile = append(hostile, o)
				}
			}
			return hostile
		},
	}
}

func TestRunFightFourVersusFour(t *testing.T) {
	f := laneFight(t, "lane-4v4")
	res, err := RunFight(f, 7, policy.DefaultCombatTuning())
	if err != nil {
		t.Fatal(err)
	}
	if res.Decisions == 0 || res.Orders == 0 {
		t.Fatalf("the policy never decided: %+v", res)
	}
	if res.Metrics.Fixture != "lane-4v4" || res.Metrics.Ticks != res.Ticks || res.Metrics.Ticks == 0 {
		t.Fatalf("metrics row = %+v", res.Metrics)
	}
	if !res.Resolved && res.Ticks < f.MaxTicks {
		t.Fatalf("ended unresolved before the budget: %+v", res)
	}
	if res.Resolved && res.Metrics.Winner != res.Winner {
		t.Fatalf("winner %q != metrics winner %q", res.Winner, res.Metrics.Winner)
	}
	if res.Metrics.FirstContactTick < 0 {
		t.Fatalf("a ranged fight had no contact: %+v", res.Metrics)
	}
	if res.Metrics.ColonistDamage+float64(res.Metrics.EnemyDowns+res.Metrics.EnemyDeaths) == 0 {
		t.Fatalf("nobody was hurt: %+v", res.Metrics)
	}
}

func TestRunFightIsDeterministic(t *testing.T) {
	run := func(seed uint64) FightResult {
		res, err := RunFight(laneFight(t, "lane-4v4"), seed, policy.DefaultCombatTuning())
		if err != nil {
			t.Fatal(err)
		}
		return res
	}
	a, b := run(3), run(3)
	if !reflect.DeepEqual(a, b) {
		t.Fatalf("same seed, different fights:\n%+v\n%+v", a, b)
	}
}

func TestRunFightUnsupportedFacts(t *testing.T) {
	cases := map[string]func(f *Fight){
		"no facts at all":   func(f *Fight) { f.Facts = nil },
		"no kind":           func(f *Fight) { f.Facts["c0"] = PawnFacts{Armor: domain.Known(0.2), MeleePower: domain.Known(1.0)} },
		"no colonist armor": func(f *Fight) { f.Facts["c0"] = PawnFacts{Kind: "k", MeleePower: domain.Known(1.0)} },
		"no melee power":    func(f *Fight) { f.Facts["h0"] = PawnFacts{Kind: "k"} },
		"facts for a ghost": func(f *Fight) { f.Facts["ghost"] = PawnFacts{Kind: "k"} },
		"orderable hostile": func(f *Fight) { f.Orderable = []domain.PawnID{"h0"} },
	}
	for name, mod := range cases {
		f := laneFight(t, "x")
		mod(&f)
		if _, err := RunFight(f, 1, policy.DefaultCombatTuning()); !errors.Is(err, ErrUnsupported) {
			t.Errorf("%s: err = %v, want ErrUnsupported", name, err)
		}
	}
	f := laneFight(t, "x")
	f.MaxTicks = 0
	if _, err := RunFight(f, 1, policy.DefaultCombatTuning()); err == nil {
		t.Error("a fight with no tick budget is refused")
	}
	f = laneFight(t, "x")
	f.Scenario.Pawns[0].Combat.Body = nil
	if _, err := RunFight(f, 1, policy.DefaultCombatTuning()); !errors.Is(err, ErrUnsupported) {
		t.Errorf("an unknown sim fact is unsupported: %v", err)
	}
}

// TestRecordedGeometryMatches checks the sim-built reply against the native
// answer recorded at lab-ranged's first stop: the same scene (open ground, a
// seven-cell sandbag line at z=42, four riflemen behind it and four raiders 25
// cells north) built in the sim answers the same ask with the same standable
// cells, proposals and lines of fire, and the policy, given either reply with
// the same view and memory, gives the same orders. The recordings carry no
// terrain and no stance injection, so the scene is rebuilt from the fixture's
// layout and the view comes from the sim, not from the recorded frame.
func TestRecordedGeometryMatches(t *testing.T) {
	stops, err := snap.CombatStops("../buildingruntime/testdata/combat/lab-ranged.json.gz")
	if err != nil {
		t.Fatal(err)
	}
	var rec snap.RecordedStop
	for _, s := range stops {
		if s.Ask != nil && len(s.Reply.Lines) > 0 {
			rec = s
			break
		}
	}
	if rec.Ask == nil {
		t.Fatal("no recorded stop with a geometry answer")
	}
	rows := blank(100, 100)
	rows = withRow(rows, 42, 47, "SSSSSSS")
	sc := duel(t, testRifle, nil, rows...)
	sc.Pawns = nil
	for _, p := range rec.Frame.GetCombatPawns() {
		side := combatmetrics.Hostile
		if p.GetSide().String() == "COMBAT_SIDE_COLONIST" {
			side = combatmetrics.Colonist
		}
		w := testRifle
		sc.Pawns = append(sc.Pawns, PawnSpec{
			ID: domain.PawnID(p.GetId()), Pos: cell(p.GetCell().GetX(), p.GetCell().GetZ()),
			MoveSpeed: domain.Known(4.6), Side: side, Combat: testCombat(&w),
		})
	}
	a := adapterFor(t, sc)
	got := a.Geometry(rec.Ask)
	want := rec.Reply

	if !reflect.DeepEqual(got.Standable, want.Standable) {
		t.Errorf("standable:\n sim      %v\n recorded %v", got.Standable, want.Standable)
	}
	if !reflect.DeepEqual(got.Proposals, want.Proposals) {
		t.Errorf("proposals:\n sim      %v\n recorded %v", got.Proposals, want.Proposals)
	}
	gl, wl := lineSet(got), lineSet(want)
	for k := range wl {
		if !gl[k] {
			t.Errorf("recorded line %s missing from the sim's answer", k)
		}
	}
	for k := range gl {
		if !wl[k] {
			t.Errorf("sim line %s not in the recorded answer", k)
		}
	}

	view := a.View()
	memory := rec.MemoryIn
	fromSim, _, _ := policy.DecideCombat(view, got, rec.Stop, memory)
	fromRec, _, _ := policy.DecideCombat(view, want, rec.Stop, memory)
	if !reflect.DeepEqual(fromSim, fromRec) {
		t.Errorf("orders differ:\n sim      %v\n recorded %v", fromSim, fromRec)
	}
	if len(fromSim) == 0 {
		t.Error("the matched stop gave no orders, so the comparison proved nothing")
	}
}
