package domain

import "testing"

// A styled rectangle: the four corners and the two cells beside the door
// take the accent stuff, every other wall cell the run stuff, and the door
// its own stuff (#610).
func TestStyledPlacementsAccentsCornersAndDoorFrame(t *testing.T) {
	bounds := RoomBounds{X: 10, Z: 10, Width: 5, Height: 4}
	f, err := RectangleFootprint(bounds, South)
	if err != nil {
		t.Fatal(err)
	}
	style := ShellStyle{WallDef: "Wall", DoorDef: "Autodoor", DoorStuff: "Steel", WallStuff: func(part ShellPart) string {
		switch part {
		case ShellCorner:
			return "corner"
		case ShellDoorFrame:
			return "frame"
		}
		return "run"
	}}
	got := f.StyledPlacements(style)
	if len(got) != len(f.Walls()) || got[0].Definition() != "Autodoor" || got[0].Stuff() != "Steel" || got[0].Cell() != f.Door() {
		t.Fatalf("door placement %+v", got[0])
	}
	corners := map[Cell]bool{{10, 10}: true, {14, 10}: true, {10, 13}: true, {14, 13}: true}
	door := f.Door()
	frames := map[Cell]bool{{door.X - 1, door.Z}: true, {door.X + 1, door.Z}: true}
	for _, b := range got[1:] {
		want := "run"
		switch {
		case corners[b.Cell()]:
			want = "corner"
		case frames[b.Cell()]:
			want = "frame"
		}
		if b.Definition() != "Wall" || b.Stuff() != want {
			t.Fatalf("cell %v stuff %q want %q", b.Cell(), b.Stuff(), want)
		}
	}
	if f.StyledPlacements(ShellStyle{WallDef: "Wall", DoorDef: "Door", DoorStuff: "WoodLog"}) != nil {
		t.Fatal("a style without wall stuff expanded")
	}
	plain := f.Placements("Wall", "Door", "WoodLog")
	for _, b := range plain {
		if b.Stuff() != "WoodLog" {
			t.Fatalf("plain placement %+v", b)
		}
	}
}
