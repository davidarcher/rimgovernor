package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A standing wall of a lower ranked stuff is swapped in place, one cell
// of an otherwise complete ring per pass, never while the ring has other work.
func TestReconcileSwapsOneWallOfLowerStuffAtATime(t *testing.T) {
	in, _ := reconFixture()
	in.Ground.stuff = map[domain.Cell]string{}
	for c := range in.Ground.walls {
		in.Ground.stuff[c] = "WoodLog"
	}
	// Nothing wants better: nothing owed.
	if rec := Reconcile(in); len(rec.Owed) != 0 {
		t.Fatal(rec.Owed)
	}
	in.WallUpgrade = func(have string) bool { return have == "WoodLog" }
	op := readyOp(t, Reconcile(in), OpWallUp)
	if len(op.Cells) != 1 || !in.Ground.walls[op.Cells[0]] {
		t.Fatalf("one wall cell per pass: %+v", op)
	}
	// A wall already of the wanted stuff is left.
	for c := range in.Ground.stuff {
		in.Ground.stuff[c] = "BlocksGranite"
	}
	if rec := Reconcile(in); len(rec.Owed) != 0 {
		t.Fatal(rec.Owed)
	}
	// So is a wall whose stuff the census does not name (rock).
	in.Ground.stuff = map[domain.Cell]string{}
	if rec := Reconcile(in); len(rec.Owed) != 0 {
		t.Fatal("an unnamed stuff is left", rec.Owed)
	}
	// A ring gap is closed first; no swap opens a second hole.
	for c := range in.Ground.walls {
		in.Ground.stuff[c] = "WoodLog"
	}
	for c := range in.Ground.walls {
		delete(in.Ground.walls, c)
		break
	}
	if k := readyKinds(Reconcile(in)); !kindsEqual(k, OpWallIn) {
		t.Fatal(k)
	}
}
