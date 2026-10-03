package buildingruntime

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The outer ring is built only behind the built core ring, the wealth
// milestone and the stone floor; once begun it stays (#1596).
func TestDefenseOuterRingGate(t *testing.T) {
	t.Parallel()
	core := func(built bool) []store.DefenseTierRecord {
		return []store.DefenseTierRecord{
			{Name: policy.TierTrapCorridor},
			{Name: "perimeter-00", Built: built},
			{Name: "perimeter-01", Built: true},
			{Name: policy.TierPumpPrefix + "00"},
		}
	}
	projection := func(wealth float64, stone int64) observation.ColonyProjection {
		var p observation.ColonyProjection
		p.Facts.Wealth = domain.Known(policy.WealthFacts{Total: wealth})
		p.Resources = domain.Known(map[policy.Resource]int64{"BlocksGranite": stone, "WoodLog": 1e6})
		return p
	}
	rich, stone := defenseOuterRingWealth, defenseOuterRingStoneFloor
	for name, c := range map[string]struct {
		tiers      []store.DefenseTierRecord
		projection observation.ColonyProjection
		want       bool
	}{
		"gate holds":           {core(true), projection(rich, stone), true},
		"core ring incomplete": {core(false), projection(rich, stone), false},
		"wealth short":         {core(true), projection(rich-1, stone), false},
		"stone short":          {core(true), projection(rich, stone-1), false},
		"no core ring":         {nil, projection(rich, stone), false},
		"unknown wealth":       {core(true), observation.ColonyProjection{}, false},
		"removal pending":      {append(core(true), store.DefenseTierRecord{Name: "perimeter-r1-x00", Remove: true}), projection(rich, stone), false},
		"begun stays":          {append(core(false), store.DefenseTierRecord{Name: "perimeter-outer-00"}), projection(0, 0), true},
	} {
		if got := defenseOuterRingOpen(store.DefenseLayoutRecord{Tiers: c.tiers}, c.projection); got != c.want {
			t.Errorf("%s: open = %v, want %v", name, got, c.want)
		}
	}
}

// The outer ring's reservations are ignored until the gate opens, then
// join the record as stone sections of their own.
func TestDefenseRecutPerimeterOuterRing(t *testing.T) {
	t.Parallel()
	plan, ok := policy.DeriveLayoutPlan(perimeterSurvey(func(x, z int32) policy.SurveyCell {
		return policy.SurveyCell{Walkable: true, Fertility: 1}
	}), 3, policy.BuildTierCamp, nil, 30).Value()
	if !ok {
		t.Fatal("no plan")
	}
	plan.Reservations = append(plan.Reservations,
		policy.LayoutReservation{Kind: policy.ReserveOuterWall, Area: policy.Rectangle{X: 4, Z: 4, Width: 45, Height: 3}},
		policy.LayoutReservation{Kind: policy.ReserveOuterGate, Area: policy.Rectangle{X: 20, Z: 4, Width: 1, Height: 3}})
	record := perimeterRecord(t, plan)
	outer := func() (n int) {
		for _, tier := range record.Tiers {
			if policy.IsOuterPerimeterTier(tier.Name) {
				n++
			}
		}
		return n
	}
	if _, err := defenseRecutPerimeter(&record, plan, perimeterBounds, policy.PerimeterBridge, nil, 0, false, nil); err != nil || outer() != 0 {
		t.Fatal("outer ring planned behind a closed gate", err, outer())
	}
	if changed, err := defenseRecutPerimeter(&record, plan, perimeterBounds, policy.PerimeterBridge, nil, 0, true, nil); err != nil || !changed || outer() == 0 {
		t.Fatal("outer ring not added once open", changed, err, outer())
	}
	for _, tier := range record.Tiers {
		if policy.IsOuterPerimeterTier(tier.Name) && !strings.Contains(string(tier.Name), "-outer-") {
			t.Fatal("outer section misnamed", tier.Name)
		}
	}
	if changed, err := defenseRecutPerimeter(&record, plan, perimeterBounds, policy.PerimeterBridge, nil, 0, true, nil); err != nil || changed {
		t.Fatal("an unchanged open ring re-cut", changed, err)
	}
}
