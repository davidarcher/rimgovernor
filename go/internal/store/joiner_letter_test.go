package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestJoinerLetterTargetSurvivesRestart(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "letters.db")
	s := open(t, path)
	value, err := domain.NewJoinerLetterAnswer(7, "Accept", "letter-token")
	if err != nil {
		t.Fatal(err)
	}
	action, _ := domain.NewDialogAnswerAction("answer", value)
	plan, _ := domain.NewPlan("letter-plan", 1, []domain.Action{action})
	if err := s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	got, ok := loaded.Spec.Actions()[0].DialogAnswer()
	if !ok || got != value {
		t.Fatal("letter target lost", loaded)
	}
}
