package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The pollution-clear area edit and the wastepack haul (#1683) persist and
// reload as themselves, the pollution-clear area distinct from home.
func TestPollutionActionsRoundTrip(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	clear, err := domain.NewPollutionClearArea(domain.AreaSetCells, []domain.Cell{{X: 5, Z: 1}, {X: 4, Z: 1}})
	if err != nil {
		t.Fatal(err)
	}
	home, _ := domain.NewArea(domain.AreaSetCells, "", []domain.Cell{{X: 5, Z: 1}, {X: 4, Z: 1}})
	if clear == home || clear.Home() || !clear.PollutionClear() {
		t.Fatal(clear, home)
	}
	areaClear, _ := domain.NewAreaAction("c", clear)
	areaHome, _ := domain.NewAreaAction("h", home)
	haul, err := domain.NewWastepackHaul("Wastepack12", "Wastepack", domain.Cell{X: 7, Z: 8})
	if err != nil {
		t.Fatal(err)
	}
	haulAction, err := domain.NewWastepackHaulAction("w", haul)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("pollution-plan", 1, []domain.Action{areaClear, areaHome, haulAction})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Standard.ID, g.Revision, "restore", p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "pollution-plan")
	if err != nil {
		t.Fatal(err)
	}
	got := loaded.Spec.Actions()
	if len(got) != 3 {
		t.Fatal(got)
	}
	if v, ok := got[0].Area(); !ok || v != clear {
		t.Fatal(v)
	}
	if v, ok := got[1].Area(); !ok || v != home {
		t.Fatal(v)
	}
	if v, ok := got[2].WastepackHaul(); !ok || v != haul {
		t.Fatal(v)
	}
}
