package combatsim

import (
	"errors"
	"fmt"
	"math"
	"reflect"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// testRifle is a hand-built weapon for the formulas: all values here are test
// inputs, shown so the expected numbers can be checked by hand.
var testRifle = WeaponDef{
	Name: "test rifle", Range: 30, MinRange: 1.421,
	WarmupSeconds: 1, CooldownSeconds: 1.5,
	BurstShotCount: 3, TicksBetweenBurstShots: 5,
	AccuracyTouch: 0.9, AccuracyShort: 0.8, AccuracyMedium: 0.7, AccuracyLong: 0.6,
	ProjectileSpeed: 0.55, DamageAmount: 10,
}

// rapid fires one shot per tick or two with no warmup, for statistics.
func rapid() WeaponDef {
	w := testRifle
	w.WarmupSeconds, w.CooldownSeconds, w.BurstShotCount, w.TicksBetweenBurstShots = 0, 0, 1, 0
	return w
}

func testShooter() ShooterStats {
	k := domain.Known[float64]
	return ShooterStats{
		ShootingAccuracyPawn: k(0.95),
		AccuracyTouch:        k(1), AccuracyShort: k(0.9), AccuracyMedium: k(0.8), AccuracyLong: k(0.7),
		AimingDelayFactor: k(1), RangedCooldownFactor: k(1),
	}
}

func testCombat(w *WeaponDef) *CombatSpec {
	return &CombatSpec{
		BodySize:            domain.Known(1.0),
		CanBenefitFromCover: true,
		DamageToDown:        domain.Known(25.0),
		DamageToKill:        domain.Known(45.0),
		Armor:               domain.Known[[]float64](nil),
		Weapon:              w,
		Shooter:             testShooter(),
	}
}

// duel builds rows with pawn a (colonist) and b (hostile), each armed with w
// and edited by mod(id, spec).
func duel(t testing.TB, w WeaponDef, mod func(id domain.PawnID, c *CombatSpec), rows ...string) Scenario {
	t.Helper()
	sc := scenario(t, 4.6, rows...)
	sc.WeatherAccuracy = domain.Known(1.0)
	sc.FriendlyFireChanceFactor = domain.Known(1.0)
	for i := range sc.Pawns {
		p := &sc.Pawns[i]
		p.Side = combatmetrics.Colonist
		if p.ID != "a" {
			p.Side = combatmetrics.Hostile
		}
		w := w
		p.Combat = testCombat(&w)
		if mod != nil {
			mod(p.ID, p.Combat)
		}
	}
	return sc
}

func immortal(_ domain.PawnID, c *CombatSpec) {
	c.DamageToDown, c.DamageToKill = domain.Known(1e9), domain.Known(1e9)
}

func attack(from, to domain.PawnID) Order { return Order{Pawn: from, Kind: OrderAttack, Target: to} }

// runUntil steps until stop reports true or max ticks pass, collecting every event.
func runUntil(t testing.TB, s *Sim, orders []Order, max int, stop func(State) bool) (State, []Event) {
	t.Helper()
	var all []Event
	var st State
	for s.Tick() < max {
		var ev []Event
		var err error
		st, ev, err = s.Step(orders)
		if err != nil {
			t.Fatal(err)
		}
		orders = nil
		all = append(all, ev...)
		if stop != nil && stop(st) {
			break
		}
	}
	return st, all
}

func ofKind(ev []Event, k EventKind) []Event {
	var out []Event
	for _, e := range ev {
		if e.Kind == k {
			out = append(out, e)
		}
	}
	return out
}

// sigma is the half-width, in standard deviations, of every statistical bound.
const sigma = 5

func within(t *testing.T, name string, count, n int, p float64) {
	t.Helper()
	got := float64(count) / float64(n)
	if tol := sigma * math.Sqrt(p*(1-p)/float64(n)); math.Abs(got-p) > tol {
		t.Errorf("%s: %d/%d = %.4f, want %.4f +/- %.4f", name, count, n, got, p, tol)
	}
}

func TestShotReportFormula(t *testing.T) {
	// Shooter accuracy 0.95; stat factors touch/short/medium/long 1/0.9/0.8/0.7;
	// weapon accuracy .9/.8/.7/.6. The expected figures are accuracy^d times the
	// banded stat factor times the banded weapon accuracy:
	//   d=2  (touch)          .95^2  *1                 *.9
	//   d=5  (3..12, t=2/9)   .95^5  *(1-.1*2/9)        *(.9-.1*2/9)
	//   d=8  (3..12, t=5/9)   .95^8  *(1-.1*5/9)        *(.9-.1*5/9)
	//   d=15 (12..25, t=3/13) .95^15 *(.9-.1*3/13)      *(.8-.1*3/13)
	//   d=30 (25..40, t=1/3)  .95^30 *(.8-.1/3)         *(.7-.1/3)
	cases := []struct {
		dist                    int
		shooter, equipment, aim float64
	}{
		{2, 0.902500, 0.900000, 0.812250},
		{5, 0.756586, 0.877778, 0.664114},
		{8, 0.626564, 0.844444, 0.529098},
		{15, 0.406271, 0.776923, 0.315641},
		{30, 0.164556, 0.666667, 0.109704},
	}
	for _, c := range cases {
		s := newSim(t, duel(t, testRifle, nil, "a"+strings.Repeat(".", c.dist-1)+"b"), 1)
		r, err := s.ShotReport("a", "b")
		if err != nil {
			t.Fatal(err)
		}
		if r.Distance != float64(c.dist) {
			t.Errorf("d=%d: distance %v", c.dist, r.Distance)
		}
		for _, f := range []struct {
			name      string
			got, want float64
		}{
			{"shooter factor", r.FromShooter, c.shooter},
			{"equipment factor", r.FromEquipment, c.equipment},
			{"aim standard", r.AimStandard, c.aim},
			{"aim (standing, size 1)", r.Aim, c.aim},
			{"total with no cover", r.Total, c.aim},
		} {
			if math.Abs(f.got-f.want) > 1e-5 {
				t.Errorf("d=%d %s = %.6f, want %.6f", c.dist, f.name, f.got, f.want)
			}
		}
	}
}

func TestShotReportModifiers(t *testing.T) {
	// d=2 base aim .81225 (see TestShotReportFormula).
	const base = 0.81225
	build := func(rows ...string) *Sim {
		return newSim(t, duel(t, testRifle, nil, rows...), 1)
	}
	t.Run("cover blocks .55 of shots", func(t *testing.T) {
		// A sandbag (fill .55) cardinally next to the target on the firing
		// line, 5 cells from the shooter: block chance .55, no distance cut.
		r, _ := build("a....Sb").ShotReport("a", "b")
		if math.Abs(r.CoverBlock-0.55) > 1e-9 || math.Abs(r.Total-r.Aim*0.45) > 1e-9 {
			t.Errorf("cover block %v total %v", r.CoverBlock, r.Total)
		}
	})
	t.Run("target size scales aim, not the standard", func(t *testing.T) {
		sc := duel(t, testRifle, func(id domain.PawnID, c *CombatSpec) { c.BodySize = domain.Known(0.25) }, "a.b")
		r, _ := newSim(t, sc, 1).ShotReport("a", "b")
		if math.Abs(r.AimStandard-base) > 1e-5 || math.Abs(r.AimIgnoringPosture-base*0.5) > 1e-5 {
			t.Errorf("standard %v ignoring posture %v (size clamps to .5)", r.AimStandard, r.AimIgnoringPosture)
		}
	})
	t.Run("weather applies unless both roofed", func(t *testing.T) {
		sc := duel(t, testRifle, nil, "a.b")
		sc.WeatherAccuracy = domain.Known(0.8)
		r, _ := newSim(t, sc, 1).ShotReport("a", "b")
		if math.Abs(r.FromWeather-0.8) > 1e-9 || math.Abs(r.AimStandard-base*0.8) > 1e-5 {
			t.Errorf("weather factor %v aim %v", r.FromWeather, r.AimStandard)
		}
		sc = duel(t, testRifle, nil, "arb")
		sc.WeatherAccuracy = domain.Known(0.8)
		sc.Roofed[0], sc.Roofed[2] = true, true
		r, _ = newSim(t, sc, 1).ShotReport("a", "b")
		if r.FromWeather != 1 {
			t.Errorf("both roofed: weather factor %v, want 1", r.FromWeather)
		}
	})
	t.Run("accuracy floor", func(t *testing.T) {
		sc := duel(t, testRifle, func(id domain.PawnID, c *CombatSpec) { c.Shooter.ShootingAccuracyPawn = domain.Known(0.01) }, "a....b")
		r, _ := newSim(t, sc, 1).ShotReport("a", "b")
		if r.FromShooter != 0.0201 || r.AimStandard != 0.0201 {
			t.Errorf("floors: shooter %v standard %v", r.FromShooter, r.AimStandard)
		}
	})
}

func TestTimeToFirstShot(t *testing.T) {
	// Warmup 1 s x AimingDelayFactor 1.5 = 90 ticks. The order lands at tick 0,
	// the job starts the verb on tick 1, the stance expires 90 ticks later: tick
	// 91. The second burst shot comes TicksBetweenBurstShots-1 = 4 ticks on (the
	// equipment's VerbTick runs the tick the burst starts), then every 5. The
	// cooldown (1.5 s x 1 = 90 ticks) starts after the third shot, and the next
	// warmup starts the tick it ends.
	sc := duel(t, testRifle, func(id domain.PawnID, c *CombatSpec) {
		c.Shooter.AimingDelayFactor = domain.Known(1.5)
		immortal(id, c)
	}, "a.......b")
	s := newSim(t, sc, 3)
	_, ev := runUntil(t, s, []Order{attack("a", "b")}, 300, nil)
	var ticks []int
	for _, e := range ofKind(ev, EventShot) {
		if e.Pawn == "a" {
			ticks = append(ticks, e.Tick)
		}
	}
	want := []int{91, 95, 100, 280, 284, 289}
	if len(ticks) < len(want) || !reflect.DeepEqual(ticks[:len(want)], want) {
		t.Errorf("shot ticks %v, want prefix %v", ticks, want)
	}
}

func TestZeroWarmupShootsOnTheFirstTick(t *testing.T) {
	w := rapid()
	s := newSim(t, duel(t, w, immortal, "a....b"), 3)
	_, ev := runUntil(t, s, []Order{attack("a", "b")}, 3, nil)
	if shots := ofKind(ev, EventShot); len(shots) == 0 || shots[0].Tick != 1 {
		t.Errorf("shots %+v, want the first on tick 1", shots)
	}
}

func TestHitProbabilityMatchesReport(t *testing.T) {
	cases := []struct {
		name string
		rows string
	}{
		{"open ground d=2", "a.b"},
		{"open ground d=8", "a......b"},
		{"behind cover d=6", "a....Sb"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			s := newSim(t, duel(t, rapid(), immortal, c.rows), 11)
			r, _ := s.ShotReport("a", "b")
			_, ev := runUntil(t, s, []Order{attack("a", "b")}, 12000, nil)
			shots, hits := 0, 0
			for _, e := range ev {
				switch {
				case e.Kind == EventShot && e.Pawn == "a":
					shots++
				case e.Kind == EventHit && e.Pawn == "a" && e.Target == "b":
					hits++
				}
			}
			if shots < 3000 {
				t.Fatalf("only %d shots", shots)
			}
			// Nothing stands between a and b, so every landed shot is Total.
			within(t, "hit rate", hits, shots, r.Total)
		})
	}
}

