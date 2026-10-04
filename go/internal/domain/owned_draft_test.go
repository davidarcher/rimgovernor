package domain

import (
	"strings"
	"testing"
)

func TestOwnedDraftFlatIntentAndCoverage(t *testing.T) {
	for _, id := range []string{"", " ", "a\x00b", strings.Repeat("x", 257)} {
		if _, err := NewOwnedDraft(PawnID(id)); err == nil {
			t.Fatal("invalid pawn")
		}
	}
	d, _ := NewOwnedDraft("pawn")
	a, _ := NewOwnedDraftAction("draft", d)
	got, ok := a.OwnedDraft()
	if !ok || got != d || got.Pawn() != "pawn" {
		t.Fatal(a)
	}
	if _, ok = a.Building(); ok {
		t.Fatal("mixed family")
	}
	if _, err := NewPlan("plan", 1, []Action{a, a}); err == nil {
		t.Fatal("duplicate actions")
	}
	_ = map[Action]bool{a: true}
}

// A draft is an intent (#939): its receipt settles it, with no claim and
// no cleanup phase.
func TestOwnedDraftReceiptIsTerminal(t *testing.T) {
	RegisterIntentKind(OwnedDraftAction)
	d, _ := NewOwnedDraft("pawn")
	a, _ := NewOwnedDraftAction("draft", d)
	plan, err := NewPlan("plan", 1, []Action{a})
	if err != nil {
		t.Fatal(err)
	}
	p, _ := NewProgress(plan, a.ID())
	s := GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: "plan", Revision: 1, Native: 1}
	if p, err = p.Prepare(s, 10); err != nil {
		t.Fatal(err)
	}
	if p, err = p.MarkDispatched(s, 10); err != nil {
		t.Fatal(err)
	}
	if p, err = p.RecordReceipt(1, ReceiptAccepted); err != nil || p.View().Stage != Completed || p.View().Unresolved {
		t.Fatal(p.View(), err)
	}
	if StandardWorkOpen([]Progress{p}) {
		t.Fatal("completed draft left goal work open")
	}
}
