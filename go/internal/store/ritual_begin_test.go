package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A ritual begin (#1659) persists its organizer, precept, spot and exact
// assignments, and a row that breaks the shape is refused by the schema.
func TestRitualBeginActionRoundTrips(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	value, err := domain.NewRitualBegin("guide", "Precept_12", domain.Cell{X: 40, Z: 41},
		[]domain.RitualSlot{{Slot: "moralist", Pawns: []domain.PawnID{"guide"}}, {Slot: "candidate", Pawns: []domain.PawnID{"a", "b"}}}, []domain.PawnID{"s1"})
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewRitualAction("rit", value)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("rit-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "restore", p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "rit-plan")
	if err != nil {
		t.Fatal(err)
	}
	got := loaded.Spec.Actions()
	if len(got) != 1 {
		t.Fatal(got)
	}
	if v, ok := got[0].Ritual(); !ok || v != value {
		t.Fatal(v, value)
	}
	for _, statement := range []string{
		"UPDATE actions SET ritual_payload=NULL WHERE id='rit'",
		"UPDATE actions SET x=NULL WHERE id='rit'",
		"UPDATE actions SET stuff='start' WHERE id='rit'",
		"UPDATE actions SET kind='quest_accept' WHERE id='rit'",
	} {
		if _, err := s.db.Exec(statement); err == nil {
			t.Errorf("%s accepted", statement)
		}
	}
}
