package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestRoutineTradeConstructionFloor(t *testing.T) {
	for _, tt := range []struct {
		name                                string
		reserve, construction, target, want int64
	}{
		{"minimum", 0, 0, 0, 500},
		{"reserve plus construction", 400, 350, 0, 750},
		{"construction only", 0, 800, 0, 800},
		{"target dominates", 400, 350, 900, 900},
	} {
		t.Run(tt.name, func(t *testing.T) {
			p := RoutinePolicy{ResourceReserves: map[Resource]int64{"Steel": tt.reserve}, ResourceTargets: map[Resource]int64{"Steel": tt.target}, Trade: RoutineTradePolicy{ItemWealthShare: 0.5}}
			floors := RoutineTradeFloors(p, map[string]int64{"Steel": tt.construction})
			if floors["Steel"] != tt.reserve+tt.construction {
				t.Fatalf("floor = %v", floors)
			}
			wealth := domain.Known(WealthFacts{Items: 30000, Total: 40000})
			got := WealthSurplus([]Amount{{Resource: "Steel", Count: 2000}}, p.ResourceTargets, floors, wealth, p.Trade)
			if len(got) != 1 || got[0].Count != 2000-tt.want {
				t.Fatalf("surplus = %v, retained want %d", got, tt.want)
			}
			if got := WealthSurplus([]Amount{{Resource: "Steel", Count: tt.want}}, p.ResourceTargets, floors, wealth, p.Trade); len(got) != 0 {
				t.Fatalf("sold at floor: %v", got)
			}
		})
	}
}