func TestArmorDamageDistribution(t *testing.T) {
	// Damage 10. A rating of .5 against penetration p leaves eff = .5-p: the
	// damage is stopped below eff/2 of the roll, halved (rounded at random, here
	// exactly 5) below eff, and full above.
	cases := []struct {
		name   string
		armor  []float64
		pen    float64
		stop   float64
		halved float64
	}{
		{"unarmored", nil, 0, 0, 0},
		{"rating .5", []float64{0.5}, 0, 0.25, 0.25},
		{"rating .5 pen .3", []float64{0.5}, 0.3, 0.10, 0.10},
		{"rating .5 pen .6", []float64{0.5}, 0.6, 0, 0},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			w := rapid()
			w.ArmorPenetration = c.pen
			sc := duel(t, w, func(id domain.PawnID, cs *CombatSpec) {
				immortal(id, cs)
				cs.Armor = domain.Known(c.armor)
			}, "a.b")
			s := newSim(t, sc, 5)
			_, ev := runUntil(t, s, []Order{attack("a", "b")}, 12000, nil)
			var n, zero, half, full int
			for _, e := range ofKind(ev, EventHit) {
				if e.Pawn != "a" {
					continue
				}
				n++
				switch e.Amount {
				case 0:
					zero++
				case 5:
					half++
				case 10:
					full++
				default:
					t.Fatalf("damage %v is not 0, 5 or 10", e.Amount)
				}
			}
			if n < 3000 {
				t.Fatalf("only %d hits", n)
			}
			within(t, "stopped", zero, n, c.stop)
			within(t, "halved", half, n, c.halved)
			within(t, "full", full, n, 1-c.stop-c.halved)
		})
	}
}

