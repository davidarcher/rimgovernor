package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestRoundsTradeConstructionFloor(t *testing.T) {
	for _, tt := range []struct {
		name                       string
		construction, target, want int64
	}{
		{"minimum", 0, 0, 500},
		{"construction below minimum", 350, 0, 500},
		{"construction only", 800, 0, 800},
		{"target dominates", 350, 900, 900},
	} {
		t.Run(tt.name, func(t *testing.T) {
			p := RoundsPolicy{ResourceTargets: map[Resource]int64{"Steel": tt.target}, Trade: RoundsTradePolicy{}}
			floors := RoundsTradeFloors(p, map[string]int64{"Steel": tt.construction})
			if floors["Steel"] != tt.construction {
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
