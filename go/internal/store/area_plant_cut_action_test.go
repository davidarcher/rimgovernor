package store

import (
	"context"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A area plant cut (#1547) persists its canonical cells.
func TestAreaPlantCutActionRoundTrips(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	value, err := domain.NewAreaPlantCut([]domain.Cell{{X: 4, Z: 3}, {X: 2, Z: 7}})
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewAreaPlantCutAction("cut", value)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("cut-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "restore", p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "cut-plan")
	if err != nil {
		t.Fatal(err)
	}
	got := loaded.Spec.Actions()
	if len(got) != 1 {
		t.Fatal(got)
	}
	v, ok := got[0].AreaPlantCut()
	if !ok || v != value || !slices.Equal(v.Cells(), []domain.Cell{{X: 2, Z: 7}, {X: 4, Z: 3}}) {
		t.Fatal(v, value)
	}
}
