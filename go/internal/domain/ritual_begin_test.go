package domain

import "testing"

func TestRitualBeginValidationAndAction(t *testing.T) {
	slots := []RitualSlot{{Slot: "moralist", Pawns: []PawnID{"guide"}}, {Slot: "candidate", Pawns: []PawnID{"a", "b"}}}
	r, err := NewRitualBegin("guide", "Precept_12", Cell{X: 40, Z: 41}, slots, []PawnID{"s1"})
	if err != nil || r.Verb() != RitualBegin || r.Ritual() != "Precept_12" || r.Pawn() != "guide" || r.Spot() != (Cell{X: 40, Z: 41}) {
		t.Fatal(r, err)
	}
	got := r.Slots()
	if len(got) != 2 || got[0].Slot != "candidate" || got[1].Slot != "moralist" || len(got[0].Pawns) != 2 || got[0].Pawns[1] != "b" {
		t.Fatalf("slots are sorted by id and keep pawn order: %v", got)
	}
	if s := r.Spectators(); len(s) != 1 || s[0] != "s1" {
		t.Fatal(s)
	}
	slots[0].Pawns[0] = "changed"
	if r.Slots()[1].Pawns[0] != "guide" {
		t.Fatal("begin aliases the caller's slots")
	}
	a, err := NewRitualAction("ritual-2", r)
	if err != nil {
		t.Fatal(err)
	}
	if again, _ := a.Ritual(); again != r {
		t.Fatal("round trip changed the ritual")
	}
	empty, err := NewRitualBegin("guide", "Precept_12", Cell{}, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := NewRitualAction("ritual-3", empty); err != nil {
		t.Fatal(err)
	}
}

func TestRitualBeginRefusals(t *testing.T) {
	ok := []RitualSlot{{Slot: "moralist", Pawns: []PawnID{"guide"}}}
	two := []RitualSlot{{Slot: "moralist", Pawns: []PawnID{"guide"}}, {Slot: "moralist", Pawns: []PawnID{"other"}}}
	crossed := []RitualSlot{{Slot: "moralist", Pawns: []PawnID{"guide"}}, {Slot: "y", Pawns: []PawnID{"guide"}}}
	for name, build := range map[string]func() (Ritual, error){
		"organizer":     func() (Ritual, error) { return NewRitualBegin("", "Precept_1", Cell{}, ok, nil) },
		"precept":       func() (Ritual, error) { return NewRitualBegin("guide", " ", Cell{}, ok, nil) },
		"bestowing id":  func() (Ritual, error) { return NewRitualBegin("guide", string(RitualBestowing), Cell{}, ok, nil) },
		"negative spot": func() (Ritual, error) { return NewRitualBegin("guide", "Precept_1", Cell{X: -1}, ok, nil) },
		"slot id": func() (Ritual, error) {
			return NewRitualBegin("guide", "Precept_1", Cell{}, []RitualSlot{{Pawns: []PawnID{"a"}}}, nil)
		},
		"empty slot": func() (Ritual, error) {
			return NewRitualBegin("guide", "Precept_1", Cell{}, []RitualSlot{{Slot: "x"}}, nil)
		},
		"repeated slot":   func() (Ritual, error) { return NewRitualBegin("guide", "Precept_1", Cell{}, two, nil) },
		"pawn in 2 slots": func() (Ritual, error) { return NewRitualBegin("guide", "Precept_1", Cell{}, crossed, nil) },
		"spectator twice": func() (Ritual, error) { return NewRitualBegin("guide", "Precept_1", Cell{}, ok, []PawnID{"guide"}) },
		"bad spectator":   func() (Ritual, error) { return NewRitualBegin("guide", "Precept_1", Cell{}, ok, []PawnID{""}) },
	} {
		if _, err := build(); err == nil {
			t.Errorf("%s accepted", name)
		}
	}
	// A start carries no spot or assignments, and a begin cannot be forged
	// from a start or carry assignments it did not build.
	begin, _ := NewRitualBegin("guide", "Precept_1", Cell{X: 1, Z: 1}, ok, nil)
	forged := begin
	forged.verb = RitualStart
	if _, err := NewRitualAction("ritual-4", forged); err == nil {
		t.Fatal("start with assignments accepted")
	}
	foreign := begin
	foreign.assignment = `{"Spectators":[],"Slots":[]}`
	if _, err := NewRitualAction("ritual-5", foreign); err == nil {
		t.Fatal("begin with noncanonical assignments accepted")
	}
}
