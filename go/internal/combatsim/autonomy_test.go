package combatsim

import (
	"reflect"
	"testing"
)

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
