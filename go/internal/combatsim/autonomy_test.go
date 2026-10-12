package combatsim

import (
	"math"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A drafted colonist's CheckForAutoAttack is the same scored random pick a
// raider makes (A46), not the nearest hostile: hostiles 10 and 25 cells away
// score 50 and 35, weigh 1 and 0.5, and are drawn 2 : 1 over many runs.
func TestAutonomyColonistPicksByScore(t *testing.T) {
	rows := emptyRows(81, 61, map[domain.Cell]byte{cell(40, 30): 'h', cell(50, 30): 'a', cell(40, 5): 'b'})
	sc := raidScene(t, longRifle(), "h", rows...)
	const n = 3000
	picked := map[domain.PawnID]int{}
	for seed := range uint64(n) {
		s := newSim(t, sc, seed)
		a, err := NewAdapter(Fight{Name: "t", Scenario: sc, Facts: factsFor(sc)}, s)
		if err != nil {
			t.Fatal(err)
		}
		for _, o := range a.Autonomy() {
			if o.Pawn == "h" {
				picked[o.Target]++
			}
		}
	}
	if got := float64(picked["a"]) / n; math.Abs(got-2.0/3) > 0.04 {
		t.Errorf("the nearer hostile was picked %.3f of runs, want 0.667 (picks %v)", got, picked)
	}
	if picked["a"]+picked["b"] != n {
		t.Errorf("the colonist did not pick every run: %v", picked)
	}
}

// A drafted colonist that is idle shoots on its own whether or not it was
// ordered to hold (#2899): vanilla's drafted idle job is Wait_Combat. A pawn
// that is walking, on hold fire or outside the plan does not.
func TestAutonomyCoversEveryIdleDraftedColonist(t *testing.T) {
	sc := duel(t, testRifle, nil, "a.b...")
	a := adapterFor(t, sc)
	want := []Order{{Pawn: "a", Kind: OrderAttack, Target: "b", Auto: true}}
	if got := a.Autonomy(); !reflect.DeepEqual(got, want) {
		t.Fatalf("idle colonist with no order: %v, want %v", got, want)
	}
	a.holdFire["a"] = true
	if got := a.Autonomy(); len(got) != 0 {
		t.Fatalf("hold fire: %v", got)
	}
	delete(a.holdFire, "a")
	a.sim.pawns[a.sim.pawnIndex("a")].moving = true
	if got := a.Autonomy(); len(got) != 0 {
		t.Fatalf("walking colonist: %v", got)
	}
	a.sim.pawns[a.sim.pawnIndex("a")].moving = false
	delete(a.orderable, "a")
	if got := a.Autonomy(); len(got) != 0 {
		t.Fatalf("colonist outside the plan: %v", got)
	}
}