func TestLayeredArmorAppliesInOrder(t *testing.T) {
	// Two layers of .5: the first stops 25%, halves 25%; each survivor meets the
	// second the same way. Full damage is .5*.5 = 25% of hits.
	sc := duel(t, rapid(), func(id domain.PawnID, cs *CombatSpec) {
		immortal(id, cs)
		cs.Armor = domain.Known([]float64{0.5, 0.5})
	}, "a.b")
	_, ev := runUntil(t, newSim(t, sc, 8), []Order{attack("a", "b")}, 12000, nil)
	n, full := 0, 0
	for _, e := range ofKind(ev, EventHit) {
		n++
		if e.Amount == 10 {
			full++
		}
	}
	within(t, "full through two layers", full, n, 0.25)
}

func TestInjuryThresholds(t *testing.T) {
	// 10 damage per hit, unarmored: down at >=25 (third hit), dead at >=45 (fifth).
	sc := duel(t, rapid(), func(id domain.PawnID, c *CombatSpec) {
		if id == "b" {
			return
		}
		immortal(id, c)
	}, "a.b")
	// a is immortal and b (hostile, same weapon) shoots back; watch b fall first.
	sc.Pawns[1].Combat.Weapon = nil
	s := newSim(t, sc, 21)
	_, ev := runUntil(t, s, []Order{attack("a", "b")}, 400, nil)
	// Shots already in flight still land on a downed pawn, so the thresholds
	// are read off the running total at each event.
	total, downedAt, diedAt := 0.0, 0.0, 0.0
	for _, e := range ev {
		switch e.Kind {
		case EventHit:
			total += e.Amount
		case EventDowned:
			downedAt = total
		case EventDied:
			diedAt = total
		}
	}
	if downedAt != 30 {
		t.Errorf("downed at %v damage, want 30 (third hit)", downedAt)
	}
	if diedAt != 50 {
		t.Errorf("died at %v damage, want 50 (first multiple of 10 at or above 45)", diedAt)
	}
	if got := ofKind(ev, EventAttackEnded); len(got) != 1 || got[0].Pawn != "a" || got[0].Target != "b" {
		t.Errorf("attack-ended events %+v", got)
	}

	// Straight to death: one hit of 50 kills (kill threshold 45).
	w := rapid()
	w.DamageAmount = 50
	sc = duel(t, w, func(id domain.PawnID, c *CombatSpec) {
		if id == "a" {
			immortal(id, c)
		} else {
			c.Weapon = nil
		}
	}, "a.b")
	st, ev := runUntil(t, newSim(t, sc, 22), []Order{attack("a", "b")}, 500, func(st State) bool { return st.Pawns[1].Dead })
	if !st.Pawns[1].Dead || !st.Pawns[1].Downed || len(ofKind(ev, EventDied)) != 1 {
		t.Errorf("b should be dead: %+v", st.Pawns[1])
	}
}

