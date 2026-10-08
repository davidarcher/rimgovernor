package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

type consumptionFake struct {
	observation.RoundsSource
	asked []int
	page  policy.ConsumptionPage
	err   error
}

func (f *consumptionFake) ReadConsumption(_ context.Context, since int) (policy.ConsumptionPage, error) {
	f.asked = append(f.asked, since)
	return f.page, f.err
}

// The review asks native for the whole window once per load, then only for
// the hours after the last one it holds; a new load starts over.
func TestResourceConsumptionAsksSinceHour(t *testing.T) {
	native := &consumptionFake{page: policy.ConsumptionPage{CurrentHour: 48, FirstHour: 0, Hours: []policy.ConsumptionHour{
		{Hour: 3, Rows: []policy.ConsumptionRow{{Resource: "Steel", Reason: "bill_ingredient", Count: 20}, {Resource: "Steel", Reason: "rot", Count: 99}}}}}}
	r := &Rounder{native: native}
	ctx := context.Background()
	one := domain.GenerationSnapshot{Colony: "c", Load: "l1"}

	c, known := r.resourceConsumption(ctx, one).Value()
	if !known || c.WindowDays != 2 || c.Recurring["Steel"] != 20 {
		t.Fatal(c, known)
	}
	native.page = policy.ConsumptionPage{CurrentHour: 50, FirstHour: 0}
	if c, _ = r.resourceConsumption(ctx, one).Value(); c.WindowDays != 50.0/24 || c.Recurring["Steel"] != 20 {
		t.Fatal(c)
	}
	two := one
	two.Load = "l2"
	r.resourceConsumption(ctx, two)
	if got := native.asked; len(got) != 3 || got[0] != -1 || got[1] != 47 || got[2] != -1 {
		t.Fatal(got)
	}
	native.err = errors.New("native down")
	if _, known = r.resourceConsumption(ctx, two).Value(); known {
		t.Fatal("a failed read is unknown")
	}
}
