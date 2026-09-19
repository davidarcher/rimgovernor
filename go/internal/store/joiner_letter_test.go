package store

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"path/filepath"
	"testing"
)

func TestJoinerLetterTargetSurvivesRestartAndGuardsAdmission(t *testing.T) {
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
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "letter-plan", Revision: 1, Native: 2}
	admission := DialogAnswerAdmission{Snapshot: snapshot, Tick: 12, WindowID: 7, OptionIndex: 0, OptionLabel: "Accept"}
	if _, err := s.PrepareDialogAnswer(ctx, plan.ID(), action.ID(), admission); err == nil {
		t.Fatal("accepted window admission for letter")
	}
	admission.LetterToken = "letter-token"
	if _, err := s.PrepareDialogAnswer(ctx, plan.ID(), action.ID(), admission); err != nil {
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
	if !ok || got != value || loaded.DialogAnswerAdmissions[0].Admission != admission {
		t.Fatal("letter target lost", loaded)
	}
}