func TestDeadPawnFreesItsCell(t *testing.T) {
	w := rapid()
	w.DamageAmount = 50
	sc := duel(t, w, func(id domain.PawnID, c *CombatSpec) {
		if id == "a" {
			immortal(id, c)
		} else {
			c.Weapon = nil
		}
	}, "a.b.")
	s := newSim(t, sc, 22)
	runUntil(t, s, []Order{attack("a", "b")}, 500, func(st State) bool { return st.Pawns[1].Dead })
	if _, ev := runUntil(t, s, []Order{{Pawn: "a", To: cell(2, 0)}}, s.Tick()+60, nil); len(ofKind(ev, EventArrived)) != 1 {
		t.Errorf("a could not walk onto the cell the dead pawn left: %+v", ev)
	}
}

func TestLedgerMatchesEvents(t *testing.T) {
	s := newSim(t, duel(t, rapid(), nil, "a.....b"), 4)
	_, ev := runUntil(t, s, []Order{attack("a", "b"), attack("b", "a")}, 3000, func(State) bool { _, ok := s.Resolved(); return ok })
	hits := ofKind(ev, EventHit)
	led := s.Damage()
	if len(hits) == 0 || len(hits) != len(led) {
		t.Fatalf("%d hit events, %d ledger rows", len(hits), len(led))
	}
	for i, h := range hits {
		l := led[i]
		if l.Tick != h.Tick || l.Instigator != string(h.Pawn) || l.Victim != string(h.Target) || l.Dealt != h.Amount {
			t.Errorf("row %d: ledger %+v, event %+v", i, l, h)
		}
	}
	last := led[len(led)-1]
	if !last.Downed {
		t.Errorf("the deciding hit should be marked downed: %+v", last)
	}
}

