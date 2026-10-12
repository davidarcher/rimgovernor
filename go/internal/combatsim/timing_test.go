package combatsim

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A launched projectile ticks every offCameraInterval ticks at a phase of its
// own (A9): it lands on the first interval tick at or after launch plus its
// 3D flight, so the flight is the 3D ticks plus 0 to 14 and every residue of
// the interval is landed on.
func TestProjectileLandsOnItsIntervalTickAfterTheThreeDimensionalFlight(t *testing.T) {
	w := rapid()
	w.CooldownSeconds = 600 // one shot per seed
	// 20 cells: the destination is within 0.3 of 20 on each axis and the origin
	// is pawnAltitude (about 8.41) above the ground, so the 3D distance is
	// 21.4..22.0 cells, 39..40 ticks at 0.55 cells per tick; the flat distance
	// would give 37.
	sc := duel(t, w, immortal, "a"+strings.Repeat(".", 19)+"b")
	residues := map[int]bool{}
	for seed := uint64(1); seed <= 300; seed++ {
		_, ev := runUntil(t, newSim(t, sc, seed), []Order{autoAttack("a", "b")}, 1, nil)
		shots := ofKind(ev, EventShot)
		if len(shots) != 1 {
			t.Fatalf("seed %d: %d shots", seed, len(shots))
		}
		if shots[0].Outcome != ShotFlying {
			continue
		}
		flight := shots[0].ETA - shots[0].Tick
		if flight < 39 || flight > 40+14 {
			t.Fatalf("seed %d: flight %d ticks, want 39..54", seed, flight)
		}
		residues[shots[0].ETA%offCameraInterval] = true
	}
	if len(residues) != offCameraInterval {
		t.Errorf("projectiles landed on %d of %d interval phases", len(residues), offCameraInterval)
	}
}

// The projectile's phase comes from its own stream: drawing it does not move
// the shot report's rolls, so the same seed still decides the same hits.
func TestProjectilePhaseHasItsOwnStream(t *testing.T) {
	var s Sim
	if _, err := s.Reset(duel(t, rapid(), immortal, "a....b"), 5); err != nil {
		t.Fatal(err)
	}
	st := s.Streams()
	if st.Phase == nil || st.Phase == st.Physical {
		t.Fatal("no dedicated phase stream")
	}
}

// An armed colonist walking to a cell starts Wait_Combat's auto-attack in the
// pather tick it arrives in, and the stance tick of that same tick counts the
// warmup down, so the first shot is arrival + warmup - 1 (A36: real movers
// arrive at 29/29/57 and shoot at 88/88/116).
func TestMoverStartsAutoAttackInTheArrivalTick(t *testing.T) {
	sc := duel(t, testRifle, immortal, "a.........b")
	s := newSim(t, sc, 3)
	a, err := NewAdapter(Fight{Name: "t", Scenario: sc, Facts: factsFor(sc)}, s)
	if err != nil {
		t.Fatal(err)
	}
	if err := s.Apply([]Order{{Pawn: "a", Kind: OrderMove, To: domain.Cell{X: 3, Z: 0}}}); err != nil {
		t.Fatal(err)
	}
	var all []Event
	for s.Tick() < 300 {
		_, ev, err := s.Step(a.Autonomy())
		if err != nil {
			t.Fatal(err)
		}
		all = append(all, ev...)
	}
	arrived := -1
	for _, e := range all {
		if e.Kind == EventArrived && e.Pawn == "a" {
			arrived = e.Tick
		}
	}
	if arrived <= 0 {
		t.Fatal("the mover never arrived")
	}
	if got := firstShot(all, "a"); got != arrived+60-1 {
		t.Errorf("arrived at %d, first shot at %d, want %d", arrived, got, arrived+59)
	}
}

// StartPath queues an async request that PatherTick claims one tick later, so
// a one-cell walk of cost 10 arrives on tick 11 (A37).
func TestPathRequestIsClaimedATickLate(t *testing.T) {
	s := newSim(t, scenario(t, 6, "a."), 1)
	ev := run(t, s, []Order{{Pawn: "a", To: cell(1, 0)}}, 100, arrived)
	if got := arrivalTick(ev); got != 10+pathLatency {
		t.Errorf("arrived at tick %d, want %d", got, 10+pathLatency)
	}
}

// A raider deciding at tick 0 is a resumed checkpoint's pawn already in
// Wait_Combat: its warmup counts down from tick 0 and its first shot is the
// warmup's length, not one tick more (A36).
func TestRaiderDecidingAtTickZeroShootsOnTheWarmupTick(t *testing.T) {
	sc := raidScene(t, testRifle, "a", "a.....h")
	s, e := raidAI(t, sc, 1, EnemyConfig{})
	_, ev := runUntil(t, s, e.Orders(), 100, nil)
	if got := firstShot(ev, "h"); got != 60 {
		t.Errorf("raider's first shot at tick %d, want 60", got)
	}
}
