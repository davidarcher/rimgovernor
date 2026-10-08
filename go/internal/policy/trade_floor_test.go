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
		{"unconsumed and unbuilt", 0, 0, 0},
		{"construction demand", 350, 0, 350},
		{"target dominates", 350, 900, 900},
	} {
		t.Run(tt.name, func(t *testing.T) {
			targets := map[Resource]int64{"Steel": tt.target}
			floors := RoundsTradeFloors(RoundsPolicy{}, map[string]int64{"Steel": tt.construction})
			if floors["Steel"] != tt.construction {
				t.Fatalf("floor = %v", floors)
			}
			retained := TradeRetained(domain.Known(ResourceConsumption{WindowDays: 15}), nil, map[Resource]int64{"Steel": tt.construction})
			wealth := domain.Known(WealthFacts{Items: 30000, Total: 40000})
			items := CoreItemFacts()
			got := WealthSurplus([]Amount{{Resource: "Steel", Count: 2000}}, items, targets, floors, retained, wealth)
			if len(got) != 1 || got[0].Count != 2000-tt.want {
				t.Fatalf("surplus = %v, retained want %d", got, tt.want)
			}
			if tt.want == 0 {
				return
			}
			if got := WealthSurplus([]Amount{{Resource: "Steel", Count: tt.want}}, items, targets, floors, retained, wealth); len(got) != 0 {
				t.Fatalf("sold at floor: %v", got)
			}
		})
	}
}