func TestWallBlocksLineOfFire(t *testing.T) {
	sc := duel(t, rapid(), immortal,
		"a.#.b",
		"..#..",
		"..#..",
	)
	s := newSim(t, sc, 1)
	_, ev := runUntil(t, s, []Order{attack("a", "b")}, 300, nil)
	if n := len(ofKind(ev, EventShot)); n != 0 {
		t.Errorf("%d shots through a wall", n)
	}
}

func TestOutOfRangeWaits(t *testing.T) {
	w := rapid()
	w.Range = 10
	s := newSim(t, duel(t, w, immortal, "a"+strings.Repeat(".", 14)+"b"), 1)
	_, ev := runUntil(t, s, []Order{attack("a", "b")}, 200, nil)
	if n := len(ofKind(ev, EventShot)); n != 0 {
		t.Errorf("%d shots beyond range", n)
	}
}

func TestInterceptionNeedsDistanceAndOrder(t *testing.T) {
	// c stands in the line of fire. Within 5 cells of the shooter it cannot be
	// hit; farther out it can, except when friendly fire is off.
	count := func(ffFactor float64, cSide string, rows string) (hitsOnC, hitsOnB int) {
		sc := duel(t, rapid(), immortal, rows)
		sc.FriendlyFireChanceFactor = domain.Known(ffFactor)
		for i := range sc.Pawns {
			if sc.Pawns[i].ID == "c" {
				sc.Pawns[i].Side = cSide
			}
		}
		_, ev := runUntil(t, newSim(t, sc, 9), []Order{attack("a", "b")}, 6000, nil)
		for _, e := range ofKind(ev, EventHit) {
			if e.Pawn != "a" {
				continue
			}
			if e.Target == "c" {
				hitsOnC++
			} else {
				hitsOnB++
			}
		}
		return
	}
	line := func(cAt int) string {
		r := []byte("a" + strings.Repeat(".", 19) + "b")
		r[cAt] = 'c'
		return string(r)
	}
	const colonist, hostile = combatmetrics.Colonist, combatmetrics.Hostile
	if c, b := count(1, hostile, line(3)); c != 0 || b == 0 {
		t.Errorf("a blocker 3 cells out was hit %d times (b %d)", c, b)
	}
	// An allied blocker is friendly fire, scaled by the difficulty factor; an
	// enemy blocker is not.
	if c, _ := count(0, colonist, line(12)); c != 0 {
		t.Errorf("friendly fire factor 0 still hit the ally %d times", c)
	}
	if c, _ := count(0, hostile, line(12)); c == 0 {
		t.Error("friendly fire factor 0 must not shield an enemy blocker")
	}
	cAll, _ := count(1, colonist, line(12))
	cHalf, _ := count(0.5, colonist, line(12))
	if cAll == 0 || cHalf == 0 || cHalf >= cAll {
		t.Errorf("friendly fire factor should scale ally hits: 1 -> %d, 0.5 -> %d", cAll, cHalf)
	}
	c, b := count(1, hostile, line(12))
	if _, bClear := count(1, hostile, "a"+strings.Repeat(".", 19)+"b"); b >= bClear || c == 0 {
		t.Errorf("a blocker should take hits off the target: %d with, %d without", b, bClear)
	}
	// Beyond 12 cells the nearer blocker shields the farther one.
	sc := duel(t, rapid(), immortal, "a"+strings.Repeat(".", 12)+"c"+strings.Repeat(".", 3)+"d"+strings.Repeat(".", 7)+"b")
	for i := range sc.Pawns {
		sc.Pawns[i].Side = combatmetrics.Hostile
	}
	sc.Pawns[0].Side = combatmetrics.Colonist
	_, ev := runUntil(t, newSim(t, sc, 9), []Order{attack("a", "b")}, 6000, nil)
	near, far := 0, 0
	for _, e := range ofKind(ev, EventHit) {
		switch e.Target {
		case "c":
			near++
		case "d":
			far++
		}
	}
	if near <= far || far == 0 {
		t.Errorf("first blocker takes more hits than the second: near %d far %d", near, far)
	}
}

