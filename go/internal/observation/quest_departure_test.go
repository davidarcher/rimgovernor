package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"testing"
)

func TestDepartureStrengthUsesSameObservedDefenseContributions(t *testing.T) {
	candidates := domain.Known([]policy.QuestDeparturePawn{{ID: "a"}, {ID: "missing"}})
	defenders := domain.Known([]policy.SquadDefenderFacts{{ID: "a", MeleePower: domain.Known(2.0), RangedDPS: domain.Known(8.0)}})
	rows, known := projectDepartureStrength(candidates, defenders).Value()
	if !known {
		t.Fatal("lost eligibility")
	}
	if points, known := rows[0].DefensePoints.Value(); !known || points != 50 {
		t.Fatal(points, known)
	}
	if _, known := rows[1].DefensePoints.Value(); known {
		t.Fatal("invented missing combat contribution")
	}
	original, _ := candidates.Value()
	if _, known := original[0].DefensePoints.Value(); known {
		t.Fatal("mutated input census")
	}
}
