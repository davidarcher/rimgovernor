package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func apparelPolicyStoreFixture(t *testing.T) (*Store, string, ApparelPolicyAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "apparel_policy.db")
	s := open(t, path)
	value, _ := domain.NewApparelPolicy(domain.ApparelPolicySpec{Pawn: "pawn", Token: "cas", Name: "RimGovernor worker", Definitions: []string{"Shirt"}, MinHP: .51, MaxHP: 1, MaxQuality: 6})
	a, _ := domain.NewApparelPolicyAction("apparel-policy", value)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
	return s, path, ApparelPolicyAdmission{Snapshot: snapshot, Tick: 12, Value: value.Encoded()}
}

func TestApparelPolicyActionRoundTripsAndAdmits(t *testing.T) {
	ctx := context.Background()
	s, path, v := apparelPolicyStoreFixture(t)
	if _, err := s.PrepareApparelPolicy(ctx, "plan", "apparel-policy", v); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.ApparelPolicyAdmissions) != 1 || state.ApparelPolicyAdmissions[0].Admission != v || state.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(state, err)
	}
	got, ok := state.Spec.Actions()[0].ApparelPolicy()
	if !ok || got.Encoded() != v.Value {
		t.Fatal("dialog answer did not round-trip", got, ok)
	}
	if _, err := s.Dispatch(ctx, "plan", "apparel-policy", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
}

func TestApparelPolicyDispatchRequiresTypedAdmission(t *testing.T) {
	ctx := context.Background()
	s, _, v := apparelPolicyStoreFixture(t)
	if _, err := s.Dispatch(ctx, "plan", "apparel-policy", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.Prepare(ctx, "plan", "apparel-policy", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted a dialog answer action")
	}
	wrong := v
	wrong.Value = "wrong"
	if _, err := s.PrepareApparelPolicy(ctx, "plan", "apparel-policy", wrong); err == nil {
		t.Fatal("admission for a different option accepted")
	}
}
