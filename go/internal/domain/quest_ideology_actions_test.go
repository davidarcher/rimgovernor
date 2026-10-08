package domain

import (
	"math"
	"testing"
)

func TestHackDesignationPreservesExplicitDisabledState(t *testing.T) {
	h, err := NewHackDesignation("Terminal_1", false)
	if err != nil || h.Enabled() {
		t.Fatal(h, err)
	}
	a, err := NewHackDesignationAction("toggle", h)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = NewPlan("plan", 1, []Action{a}); err != nil {
		t.Fatal(err)
	}
	if _, err = NewHackDesignation("", true); err == nil {
		t.Fatal("empty target accepted")
	}
}

func TestGiveItemRequiresBoundedWholeRequestPrecondition(t *testing.T) {
	for _, count := range []int64{0, -1, math.MaxInt32 + 1} {
		if _, err := NewGiveItem("hauler", "visitor", "Silver", count); err == nil {
			t.Fatalf("accepted count %d", count)
		}
	}
	if _, err := NewGiveItem("visitor", "visitor", "Silver", 3); err == nil {
		t.Fatal("self delivery accepted")
	}
	g, err := NewGiveItem("hauler", "visitor", "Silver", math.MaxInt32)
	if err != nil {
		t.Fatal(err)
	}
	a, err := NewGiveItemAction("gift", g)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = NewPlan("plan", 1, []Action{a}); err != nil {
		t.Fatal(err)
	}
}
