package store

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"path/filepath"
	"testing"
)

func TestIdeoligionReformRoundTripsExpectedState(t *testing.T) {
	s := open(t, filepath.Join(t.TempDir(), "reform.sqlite"))
	old := domain.IdeoligionDesign{Memes: []string{"Structure", "Normal"}, Precepts: []string{"Old"}, Fluid: true}
	next := domain.IdeoligionDesign{Memes: old.Memes, Precepts: []string{"New"}, Fluid: true}
	v, err := domain.NewIdeoligionReform("Ideo_1", old, next, 3)
	if err != nil {
		t.Fatal(err)
	}
	a, _ := domain.NewIdeoligionReformAction("reform-0", v)
	plan, _ := domain.NewPlan("reform", 1, []domain.Action{a})
	if err := s.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	got, err := s.LoadPlan(context.Background(), plan.ID())
	if err != nil || got.Spec.Actions()[0] != a {
		t.Fatalf("reform intent lost: %v %v", got, err)
	}
}
