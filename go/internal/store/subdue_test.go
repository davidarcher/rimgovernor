package store

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"path/filepath"
	"testing"
)

func TestSubdueRoundTripAndMoodEvidence(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "subdue.sqlite")
	s := open(t, path)
	d, _ := domain.NewOwnedDraft("responder")
	da, _ := domain.NewOwnedDraftAction("draft", d)
	m, _ := domain.NewSubdue("responder", "broken", "draft")
	a, _ := domain.NewMeleeAttackAction("subdue", m)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{da, a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	got, err := s.LoadPlan(ctx, "plan")
	if err != nil || got.Spec.Actions()[1] != a {
		t.Fatalf("%+v %v", got, err)
	}
	sites, err := s.DispatchSites(ctx, "plan")
	if err != nil || sites[a.ID()].Worker != "responder" || sites[a.ID()].Target != "broken" {
		t.Fatal(sites, err)
	}
	state := policy.MentalState{DefName: "Wander_Sad", TicksInState: 90}
	history := policy.MoodHistory{States: []policy.MoodState{{Pawn: policy.MoodPawn{ID: "broken", Mental: domain.Known(true), Break: domain.Known(state)}, Active: true}}}
	restored := (RoutineReview{Mood: moodRecord(history)}).MoodHistory()
	if value, known := restored.States[0].Pawn.Break.Value(); !known || value != state {
		t.Fatal(restored)
	}
}
