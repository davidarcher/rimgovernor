package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func shadowProjection() ForwardProjection {
	return ForwardProjection{
		HorizonDays: ProjectionHorizonDays,
		Food:        domain.Known(FoodProjection{RunwayDays: 1, ShortfallDays: 4}),
		Power:       domain.Known(PowerProjection{ShortfallDays: 2}),
		Temperature: domain.Known(TemperatureProjection{BreachDays: 5}),
		Defense:     domain.Known(DefenseProjection{Gaps: []string{"raid arrival"}}),
	}
}

func shadowRow(goal ConcernID, reason DevelopmentReason) DevelopmentRow {
	return DevelopmentRow{Concern: goal, Reason: reason, Selected: reason == ""}
}

func TestShadowRankOrdersByShortfallPerAction(t *testing.T) {
	state := DevelopmentState{Rows: []DevelopmentRow{
		shadowRow(MaintainRefrigeration, ""),                  // 2 days / 1 action = 2
		shadowRow(MaintainResource, ""),                       // 4 / 4 = 1
		shadowRow(EnsureTemperatureSafety, ""),                // 5 / 1 = 5
		shadowRow(EnsureBasicDefense, ""),                     // no shortfall in the projection
		shadowRow(MaintainFoodStorage, ""),                    // no open action
		shadowRow(MaintainPsylink, ""),                        // unmapped: left out
		shadowRow(MaintainIncineration, DevelopmentCommitted), // already under way: left out
	}}
	open := map[ConcernID]int{MaintainRefrigeration: 1, MaintainResource: 4, EnsureTemperatureSafety: 1, EnsureBasicDefense: 2}
	got := ShadowRankOf(state.Rows, shadowProjection(), open)
	var order []ConcernID
	for _, e := range got.Ranked {
		order = append(order, e.Concern)
	}
	if want := []ConcernID{EnsureTemperatureSafety, MaintainRefrigeration, MaintainResource}; !reflect.DeepEqual(order, want) {
		t.Fatal(order)
	}
	if len(got.Unranked) != 2 || got.Unranked[0].Concern != EnsureBasicDefense || got.Unranked[1].Concern != MaintainFoodStorage {
		t.Fatal(got.Unranked)
	}
}

func TestShadowRankUnknownProjectionIsLeftOut(t *testing.T) {
	state := DevelopmentState{Rows: []DevelopmentRow{shadowRow(MaintainResource, DevelopmentCapacity)}}
	p := shadowProjection()
	p.Food = domain.Unknown[FoodProjection]()
	got := ShadowRankOf(state.Rows, p, map[ConcernID]int{MaintainResource: 1})
	if len(got.Ranked) != 0 || len(got.Unranked) != 1 {
		t.Fatal(got)
	}
}

func TestForwardInputsOfUnknownFoodStaysUnknown(t *testing.T) {
	in := ForwardInputsOf(RoundsFacts{}, DefaultRoundsPolicy())
	if _, ok := ProjectForward(in).Food.Value(); ok {
		t.Fatal("no food supply must project unknown")
	}
}
