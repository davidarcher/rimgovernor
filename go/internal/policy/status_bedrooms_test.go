package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestBedroomRowsPerWingAndSuite(t *testing.T) {
	room := func(x, z, w, h int32) LayoutRoom {
		return LayoutRoom{Interior: Rectangle{X: x, Z: z, Width: w, Height: h}}
	}
	standing := func(id string, r LayoutRoom, bed string) Room {
		c := domain.Cell{X: r.Interior.X + r.Interior.Width/2, Z: r.Interior.Z + r.Interior.Height/2}
		return Room{ID: id, Enclosed: domain.Known(true), Cells: []domain.Cell{c}, Beds: []string{bed}}
	}
	a, b, c := room(0, 0, 3, 4), room(4, 0, 3, 4), room(20, 0, 4, 4)
	s1, s2 := room(0, 10, 6, 6), room(7, 10, 6, 6)
	plan := LayoutPlan{Wings: []Wing{
		{Purpose: WingBedrooms, Rooms: []LayoutRoom{a, b}},
		{Purpose: WingSuites, Rooms: []LayoutRoom{s1, s2}},
		{Purpose: WingBedrooms, Rooms: []LayoutRoom{c}},
	}}
	rooms := RoomObservation{Rooms: []Room{standing("ra", a, "ba"), standing("rb", b, "bb"), standing("rs", s1, "bs")}}
	sleeping := SleepingObservation{Beds: []SleepingBed{{ID: "ba", Owners: []PawnID{"Ann"}}, {ID: "bb"}, {ID: "bs", Owners: []PawnID{"Bo"}}}}
	targets := map[string]RoomTarget{"rs": {Reasons: []string{"greedy", "tier", "title"}}}
	got := BedroomRows(plan, rooms, sleeping, targets)
	want := []struct {
		key, text string
		detail    bool
	}{
		{"bedrooms.0", "Bedrooms 1/2, 3x4", false},
		{"bedrooms.1", "Bedrooms 0/1, 4x4", false},
		{"domain.Bedrooms", "Bedrooms", true},
		{"suite.0.10", "Suite 6x6 Bo: greedy, title", true},
		{"suite.7.10", "Suite 6x6 vacant", true},
	}
	if len(got) != len(want) {
		t.Fatalf("rows = %+v", got)
	}
	for i, w := range want {
		if got[i].Key != w.key || got[i].Text != w.text || got[i].Detail != w.detail {
			t.Errorf("row %d = %q %q %v, want %+v", i, got[i].Key, got[i].Text, got[i].Detail, w)
		}
	}
	if suiteReason(RoomTarget{Reasons: []string{"tier"}}) != "space" {
		t.Error("a tier-only suite should read space")
	}
}
