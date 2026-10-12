package combatsim

import (
	"fmt"
	"math"
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// longRifle reaches across the test maps.
func longRifle() WeaponDef {
	w := testRifle
	w.Range = 60
	return w
}

// raidScene is duel's scenario with the pawns named in colonists on the
// colonist side and every other lettered pawn a hostile raider.
func raidScene(t testing.TB, w WeaponDef, colonists string, rows ...string) Scenario {
	t.Helper()
	sc := duel(t, w, immortal, rows...)
	for i := range sc.Pawns {
		p := &sc.Pawns[i]
		p.Side = combatmetrics.Hostile
		if strings.Contains(colonists, string(p.ID)) {
			p.Side = combatmetrics.Colonist
		}
	}
	return sc
}

func raidAI(t testing.TB, sc Scenario, seed uint64, cfg EnemyConfig) (*Sim, *EnemyAI) {
	t.Helper()
	s := newSim(t, sc, seed)
	e, err := NewEnemyAI(s, cfg)
	if err != nil {
		t.Fatal(err)
	}
	return s, e
}

// emptyRows is an open map of w by h with the given glyphs written at cells.
func emptyRows(w, h int, glyphs map[domain.Cell]byte) []string {
	rows := make([][]byte, h)
	for z := range rows {
		rows[z] = []byte(strings.Repeat(".", w))
	}
	for c, g := range glyphs {
		rows[c.Z][c.X] = g
	}
	out := make([]string, h)
	for z := range rows {
		out[z] = string(rows[z])
	}
	return out
}

func near(t *testing.T, what string, got, want float64) {
	t.Helper()
	if math.Abs(got-want) > 1e-9 {
		t.Errorf("%s = %v, want %v", what, got, want)
	}
}

// The paper's example (section 2.1): scores 80, 65 and 40 weigh 1, 0.5 and 0.
func TestSelectionWeights(t *testing.T) {
	inf := math.Inf(-1)
	for _, c := range []struct {
		name   string
		scores []float64
		want   []float64
	}{
		{"within 30 of the best", []float64{80, 65, 40}, []float64{1, 0.5, 0}},
		{"just inside the band", []float64{80, 50.0001}, []float64{1, 0.0001 / 30}},
		{"unshootable pawns weigh nothing", []float64{inf, 30, inf, 20}, []float64{0, 1, 0, 2.0 / 3}},
		{"best below 1 takes the first best alone", []float64{0.5, -3, 0.5}, []float64{1, 0, 0}},
		{"best exactly 1 uses the band", []float64{1, -28}, []float64{1, 1.0 / 30}},
		{"nobody", []float64{inf, inf}, []float64{0, 0}},
	} {
		got := selectionWeights(c.scores, nil)
		for i := range c.want {
			if math.Abs(got[i]-c.want[i]) > 1e-9 {
				t.Errorf("%s: weights = %v, want %v", c.name, got, c.want)
				break
			}
		}
	}
}

// Three colonists at 10, 25 and 38 cells score 50, 35 and 22: weights 1, 0.5
// and 2/30, so the draws are 1 : 0.5 : 0.0667 over 10k.
func TestTargetChoiceDistribution(t *testing.T) {
	rows := emptyRows(81, 61, map[domain.Cell]byte{
		cell(40, 30): 'h', cell(50, 30): 'a', cell(40, 5): 'b', cell(2, 30): 'c',
	})
	sc := raidScene(t, longRifle(), "abc", rows...)
	s, e := raidAI(t, sc, 11, EnemyConfig{})
	h, a, b, c := s.pawnIndex("h"), s.pawnIndex("a"), s.pawnIndex("b"), s.pawnIndex("c")
	near(t, "score a", e.score(h, a), 50)
	near(t, "score b", e.score(h, b), 35)
	near(t, "score c", e.score(h, c), 22)
	const n = 10000
	count := map[int]int{}
	for range n {
		count[e.pickByScore(h, e.valid(h, enemyAcquireRadius, 0, true))]++
	}
	total := 1 + 0.5 + 2.0/30
	within(t, "a", count[a], n, 1/total)
	within(t, "b", count[b], n, 0.5/total)
	within(t, "c", count[c], n, (2.0/30)/total)
}

func TestTargetScoreTerms(t *testing.T) {
	// h shoots east at a, 10 cells away. s is a sandbag beside a; f a friend
	// of h and k a colonist on the shot line past 5 cells.
	rows := emptyRows(60, 20, map[domain.Cell]byte{
		cell(20, 10): 'h', cell(30, 10): 'a', cell(29, 10): 'S',
	})
	sc := raidScene(t, longRifle(), "a", rows...)
	s, e := raidAI(t, sc, 1, EnemyConfig{})
	h, a := s.pawnIndex("h"), s.pawnIndex("a")
	cover := s.CoverBlockChance(cell(30, 10), cell(20, 10), true)
	near(t, "sandbag block chance", cover, fillSandbag)
	near(t, "distance and cover", e.score(h, a), 60-10-fillSandbag*10)

	s.pawns[a].stance, s.pawns[a].cast = stanceWarmup, h
	near(t, "aimed at the shooter", e.score(h, a), 60-10-fillSandbag*10+10)
	s.pawns[a].stance = stanceIdle
	near(t, "idle pawn aims at nobody", e.score(h, a), 60-10-fillSandbag*10)

	s.tick = 1000
	s.pawns[h].lastAttacked, s.pawns[h].lastAttackTick = a, 700
	near(t, "attacked 300 ticks ago", e.score(h, a), 60-10-fillSandbag*10+40)
	s.pawns[h].lastAttackTick = 699
	near(t, "attacked 301 ticks ago", e.score(h, a), 60-10-fillSandbag*10)

	w := &s.pawns[h].weapon
	w.TargetHasRangedAttackScoreOffset = 7
	near(t, "target carries a ranged weapon", e.score(h, a), 60-10-fillSandbag*10+7)
}

func TestFriendlyFireConeTerms(t *testing.T) {
	// A friend 6 cells down the line from h and a colonist 7 cells down it.
	rows := emptyRows(60, 20, map[domain.Cell]byte{
		cell(20, 10): 'h', cell(40, 10): 'a', cell(26, 10): 'g', cell(27, 10): 'b',
	})
	sc := raidScene(t, longRifle(), "ab", rows...)
	s, e := raidAI(t, sc, 1, EnemyConfig{})
	h, a := s.pawnIndex("h"), s.pawnIndex("a")
	f := func(d float64) float64 { return (d*d - 25) / (144 - 25) }
	want := -18*f(6) + 18*0.6*f(7)
	near(t, "cone offset", e.coneOffset(h, a), want)
	near(t, "score includes the cone", e.score(h, a), 60-20+want)
	if !math.IsNaN(e.coneOffset(h, a)) && e.coneOffset(h, a) == 0 {
		t.Fatal("the cone found nobody")
	}
}

func TestUnshootableTargetsFallBackToTheNearestReachable(t *testing.T) {
	short := testRifle
	short.Range = 8
	rows := emptyRows(60, 20, map[domain.Cell]byte{cell(10, 10): 'h', cell(30, 10): 'a', cell(10, 2): 'b'})
	sc := raidScene(t, short, "ab", rows...)
	s, e := raidAI(t, sc, 1, EnemyConfig{})
	h, b := s.pawnIndex("h"), s.pawnIndex("b")
	if got := e.findAttackTarget(h); got != b {
		t.Fatalf("nearest target = %d, want b (%d)", got, b)
	}
	// Wall b in: it is no longer reachable, a still is.
	walled := map[domain.Cell]byte{cell(10, 10): 'h', cell(30, 10): 'a', cell(10, 2): 'b'}
	for dx := int32(-1); dx <= 1; dx++ {
		for dz := int32(-1); dz <= 1; dz++ {
			if dx != 0 || dz != 0 {
				walled[cell(10+dx, 2+dz)] = '#'
			}
		}
	}
	sc = raidScene(t, short, "ab", emptyRows(60, 20, walled)...)
	s, e = raidAI(t, sc, 1, EnemyConfig{})
	if got := e.findAttackTarget(s.pawnIndex("h")); got != s.pawnIndex("a") {
		t.Fatalf("with b walled in, target = %d, want a", got)
	}
}

func TestTargetRetention(t *testing.T) {
	rows := emptyRows(120, 20, map[domain.Cell]byte{cell(10, 10): 'h', cell(30, 10): 'a', cell(100, 10): 'b'})
	sc := raidScene(t, longRifle(), "ab", rows...)
	s, e := raidAI(t, sc, 1, EnemyConfig{})
	h, a, b := s.pawnIndex("h"), s.pawnIndex("a"), s.pawnIndex("b")
	e.p[h].enemy = a
	check := func(name string, want bool) {
		t.Helper()
		if got := e.shouldLoseTarget(h); got != want {
			t.Errorf("%s: shouldLoseTarget = %v, want %v", name, got, want)
		}
	}
	e.p[h].lastEngage = 0
	s.tick = 400
	check("engaged 400 ticks ago", false)
	s.tick = 401
	check("engaged 401 ticks ago", true)
	s.tick = 400
	s.pawns[a].downed = true
	check("downed", true)
	s.pawns[a].downed, s.pawns[a].dead = false, true
	check("dead", true)
	s.pawns[a].dead = false
	e.p[h].enemy = b // 90 cells away
	e.p[h].lastEngage = s.tick
	check("beyond the keep radius", true)
	s.pawns[b].pos = cell(10+72, 10)
	check("exactly at the keep radius", false)
	s.pawns[b].pos = cell(10+73, 10)
	check("just past the keep radius", true)
}

func TestTargetRetentionNeedsAWayThere(t *testing.T) {
	glyphs := map[domain.Cell]byte{cell(10, 10): 'h', cell(30, 10): 'a'}
	for z := int32(0); z < 20; z++ {
		glyphs[cell(20, z)] = '#'
	}
	sc := raidScene(t, longRifle(), "a", emptyRows(60, 20, glyphs)...)
	s, e := raidAI(t, sc, 1, EnemyConfig{})
	h, a := s.pawnIndex("h"), s.pawnIndex("a")
	e.p[h].enemy, e.p[h].lastEngage = a, 0
	if !e.shouldLoseTarget(h) {
		t.Error("a target behind an unbroken wall is lost")
	}
	if got := e.findAttackTarget(h); got != -1 {
		t.Errorf("no target is reachable or visible, got %d", got)
	}
}

func TestEngagementRefreshesOnShots(t *testing.T) {
	rows := emptyRows(60, 20, map[domain.Cell]byte{cell(10, 10): 'h', cell(30, 10): 'a'})
	sc := raidScene(t, longRifle(), "a", rows...)
	s, e := raidAI(t, sc, 1, EnemyConfig{})
	h, a := s.pawnIndex("h"), s.pawnIndex("a")
	e.p[h].enemy, e.p[h].lastEngage = a, 0
	s.pawns[h].lastAttacked, s.pawns[h].lastAttackTick = a, 3000
	s.tick = 3100
	e.Orders()
	if e.p[h].lastEngage != 3000 {
		t.Fatalf("lastEngage = %d after a shot at the enemy target", e.p[h].lastEngage)
	}
	if e.shouldLoseTarget(h) {
		t.Fatal("a target shot at 100 ticks ago is kept")
	}
}

func orderFor(os []Order, id domain.PawnID) (Order, bool) {
	for _, o := range os {
		if o.Pawn == id {
			return o, true
		}
	}
	return Order{}, false
}

func TestRaiderWaitsWhereItCanShootInCoverOrClose(t *testing.T) {
	// Close and in sight: wait (distance 8 < 5 is false; use 4).
	rows := emptyRows(60, 20, map[domain.Cell]byte{cell(10, 10): 'h', cell(14, 10): 'a'})
	sc := raidScene(t, longRifle(), "a", rows...)
	s, e := raidAI(t, sc, 3, EnemyConfig{})
	orders := e.Orders()
	h := s.pawnIndex("h")
	if e.p[h].job != jobWait {
		t.Fatalf("a raider 4 cells from its target should wait: job %v orders %v", e.p[h].job, orders)
	}
	if d := e.p[h].end - e.p[h].start; d < shooterExpiryMin || d > shooterExpiryMax {
		t.Fatalf("Wait_Combat lasts %d ticks", d)
	}
	// The same raider, already behind cover against a target further away.
	rows = emptyRows(60, 20, map[domain.Cell]byte{cell(10, 10): 'h', cell(30, 10): 'a', cell(11, 10): 'S'})
	sc = raidScene(t, longRifle(), "a", rows...)
	s, e = raidAI(t, sc, 3, EnemyConfig{})
	e.Orders()
	if h = s.pawnIndex("h"); e.p[h].job != jobWait {
		t.Fatalf("a raider in cover that can shoot waits: job %v", e.p[h].job)
	}
}

func TestRaiderMovesToCoverWhenExposed(t *testing.T) {
	rows := emptyRows(60, 20, map[domain.Cell]byte{cell(10, 10): 'h', cell(30, 10): 'a', cell(21, 10): 'S'})
	sc := raidScene(t, longRifle(), "a", rows...)
	s, e := raidAI(t, sc, 3, EnemyConfig{})
	orders := e.Orders()
	h := s.pawnIndex("h")
	o, ok := orderFor(orders, "h")
	if !ok || o.Kind != OrderMove || e.p[h].job != jobGoto {
		t.Fatalf("an exposed raider walks to a better cell: job %v orders %v", e.p[h].job, orders)
	}
	if got := s.CoverBlockChance(o.To, cell(30, 10), true); got <= 0 {
		t.Errorf("destination %v has no cover against the target", o.To)
	}
	if !s.cellCanHit(&s.pawns[h].weapon, o.To, cell(30, 10)) {
		t.Errorf("destination %v cannot hit the target", o.To)
	}
	if e.p[h].goal != o.To {
		t.Errorf("goal %v != order %v", e.p[h].goal, o.To)
	}
}

func TestCastPositionIsDeterministicAndOnTheCastersSideFirst(t *testing.T) {
	glyphs := map[domain.Cell]byte{cell(10, 10): 'h', cell(30, 10): 'a'}
	sc := raidScene(t, longRifle(), "a", emptyRows(60, 20, glyphs)...)
	s, e := raidAI(t, sc, 3, EnemyConfig{})
	h, a := s.pawnIndex("h"), s.pawnIndex("a")
	d1, ok1 := e.castPosition(h, a)
	d2, ok2 := e.castPosition(h, a)
	if !ok1 || !ok2 || d1 != d2 {
		t.Fatalf("cast positions %v/%v %v/%v", d1, ok1, d2, ok2)
	}
	if d1.X > 30 {
		t.Errorf("an open field gives a cell on the caster's side of the target: %v", d1)
	}
	// With the caster in the target's row the perpendicular side test is
	// infinite, so everything counts as one side; the answer still exists.
	if float64(distSq(d1, cell(30, 10))) > 60*60 {
		t.Errorf("%v is out of range", d1)
	}
}

func TestRaiderWithNoTargetWalksToTheNearestHostile(t *testing.T) {
	// Out of the 65-cell acquire radius: no target, so it goes to the nearest.
	rows := emptyRows(120, 20, map[domain.Cell]byte{cell(5, 10): 'h', cell(100, 10): 'a', cell(90, 3): 'b'})
	sc := raidScene(t, longRifle(), "ab", rows...)
	s, e := raidAI(t, sc, 3, EnemyConfig{})
	orders := e.Orders()
	h := s.pawnIndex("h")
	o, ok := orderFor(orders, "h")
	if !ok || e.p[h].job != jobGotoNearest || o.To != s.pawns[s.pawnIndex("b")].pos {
		t.Fatalf("job %v orders %v", e.p[h].job, orders)
	}
	if e.p[h].enemy != -1 {
		t.Fatal("no enemy target outside the acquire radius")
	}
	for _, c := range []struct{ manhattan, want int }{{0, 30}, {50, 120}, {100, 600}, {75, 360}, {200, 600}} {
		if got := distanceExpiry(c.manhattan); got != c.want {
			t.Errorf("distanceExpiry(%d) = %d, want %d", c.manhattan, got, c.want)
		}
	}
}

func TestWaitingRaiderAutoAttacksAndDropsTheTargetAfterOneBurst(t *testing.T) {
	rows := emptyRows(60, 20, map[domain.Cell]byte{cell(10, 10): 'h', cell(14, 10): 'a'})
	sc := raidScene(t, longRifle(), "a", rows...)
	s, e := raidAI(t, sc, 3, EnemyConfig{})
	h := s.pawnIndex("h")
	var attacks int
	for range 400 {
		orders := e.Orders()
		if o, ok := orderFor(orders, "h"); ok && o.Kind == OrderAttack {
			if !o.Once || o.Target != "a" {
				t.Fatalf("auto-attack order = %+v", o)
			}
			attacks++
		}
		if _, _, err := s.Step(orders); err != nil {
			t.Fatal(err)
		}
		if s.pawns[h].stance == stanceCooldown && !s.pawns[h].once && s.pawns[h].target >= 0 {
			t.Fatal("a one-burst attack kept its target")
		}
	}
	if attacks < 2 {
		t.Fatalf("expected repeated auto-attacks, got %d", attacks)
	}
}

func TestFleeTriggers(t *testing.T) {
	rows := emptyRows(60, 20, map[domain.Cell]byte{cell(10, 10): 'h', cell(30, 10): 'a', cell(1, 1): '.'})
	sc := raidScene(t, longRifle(), "a", rows...)
	cfg := EnemyConfig{CanTimeoutOrFlee: true, GiveUpMinTicks: testGiveUpMin, GiveUpMaxTicks: testGiveUpMax, Exit: domain.Known(cell(55, 18)), ColonyHealthTotal: domain.Known(10000.0)}
	s, e := raidAI(t, sc, 9, cfg)
	a := s.pawnIndex("a")
	if e.giveUpAt < testGiveUpMin || e.giveUpAt > testGiveUpMax || e.fraction < fleeFractionMin || e.fraction >= fleeFractionMax {
		t.Fatalf("lord draws: give up %d fraction %v", e.giveUpAt, e.fraction)
	}
	threshold := math.Max(10000*e.fraction, fleeMinDamage)
	s.pawns[a].damage = threshold // not above
	e.Orders()
	if e.Fleeing() {
		t.Fatal("damage equal to the threshold must not trip it")
	}
	s.pawns[a].damage = threshold + 0.001
	orders := e.Orders()
	if !e.Fleeing() {
		t.Fatal("damage above the threshold trips it")
	}
	if o, ok := orderFor(orders, "h"); !ok || o.Kind != OrderMove || o.To != cell(55, 18) {
		t.Fatalf("a fleeing raider walks to the exit: %v", orders)
	}

	// The 900 floor holds for a small colony.
	cfg.ColonyHealthTotal = domain.Known(1000.0)
	s, e = raidAI(t, sc, 9, cfg)
	s.pawns[s.pawnIndex("a")].damage = 899
	e.Orders()
	if e.Fleeing() {
		t.Fatal("below 900 damage no one flees")
	}
	s.pawns[s.pawnIndex("a")].damage = 901
	e.Orders()
	if !e.Fleeing() {
		t.Fatal("above the floor the raid flees")
	}

	// The timeout.
	s, e = raidAI(t, sc, 9, cfg)
	s.tick = e.giveUpAt - 1
	e.Orders()
	if e.Fleeing() {
		t.Fatal("the raid gives up only at its time")
	}
	s.tick = e.giveUpAt
	e.Orders()
	if !e.Fleeing() {
		t.Fatal("the raid gives up at its time")
	}
}

// Lord.SetJob gives a faction lord with autoFlee a LordToil_PanicFlee
// transition on Trigger_FractionPawnsLost: with four raiders and 0.5625 the
// third incapped or killed raider (2.25 rounds up) sends the one left running
// for the exit, and a downed raider that later dies is not lost twice.
func TestAutoFleeOnFractionLost(t *testing.T) {
	rows := emptyRows(60, 20, map[domain.Cell]byte{cell(10, 10): 'h', cell(10, 12): 'i', cell(10, 14): 'j', cell(10, 16): 'k', cell(30, 10): 'a'})
	sc := raidScene(t, longRifle(), "a", rows...)
	cfg := EnemyConfig{Exit: domain.Known(cell(55, 18)), AutoFleeFraction: domain.Known(0.5625)}
	s, e := raidAI(t, sc, 3, cfg)
	h, i, j, k := s.pawnIndex("h"), s.pawnIndex("i"), s.pawnIndex("j"), s.pawnIndex("k")
	s.pawns[h].downed = true
	e.Observe()
	s.pawns[h].dead, s.pawns[i].downed = true, true // the first dies after going down
	e.Observe()
	if s.pawns[j].panic || s.pawns[k].panic {
		t.Fatal("two of four lost is below 2.25")
	}
	s.pawns[j].dead = true
	e.Observe()
	if !s.pawns[k].panic || s.pawns[h].panic || s.pawns[i].panic || s.pawns[j].panic {
		t.Fatal("the third loss panics exactly the raider still standing")
	}
	orders := e.Orders()
	if o, ok := orderFor(orders, "k"); !ok || o.Kind != OrderMove || o.To != cell(55, 18) {
		t.Fatalf("a panicked raider runs for the exit: %v", orders)
	}
	if _, resolved := s.Resolved(); resolved {
		t.Fatal("a running raider is still on the map and in the fight")
	}
	// Without a stated fraction the lord never panics.
	s, e = raidAI(t, sc, 3, EnemyConfig{})
	s.pawns[s.pawnIndex("h")].dead, s.pawns[s.pawnIndex("i")].dead, s.pawns[s.pawnIndex("j")].dead = true, true, true
	e.Observe()
	if s.pawns[s.pawnIndex("k")].panic {
		t.Fatal("an unknown fraction means no panic")
	}
}

func TestFleeConfigIsValidated(t *testing.T) {
	sc := raidScene(t, longRifle(), "a", emptyRows(20, 10, map[domain.Cell]byte{cell(5, 5): 'h', cell(9, 5): 'a'})...)
	s := newSim(t, sc, 1)
	for name, cfg := range map[string]EnemyConfig{
		"no exit":        {CanTimeoutOrFlee: true, GiveUpMinTicks: testGiveUpMin, GiveUpMaxTicks: testGiveUpMax, ColonyHealthTotal: domain.Known(1.0)},
		"exit off map":   {CanTimeoutOrFlee: true, GiveUpMinTicks: testGiveUpMin, GiveUpMaxTicks: testGiveUpMax, Exit: domain.Known(cell(99, 1)), ColonyHealthTotal: domain.Known(1.0)},
		"no health":      {CanTimeoutOrFlee: true, GiveUpMinTicks: testGiveUpMin, GiveUpMaxTicks: testGiveUpMax, Exit: domain.Known(cell(1, 1))},
		"health nonzero": {CanTimeoutOrFlee: true, GiveUpMinTicks: testGiveUpMin, GiveUpMaxTicks: testGiveUpMax, Exit: domain.Known(cell(1, 1)), ColonyHealthTotal: domain.Known(0.0)},
	} {
		if _, err := NewEnemyAI(s, cfg); err == nil {
			t.Errorf("%s: want an unsupported error", name)
		}
	}
	if _, err := NewEnemyAI(s, EnemyConfig{}); err != nil {
		t.Errorf("a raid that never flees needs nothing: %v", err)
	}
}

func TestFleeingRaiderLeavesTheMap(t *testing.T) {
	rows := emptyRows(30, 10, map[domain.Cell]byte{cell(5, 5): 'h', cell(25, 5): 'a'})
	sc := raidScene(t, longRifle(), "a", rows...)
	sc.StepTicks = 5
	cfg := EnemyConfig{CanTimeoutOrFlee: true, GiveUpMinTicks: testGiveUpMin, GiveUpMaxTicks: testGiveUpMax, Exit: domain.Known(cell(8, 5)), ColonyHealthTotal: domain.Known(1000.0)}
	s, e := raidAI(t, sc, 9, cfg)
	e.giveUpAt = 0
	var left []Event
	for range 400 {
		_, ev, err := s.Step(e.Orders())
		if err != nil {
			t.Fatal(err)
		}
		left = append(left, ofKind(ev, EventLeftMap)...)
		if len(left) > 0 {
			break
		}
	}
	if len(left) != 1 || left[0].Pawn != "h" || left[0].Cell != cell(8, 5) {
		t.Fatalf("left events = %+v", left)
	}
	h := s.pawnIndex("h")
	if s.pawns[h].has || !s.pawns[h].left {
		t.Fatal("the pawn left the combat")
	}
	if w, ok := s.Resolved(); !ok || w != combatmetrics.Colonist {
		t.Fatalf("a field with no raiders left is the colonists': %q %v", w, ok)
	}
	if m := s.Metrics("x"); m.EnemyDeaths != 0 || m.EnemyDowns != 0 || m.EnemyFled != 1 {
		t.Fatalf("a fled raider was neither killed nor downed: %+v", m)
	}
	if s.cells.head[s.g.idx(cell(8, 5))] != 0 {
		t.Fatal("the exit cell is free again")
	}
}

// Changing what the enemy decides must not shift the physical stream: the
// same scripted duel rolls the same hits whatever the enemy stream did.
func TestEnemyChoicesDoNotShiftHitRolls(t *testing.T) {
	rows := emptyRows(40, 10, map[domain.Cell]byte{cell(5, 5): 'h', cell(25, 5): 'a'})
	run := func(spin int) []Event {
		sc := raidScene(t, testRifle, "a", rows...)
		for i := range sc.Pawns {
			sc.Pawns[i].Combat.DamageToDown, sc.Pawns[i].Combat.DamageToKill = domain.Known(1e9), domain.Known(1e9)
		}
		s := newSim(t, sc, 77)
		for range spin {
			s.Streams().Enemy.Uint64()
		}
		_, ev := runUntil(t, s, []Order{attack("a", "h"), attack("h", "a")}, 1500, nil)
		return ofKind(ev, EventHit)
	}
	base, spun := run(0), run(5000)
	if len(base) == 0 || !slices.Equal(base, spun) {
		t.Fatalf("hits differ: %d vs %d", len(base), len(spun))
	}
}

// laneRaid is the hand-built closed-loop scene: four colonists behind a
// sandbag line at the west, four raiders at the east of an 80-cell map.
func laneRaid(t testing.TB, hostileDamage int, toughness float64) Fight {
	t.Helper()
	glyphs := map[domain.Cell]byte{}
	for z := int32(1); z < 11; z += 2 {
		glyphs[cell(8, z)] = 'S'
		glyphs[cell(48, z)] = 'S'
	}
	sc := duel(t, testRifle, nil, emptyRows(80, 12, glyphs)...)
	for i := range 4 {
		for _, side := range []string{combatmetrics.Colonist, combatmetrics.Hostile} {
			x, prefix := int32(6), "c"
			w := testRifle
			if side == combatmetrics.Hostile {
				x, prefix = 74, "h"
				w.DamageAmount = hostileDamage
			}
			spec := testCombat(&w)
			if side == combatmetrics.Colonist {
				spec.DamageToDown, spec.DamageToKill = domain.Known(1e9), domain.Known(1e9)
			} else {
				spec.DamageToDown, spec.DamageToKill = domain.Known(toughness), domain.Known(toughness*2)
			}
			sc.Pawns = append(sc.Pawns, PawnSpec{
				ID: domain.PawnID(fmt.Sprintf("%s%d", prefix, i)), Pos: cell(x, int32(1+i*3)),
				MoveSpeed: domain.Known(4.6), Side: side, Combat: spec,
			})
		}
	}
	sc.StepTicks = 6
	return Fight{
		Name: "hand-built-lane", Scenario: sc, Facts: factsFor(sc), MaxTicks: 20000, BackstopTicks: 120,
		EnemyAI: &EnemyConfig{CanTimeoutOrFlee: true, GiveUpMinTicks: testGiveUpMin, GiveUpMaxTicks: testGiveUpMax, Exit: domain.Known(cell(79, 0)), ColonyHealthTotal: domain.Known(1000.0)},
	}
}

// Closed loop: raiders advance across 68 cells, stop where they can shoot
// (some behind the sandbag line), and leave once the colony has taken 900
// damage. The colonists are unkillable and the raiders far tougher than their
// guns, so the only way out is the flee trigger.
func TestClosedLoopRaidersAdvanceTakeCoverAndFlee(t *testing.T) {
	for _, seed := range []uint64{1, 2, 3} {
		f := laneRaid(t, 60, 5000)
		var minDist = int32(1 << 30)
		covered := false
		probe := newSim(t, f.Scenario, seed)
		f.Enemy = func(st State) []Order {
			for _, p := range st.Pawns {
				if !strings.HasPrefix(string(p.ID), "h") || p.Dead {
					continue
				}
				minDist = min(minDist, p.Pos.X-6)
				if p.Pos.X < 74 && probe.CoverBlockChance(p.Pos, cell(6, p.Pos.Z), true) > 0 {
					covered = true
				}
			}
			return nil
		}
		res, err := RunFight(f, seed, policy.DefaultCombatTuning())
		if err != nil {
			t.Fatal(err)
		}
		if res.Left != 4 || !res.Resolved || res.Winner != combatmetrics.Colonist {
			t.Fatalf("seed %d: the raid should flee the field: %+v", seed, res)
		}
		if res.Ticks >= f.MaxTicks {
			t.Fatalf("seed %d: ran to the budget (a stall): %d ticks", seed, res.Ticks)
		}
		if minDist > 40 {
			t.Errorf("seed %d: raiders never advanced: closest %d cells", seed, minDist)
		}
		if !covered {
			t.Errorf("seed %d: no raider ever stood in cover", seed)
		}
		if res.Metrics.ColonistDowns != 0 || res.Metrics.EnemyDeaths != 0 || res.Metrics.EnemyFled != 4 {
			t.Errorf("seed %d: unexpected casualties: %+v", seed, res.Metrics)
		}
	}
}

// A fight where both sides can die also ends and is repeatable.
func TestClosedLoopIsDeterministicAndResolves(t *testing.T) {
	f := laneRaid(t, 10, 25)
	for i := range f.Scenario.Pawns {
		if p := &f.Scenario.Pawns[i]; p.Side == combatmetrics.Colonist {
			p.Combat.DamageToDown, p.Combat.DamageToKill = domain.Known(25.0), domain.Known(45.0)
		}
	}
	f.EnemyAI.CanTimeoutOrFlee = false
	a, err := RunFight(f, 5, policy.DefaultCombatTuning())
	if err != nil {
		t.Fatal(err)
	}
	b, err := RunFight(f, 5, policy.DefaultCombatTuning())
	if err != nil {
		t.Fatal(err)
	}
	if a.Ticks != b.Ticks || a.Winner != b.Winner || a.Metrics.EnemyDeaths != b.Metrics.EnemyDeaths {
		t.Fatalf("same seed, different fights: %+v vs %+v", a, b)
	}
	if !a.Resolved || a.Ticks >= f.MaxTicks {
		t.Fatalf("the fight stalled: %+v", a)
	}
}

// The Core AssaultColony give-up range, as a caller would read it from the game data.
const testGiveUpMin, testGiveUpMax = 26000, 38000
