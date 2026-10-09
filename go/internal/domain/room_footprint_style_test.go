package domain

import "testing"

// A styled rectangle: the door takes its own stuff and every other wall cell
// the wall stuff.
func TestStyledPlacementsDoorAndWallStuff(t *testing.T) {
	f, err := RectangleFootprint(RoomBounds{X: 10, Z: 10, Width: 5, Height: 4}, South)
	if err != nil {
		t.Fatal(err)
	}
	got := f.StyledPlacements(ShellStyle{WallDef: "Wall", DoorDef: "Autodoor", DoorStuff: "Steel", WallStuff: "run"})
	if len(got) != len(f.Walls()) || got[0].Definition() != "Autodoor" || got[0].Stuff() != "Steel" || got[0].Cell() != f.Door() {
		t.Fatalf("door placement %+v", got[0])
	}
	for _, b := range got[1:] {
		if b.Definition() != "Wall" || b.Stuff() != "run" {
			t.Fatalf("cell %v stuff %q", b.Cell(), b.Stuff())
		}
	}
}
