package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func dashReviewFacts() (RoundsFacts, domain.Fact[[]WorkPawn]) {
	f := dashCold("", 100)
	f.Dash = domain.Unknown[DashWindow]()
	f.Hostiles = domain.Known(int64(0))
	f.Items = ItemFacts{Market: map[Resource]float64{"Steel": 1.9}}
	f.EventLoot = domain.Known([]LootItem{{
		Supply: StartingSupply{Thing: "s1", Definition: "Steel", Cell: domain.Cell{X: 5, Z: 5}}, Forbidden: true, SafeToHaul: true, SafetyKnown: true,
		Count: 75, PathLength: knownF(120),
	}})
	return f, domain.Known([]WorkPawn{{ID: "a", Available: domain.Known(true), Body: dashBody(-10, 0, 4.6)}})
}

func TestReviewDashHoldsThenClosesForOneReviewAtTheBound(t *testing.T) {
	f, pawns := dashReviewFacts()
	planned, _ := ReviewDash(DashWindow{}, f, pawns, 100, PublishedHypothermiaTuning).Value()
	if planned.Pawn != "a" {
		t.Fatalf("%+v", planned)
	}
	// Before the bound the planned window is kept as planned, even when the
	// pawn's severity has since risen enough to plan a smaller one.
	pawns = domain.Known([]WorkPawn{{ID: "a", Available: domain.Known(true), Body: dashBody(-10, 0.3, 3)}})
	kept, _ := ReviewDash(planned, f, pawns, planned.Until-1, PublishedHypothermiaTuning).Value()
	if kept.Until != planned.Until || kept.Pawn != "a" {
		t.Fatalf("%+v", kept)
	}
	// At the bound it closes and plans nothing, so the pawn outside is recalled.
	closed, known := ReviewDash(planned, f, pawns, planned.Until, PublishedHypothermiaTuning).Value()
	if !known || closed.Pawn != "" {
		t.Fatalf("%v %+v", known, closed)
	}
	g := dashCold("", planned.Until)
	g.Dash = domain.Known(closed)
	if len(PlanSheltering(g)) == 0 {
		t.Fatal("a colonist outside at the bound must be sheltered")
	}
	// An unavailable pawn drops the window; so does a changed hazard.
	away := domain.Known([]WorkPawn{{ID: "a", Available: domain.Known(false), Body: dashBody(-10, 0, 4.6)}})
	if w, _ := ReviewDash(planned, f, away, 101, PublishedHypothermiaTuning).Value(); w.Pawn != "" {
		t.Fatal("window kept for an unavailable pawn")
	}
	f.DisasterConditions = domain.Known([]DisasterCondition{{ID: "c", Definition: ConditionToxicFallout}})
	if w, _ := ReviewDash(planned, f, pawns, 101, PublishedHypothermiaTuning).Value(); w.Pawn != "" {
		t.Fatal("window kept under fallout")
	}
}