func TestInterceptedShotLandsBeforeTheTarget(t *testing.T) {
	// One slow shot, tried over seeds until it strikes the blocker: the flight
	// to c at 12.5 cells (speed .55 cells/tick, so 23 ticks) is shorter than
	// the 45 ticks to b at 24.5.
	w := rapid()
	w.CooldownSeconds = 600
	for seed := uint64(1); seed < 200; seed++ {
		sc := duel(t, w, immortal, "a"+strings.Repeat(".", 11)+"c"+strings.Repeat(".", 11)+"b")
		sc.Pawns[1].Side = combatmetrics.Hostile
		_, ev := runUntil(t, newSim(t, sc, seed), []Order{attack("a", "b")}, 200, nil)
		shots, hits := ofKind(ev, EventShot), ofKind(ev, EventHit)
		if len(shots) != 1 || len(hits) == 0 || hits[0].Target != "c" {
			continue
		}
		if flight := hits[0].Tick - shots[0].Tick; flight < 1 || flight > 23 {
			t.Errorf("seed %d: shot at %d hit the blocker at %d", seed, shots[0].Tick, hits[0].Tick)
		}
		return
	}
	t.Fatal("no seed's shot hit the blocker")
}

func TestMoveOrderInterruptsWarmupAndBusyBlocksMovement(t *testing.T) {
	sc := duel(t, testRifle, immortal, "a....b", "......")
	s := newSim(t, sc, 1)
	// During warmup the pawn does not walk.
	st, _ := runUntil(t, s, []Order{attack("a", "b")}, 30, nil)
	if st.Pawns[0].Pos != cell(0, 0) {
		t.Fatal("setup: a should be aiming")
	}
	// A move order cancels the warmup, so a never fires and walks away.
	st, ev := runUntil(t, s, []Order{{Pawn: "a", To: cell(0, 1)}}, 200, nil)
	if n := len(ofKind(ev, EventShot)); n != 0 {
		t.Errorf("a fired %d shots after a move order cancelled the warmup", n)
	}
	if st.Pawns[0].Pos != cell(0, 1) {
		t.Errorf("a at %v, want it to have walked to (0,1)", st.Pawns[0].Pos)
	}
}

func TestSameSideAndUnarmedOrdersFail(t *testing.T) {
	sc := duel(t, testRifle, nil, "a.b.c")
	sc.Pawns[2].Side = combatmetrics.Colonist
	sc.Pawns[1].Combat.Weapon = nil
	s := newSim(t, sc, 1)
	for name, o := range map[string]Order{
		"ally":      attack("a", "c"),
		"unarmed":   attack("b", "a"),
		"unknown":   attack("a", "zzz"),
		"no combat": attack("a", "a"),
	} {
		if _, _, err := s.Step([]Order{o}); err == nil {
			t.Errorf("%s: want an error", name)
		}
	}
}

func TestUnsupportedCombatScenarios(t *testing.T) {
	cases := map[string]func(*Scenario){
		"unknown weather accuracy":   func(sc *Scenario) { sc.WeatherAccuracy = domain.Unknown[float64]() },
		"unknown friendly fire":      func(sc *Scenario) { sc.FriendlyFireChanceFactor = domain.Unknown[float64]() },
		"no side":                    func(sc *Scenario) { sc.Pawns[0].Side = "" },
		"unknown body size":          func(sc *Scenario) { sc.Pawns[0].Combat.BodySize = domain.Unknown[float64]() },
		"unknown armor":              func(sc *Scenario) { sc.Pawns[0].Combat.Armor = domain.Unknown[[]float64]() },
		"unknown shooting accuracy":  func(sc *Scenario) { sc.Pawns[0].Combat.Shooter.ShootingAccuracyPawn = domain.Unknown[float64]() },
		"unknown aiming delay":       func(sc *Scenario) { sc.Pawns[0].Combat.Shooter.AimingDelayFactor = domain.Unknown[float64]() },
		"unknown down threshold":     func(sc *Scenario) { sc.Pawns[0].Combat.DamageToDown = domain.Unknown[float64]() },
		"kill below down":            func(sc *Scenario) { sc.Pawns[0].Combat.DamageToKill = domain.Known(1.0) },
		"weapon with no projectile":  func(sc *Scenario) { sc.Pawns[0].Combat.Weapon.ProjectileSpeed = 0 },
		"weapon with no damage":      func(sc *Scenario) { sc.Pawns[0].Combat.Weapon.DamageAmount = 0 },
		"weapon with forced miss":    func(sc *Scenario) { sc.Pawns[0].Combat.Weapon.ForcedMissRadius = 9 },
		"burst without spacing":      func(sc *Scenario) { sc.Pawns[0].Combat.Weapon.TicksBetweenBurstShots = 0 },
		"weapon with unset accuracy": func(sc *Scenario) { sc.Pawns[0].Combat.Weapon.AccuracyLong = 0 },
	}
	for name, mod := range cases {
		t.Run(name, func(t *testing.T) {
			sc := duel(t, testRifle, nil, "a.b")
			mod(&sc)
			if _, err := (&Sim{}).Reset(sc, 1); !errors.Is(err, ErrUnsupported) {
				t.Errorf("Reset error = %v, want ErrUnsupported", err)
			}
		})
	}
}

