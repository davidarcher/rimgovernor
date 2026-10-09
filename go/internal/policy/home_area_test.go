package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func homeAreaFixture(t *testing.T, auto bool, current []domain.Cell, points ...domain.Cell) (domain.Fact[HomeAreaPlan], error) {
	t.Helper()
	r := extentFixture(t, points...)
	h, _ := r.Home.Value()
	h.Home, h.AutoHome = domain.Known(current), domain.Known(auto)
	return PlanHomeArea(domain.Known(Bounds{Width: 60, Height: 60}), r.Construction, r.Claims, domain.Known(h), nil)
}

func TestHomeAreaFootprintPlusMarginDropsFarOutpost(t *testing.T) {
	var base []domain.Cell
	for x := int32(10); x <= 12; x++ {
		for z := int32(10); z <= 12; z++ {
			base = append(base, domain.Cell{X: x, Z: z})
		}
	}
	near, far := domain.Cell{X: 25, Z: 11}, domain.Cell{X: 55, Z: 55}
	current := []domain.Cell{{X: 10, Z: 10}, {X: 0, Z: 0}, far}
	got, err := homeAreaFixture(t, false, current, append(base, near, far)...)
	plan, known := got.Value()
	if err != nil || !known {
		t.Fatal(known, err)
	}
	if plan.AutoOff {
		t.Fatal("auto-expand already off")
	}
	// Footprint plus the four-cell margin, and the near structure's region.
	for _, c := range []domain.Cell{{X: 6, Z: 6}, {X: 16, Z: 16}, {X: 11, Z: 11}, {X: 21, Z: 11}, near} {
		if !slices.Contains(plan.Set, c) {
			t.Fatalf("%v not set", c)
		}
	}
	for _, c := range []domain.Cell{{X: 5, Z: 6}, {X: 17, Z: 11}, {X: 10, Z: 10}, far, {X: 51, Z: 51}} {
		if slices.Contains(plan.Set, c) {
			t.Fatalf("%v set", c)
		}
	}
	if !slices.Equal(plan.Clear, []domain.Cell{{X: 0, Z: 0}, far}) {
		t.Fatal("clear", plan.Clear)
	}
}

func TestHomeAreaAutoExpandOffOnceAndConverged(t *testing.T) {
	c := domain.Cell{X: 30, Z: 30}
	var target []domain.Cell
	for x := int32(26); x <= 34; x++ {
		for z := int32(26); z <= 34; z++ {
			target = append(target, domain.Cell{X: x, Z: z})
		}
	}
	got, err := homeAreaFixture(t, true, target, c)
	plan, known := got.Value()
	if err != nil || !known || !plan.AutoOff || len(plan.Set) != 0 || len(plan.Clear) != 0 || plan.Empty() {
		t.Fatal(plan, known, err)
	}
	got, err = homeAreaFixture(t, false, target, c)
	if plan, known = got.Value(); err != nil || !known || !plan.Empty() {
		t.Fatal(plan, known, err)
	}
}

func TestHomeAreaUnknownWithoutHomeCellsOrSetting(t *testing.T) {
	r := extentFixture(t, domain.Cell{X: 3, Z: 3})
	got, err := PlanHomeArea(r.Bounds, r.Construction, r.Claims, r.Home, nil)
	if _, known := got.Value(); err != nil || known {
		t.Fatal(known, err)
	}
}
