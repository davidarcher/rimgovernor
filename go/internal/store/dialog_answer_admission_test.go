package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func dialogAnswerStoreFixture(t *testing.T) (*Store, string, DialogAnswerAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "dialog_answer.db")
	s := open(t, path)
	value, _ := domain.NewDialogAnswer(42, 1, "Research screen")
	a, _ := domain.NewDialogAnswerAction("dialog-answer", value)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
	return s, path, DialogAnswerAdmission{Snapshot: snapshot, Tick: 12, WindowID: 42, OptionIndex: 1, OptionLabel: "Research screen"}
}

func TestDialogAnswerActionRoundTripsAndAdmits(t *testing.T) {
	ctx := context.Background()
	s, path, v := dialogAnswerStoreFixture(t)
	if _, err := s.PrepareDialogAnswer(ctx, "plan", "dialog-answer", v); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.DialogAnswerAdmissions) != 1 || state.DialogAnswerAdmissions[0].Admission != v || state.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(state, err)
	}
	got, ok := state.Spec.Actions()[0].DialogAnswer()
	if !ok || got.WindowID() != 42 || got.OptionIndex() != 1 || got.OptionLabel() != "Research screen" {
		t.Fatal("dialog answer did not round-trip", got, ok)
	}
	if _, err := s.Dispatch(ctx, "plan", "dialog-answer", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
}

func TestDialogAnswerDispatchRequiresTypedAdmission(t *testing.T) {
	ctx := context.Background()
	s, _, v := dialogAnswerStoreFixture(t)
	if _, err := s.Dispatch(ctx, "plan", "dialog-answer", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.Prepare(ctx, "plan", "dialog-answer", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted a dialog answer action")
	}
	wrong := v
	wrong.OptionLabel = "OK"
	if _, err := s.PrepareDialogAnswer(ctx, "plan", "dialog-answer", wrong); err == nil {
		t.Fatal("admission for a different option accepted")
	}
}