// openField builds an open map with n colonists on the left edge facing n
// hostiles on the right, both armed with the test rifle.
func openField(t testing.TB, n, w, h int) Scenario {
	t.Helper()
	rows := make([]string, h)
	for i := range rows {
		rows[i] = strings.Repeat(".", w)
	}
	sc := duel(t, testRifle, nil, rows...)
	for i := range n {
		for _, side := range []string{combatmetrics.Colonist, combatmetrics.Hostile} {
			x := int32(4)
			prefix := "c"
			if side == combatmetrics.Hostile {
				x, prefix = int32(w-5), "h"
			}
			w := testRifle
			sc.Pawns = append(sc.Pawns, PawnSpec{
				ID: domain.PawnID(fmt.Sprintf("%s%d", prefix, i)), Pos: cell(x, int32(2+i*2)),
				MoveSpeed: domain.Known(4.6), Side: side, Combat: testCombat(&w),
			})
		}
	}
	sc.StepTicks = 6
	return sc
}

// nearestEnemy orders every standing pawn to shoot the nearest standing enemy.
func nearestEnemy(st State, side map[domain.PawnID]string, orders []Order) []Order {
	orders = orders[:0]
	for _, p := range st.Pawns {
		if p.Downed || p.Dead {
			continue
		}
		best, bd := domain.PawnID(""), int32(1<<30)
		for _, q := range st.Pawns {
			if q.Downed || q.Dead || side[q.ID] == side[p.ID] {
				continue
			}
			dx, dz := q.Pos.X-p.Pos.X, q.Pos.Z-p.Pos.Z
			if d := dx*dx + dz*dz; d < bd {
				best, bd = q.ID, d
			}
		}
		if best != "" {
			orders = append(orders, attack(p.ID, best))
		}
	}
	return orders
}

func sides(sc Scenario) map[domain.PawnID]string {
	m := map[domain.PawnID]string{}
	for _, p := range sc.Pawns {
		m[p.ID] = p.Side
	}
	return m
}

// fight runs a scenario to resolution or the horizon and returns its metrics.
func fight(t testing.TB, sc Scenario, seed uint64, horizon int) (*Sim, combatmetrics.Metrics, []Event) {
	t.Helper()
	s := newSim(t, sc, seed)
	side := sides(sc)
	var all, orders = []Event(nil), []Order(nil)
	st := State{}
	for s.Tick() < horizon {
		orders = nearestEnemy(st, side, orders)
		if s.Tick() == 0 {
			st, _, _ = s.Step(nil)
			continue
		}
		var ev []Event
		var err error
		st, ev, err = s.Step(orders)
		if err != nil {
			t.Fatal(err)
		}
		all = append(all, ev...)
		if _, ok := s.Resolved(); ok {
			break
		}
	}
	return s, s.Metrics("open-field"), all
}

func TestFourVFourResolvesToMetrics(t *testing.T) {
	sc := openField(t, 4, 36, 16)
	s, m, ev := fight(t, sc, 7, 6000)
	winner, ok := s.Resolved()
	if !ok || m.Winner != winner || m.Winner == "" {
		t.Fatalf("fight did not resolve: resolved=%v winner=%q metrics=%+v", ok, winner, m)
	}
	if m.Version != combatmetrics.Version || m.FirstContactTick <= 0 || m.ResolvedTick < m.FirstContactTick || m.ContactToResolution != m.ResolvedTick-m.FirstContactTick {
		t.Errorf("timing fields: %+v", m)
	}
	dead := 0
	for _, p := range s.state.Pawns {
		if p.Dead {
			dead++
		}
	}
	if got := m.ColonistDeaths + m.EnemyDeaths; got != dead {
		t.Errorf("metrics count %d deaths, state has %d", got, dead)
	}
	var wantColonistDamage float64
	for _, h := range s.Damage() {
		if h.VictimSide == combatmetrics.Colonist {
			wantColonistDamage += h.Dealt
		}
	}
	if math.Abs(m.ColonistDamage-wantColonistDamage) > 0.05 {
		t.Errorf("colonist damage %v, ledger %v", m.ColonistDamage, wantColonistDamage)
	}
	if len(ofKind(ev, EventShot)) == 0 || len(ofKind(ev, EventHit)) != len(s.Damage()) {
		t.Errorf("events and ledger disagree")
	}
	downs := m.ColonistDowns + m.ColonistDeaths + m.EnemyDowns + m.EnemyDeaths
	if downs == 0 {
		t.Error("a resolved fight must down someone")
	}
}

