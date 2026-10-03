package domain

import (
	"strings"
	"testing"
)

func TestRitualValidationAndAction(t *testing.T) {
	r, err := NewRitual("pawn-1", RitualBestowing, RitualStart)
	if err != nil || r.Pawn() != "pawn-1" || r.Ritual() != RitualBestowing || r.Verb() != RitualStart {
		t.Fatal(r, err)
	}
	a, err := NewRitualAction("ritual-1", r)
	if err != nil || a.Kind() != RitualAction {
		t.Fatal(a, err)
	}
	if got, ok := a.Ritual(); !ok || got != r {
		t.Fatal(got, ok)
	}
	if _, ok := a.QuestAccept(); ok {
		t.Fatal("ritual exposed quest accept")
	}
	for _, bad := range []string{"", " ", "x\x00y", strings.Repeat("x", 257)} {
		if _, err := NewRitual(PawnID(bad), RitualBestowing, RitualStart); err == nil {
			t.Fatal("invalid pawn accepted")
		}
		if _, err := NewRitualAction(ActionID(bad), r); err == nil {
			t.Fatal("invalid action id accepted")
		}
	}
	if _, err := NewRitual("pawn-1", "", RitualStart); err == nil {
		t.Fatal("empty ritual accepted")
	}
	if _, err := NewRitual("pawn-1", "funeral", RitualStart); err == nil {
		t.Fatal("unknown ritual accepted")
	}
	if _, err := NewRitual("pawn-1", RitualBestowing, "cancel"); err == nil {
		t.Fatal("unknown verb accepted")
	}
	if _, err := NewRitualAction("ritual-1", Ritual{}); err == nil {
		t.Fatal("zero ritual accepted")
	}
}
