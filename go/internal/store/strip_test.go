package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestStripActionRoundTrips(t *testing.T) {
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "strip.sqlite"))
	strip, _ := domain.NewStrip("Corpse_Human12")
	a, _ := domain.NewStripAction("strip-0", strip)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	got, err := s.LoadPlan(ctx, "plan")
	if err != nil || got.Spec.Actions()[0] != a {
		t.Fatalf("%+v %v", got, err)
	}
}