func TestFightDeterminismAndSeedSensitivity(t *testing.T) {
	sc := openField(t, 4, 36, 16)
	_, m1, e1 := fight(t, sc, 7, 6000)
	_, m2, e2 := fight(t, sc, 7, 6000)
	if !reflect.DeepEqual(m1, m2) || !reflect.DeepEqual(e1, e2) {
		t.Fatal("same seed must replay the same fight")
	}
	distinct := map[int]bool{}
	for seed := uint64(1); seed <= 8; seed++ {
		_, m, _ := fight(t, sc, seed, 6000)
		distinct[m.ResolvedTick] = true
	}
	if len(distinct) < 4 {
		t.Errorf("8 seeds gave only %d distinct resolution ticks", len(distinct))
	}
}

func TestCombatDoesNotTouchEnemyStream(t *testing.T) {
	// Physical outcomes draw only from the physical stream.
	a := newSim(t, openField(t, 2, 36, 10), 5)
	b := newSim(t, openField(t, 2, 36, 10), 5)
	for range 1000 {
		a.Streams().Enemy.Uint64()
	}
	side := sides(openField(t, 2, 36, 10))
	var ea, eb []Event
	var oa, ob []Order
	var sta, stb State
	sta, _, _ = a.Step(nil)
	stb, _, _ = b.Step(nil)
	for range 100 {
		oa = nearestEnemy(sta, side, oa)
		ob = nearestEnemy(stb, side, ob)
		var x, y []Event
		sta, x, _ = a.Step(oa)
		stb, y, _ = b.Step(ob)
		ea, eb = append(ea, x...), append(eb, y...)
	}
	if !reflect.DeepEqual(ea, eb) || len(ofKind(ea, EventShot)) == 0 {
		t.Error("enemy-stream draws changed physical outcomes")
	}
}

// BenchmarkFight4v4 and BenchmarkFight12v12 run complete open-ground fights
// to resolution (or a 3-minute horizon) and report fights per second, game
// ticks per second, and the ticks a fight takes. The nearest-enemy controller
// is the benchmark's own and is included in the cost.
func BenchmarkFight4v4(b *testing.B)   { benchFight(b, 4, 36, 16) }
func BenchmarkFight12v12(b *testing.B) { benchFight(b, 12, 38, 28) }

func benchFight(b *testing.B, n, w, h int) {
	const horizon = 3 * 60 * 60
	sc := openField(b, n, w, h)
	side := sides(sc)
	s := &Sim{}
	var orders []Order
	ticks, resolved := 0, 0
	b.ReportAllocs()
	b.ResetTimer()
	for i := range b.N {
		st, err := s.Reset(sc, uint64(i)+1)
		if err != nil {
			b.Fatal(err)
		}
		for s.Tick() < horizon {
			orders = nearestEnemy(st, side, orders)
			if st, _, err = s.Step(orders); err != nil {
				b.Fatal(err)
			}
			if _, ok := s.Resolved(); ok {
				resolved++
				break
			}
		}
		ticks += s.Tick()
	}
	b.ReportMetric(float64(b.N)/b.Elapsed().Seconds(), "fights/s")
	b.ReportMetric(float64(ticks)/b.Elapsed().Seconds(), "ticks/s")
	b.ReportMetric(float64(ticks)/float64(b.N), "ticks/fight")
	b.ReportMetric(float64(resolved)/float64(b.N), "resolved/fight")
}

// BenchmarkRollout4v4 is the short-rollout cost: one second of game time
// (60 ticks) from the opening, the shape a policy search loop pays per probe.
func BenchmarkRollout4v4(b *testing.B) {
	sc := openField(b, 4, 36, 16)
	side := sides(sc)
	s := &Sim{}
	var orders []Order
	b.ReportAllocs()
	b.ResetTimer()
	for i := range b.N {
		st, err := s.Reset(sc, uint64(i)+1)
		if err != nil {
			b.Fatal(err)
		}
		for s.Tick() < 60 {
			orders = nearestEnemy(st, side, orders)
			if st, _, err = s.Step(orders); err != nil {
				b.Fatal(err)
			}
		}
	}
	b.ReportMetric(float64(b.N)/b.Elapsed().Seconds(), "rollouts/s")
	b.ReportMetric(float64(b.N)*60/b.Elapsed().Seconds(), "ticks/s")
}
