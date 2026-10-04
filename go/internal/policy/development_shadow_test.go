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

func shadowRow(goal GoalID, reason DevelopmentReason) DevelopmentRow {
	return DevelopmentRow{Goal: goal, Reason: reason, Selected: reason == ""}
}

func TestShadowRankOrdersByShortfallPerAction(t *testing.T) {
	state := DevelopmentState{Rows: []DevelopmentRow{
		shadowRow(MaintainRefrigeration, ""),           // 2 days / 1 action = 2
		shadowRow(MaintainResource, ""),                // 4 / 4 = 1
		shadowRow(EnsureTemperatureSafety, ""),         // 5 / 1 = 5
		shadowRow(EnsureBasicDefense, ""),              // no shortfall in the projection
		shadowRow(MaintainFoodStorage, ""),             // no open action
		shadowRow(MaintainHousing, ""),                 // unmapped: left out
		shadowRow(MaintainWaste, DevelopmentCommitted), // already under way: left out
	}}
	open := map[GoalID]int{MaintainRefrigeration: 1, MaintainResource: 4, EnsureTemperatureSafety: 1, EnsureBasicDefense: 2}
	got := ShadowRankOf(state, shadowProjection(), open)
	var order []GoalID
	for _, e := range got.Ranked {
		order = append(order, e.Goal)
	}
	if want := []GoalID{EnsureTemperatureSafety, MaintainRefrigeration, MaintainResource}; !reflect.DeepEqual(order, want) {
		t.Fatal(order)
	}
	if want := []GoalID{MaintainRefrigeration, MaintainResource, EnsureTemperatureSafety}; !reflect.DeepEqual(got.Current, want) {
		t.Fatal(got.Current)
	}
	if len(got.Unranked) != 2 || got.Unranked[0].Goal != EnsureBasicDefense || got.Unranked[1].Goal != MaintainFoodStorage {
		t.Fatal(got.Unranked)
	}
	want := []ShadowDisagreement{{Goal: MaintainRefrigeration, Current: 1, Shadow: 2}, {Goal: MaintainResource, Current: 2, Shadow: 3}, {Goal: EnsureTemperatureSafety, Current: 3, Shadow: 1}}
	if !reflect.DeepEqual(got.Disagreements, want) {
		t.Fatal(got.Disagreements)
	}
}

func TestShadowRankUnknownProjectionIsLeftOut(t *testing.T) {
	state := DevelopmentState{Rows: []DevelopmentRow{shadowRow(MaintainResource, DevelopmentCapacity)}}
	p := shadowProjection()
	p.Food = domain.Unknown[FoodProjection]()
	got := ShadowRankOf(state, p, map[GoalID]int{MaintainResource: 1})
	if len(got.Ranked) != 0 || len(got.Unranked) != 1 || len(got.Disagreements) != 0 {
		t.Fatal(got)
	}
}

func TestShadowRankAgreeingOrderLogsNoDisagreement(t *testing.T) {
	state := DevelopmentState{Rows: []DevelopmentRow{shadowRow(EnsureTemperatureSafety, ""), shadowRow(MaintainRefrigeration, "")}}
	got := ShadowRankOf(state, shadowProjection(), map[GoalID]int{EnsureTemperatureSafety: 1, MaintainRefrigeration: 1})
	if len(got.Disagreements) != 0 || len(got.Ranked) != 2 {
		t.Fatal(got)
	}
}

// The ranker is shadow only: ranking the development state leaves the state
// admission reads (AdmitDevelopment) exactly as it was.
func TestShadowRankLeavesAdmissionUnchanged(t *testing.T) {
	state := DevelopmentState{Rows: []DevelopmentRow{
		{Goal: MaintainRefrigeration, Selected: true, Score: 3},
		{Goal: EnsureTemperatureSafety, Selected: true, Score: 1},
		{Goal: MaintainResource, Reason: DevelopmentCapacity},
	}, Committed: []GoalID{MaintainWaste}, Holds: []DevelopmentHold{{Goal: MaintainWaste}}}
	before := state
	before.Rows = append([]DevelopmentRow(nil), state.Rows...)
	admitted := func(s DevelopmentState) map[GoalID]bool {
		out := map[GoalID]bool{}
		for _, id := range []GoalID{MaintainRefrigeration, EnsureTemperatureSafety, MaintainResource, MaintainFoodStorage} {
			out[id] = AdmitDevelopment(s, id) == nil
		}
		return out
	}
	want := admitted(state)
	ShadowRankOf(state, shadowProjection(), map[GoalID]int{MaintainRefrigeration: 1, EnsureTemperatureSafety: 1, MaintainResource: 1})
	if !reflect.DeepEqual(state, before) || !reflect.DeepEqual(admitted(state), want) {
		t.Fatal("shadow ranker changed the development state")
	}
}

func TestForwardInputsOfUnknownFoodStaysUnknown(t *testing.T) {
	in := ForwardInputsOf(RoutineFacts{}, DefaultRoutinePolicy())
	if _, ok := ProjectForward(in).Food.Value(); ok {
		t.Fatal("no food supply must project unknown")
	}
}
