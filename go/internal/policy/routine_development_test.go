package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestRoutineDevelopmentNativeFractionsAndWorkers(t *testing.T) {
	p := DefaultRoutinePolicy()
	f := RoutineFacts{Colonists: domain.Known(int64(3)), Armed: domain.Known(int64(1)), Wood: domain.Known(int64(175))}
	for _, id := range []GoalID{MaintainWood, EnsureBasicDefense} {
		v, k := RoutineDevelopmentDeficit(id, f, p).Value()
		if !k || v != .5 {
			t.Fatal(id, v, k)
		}
	}
	f.Armed = domain.Unknown[int64]()
	if _, k := RoutineDevelopmentDeficit(EnsureBasicDefense, f, p).Value(); k {
		t.Fatal("unknown equipment became a deficit")
	}
	pawns := []WorkPawn{{Available: domain.Known(true), Applies: domain.Known(true)}, {Available: domain.Known(false)}}
	if v, k := RoutineWorkers(pawns).Value(); !k || v != 1 {
		t.Fatal(v, k)
	}
	pawns = append(pawns, WorkPawn{Applies: domain.Known(true)})
	if _, k := RoutineWorkers(pawns).Value(); k {
		t.Fatal("missing availability increased capacity")
	}
}

func TestRoutineDevelopmentDeficitDefensiveLayoutFollowsOptIn(t *testing.T) {
	if _, known := RoutineDevelopmentDeficit(EnsureDefensiveLayout, RoutineFacts{}, RoutinePolicy{}).Value(); known {
		t.Fatal("opted-out layout must rank deficit_unknown")
	}
	if v, known := RoutineDevelopmentDeficit(EnsureDefensiveLayout, RoutineFacts{}, RoutinePolicy{DefensiveLayout: true}).Value(); !known || v != 1 {
		t.Fatalf("opted-in layout deficit = %v,%v; want 1,true", v, known)
	}
}
