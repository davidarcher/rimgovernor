package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestLootSafetyTracksBothDirections(t *testing.T) {
	h := EventLootHistory{}
	row := LootItem{SafetyKnown: true, Supply: StartingSupply{Thing: "steel", Definition: "Steel", Cell: domain.Cell{X: 80, Z: 90}}}
	for _, tc := range []struct{ forbidden, safe, want, forbid bool }{
		{true, false, false, false}, {false, false, true, true},
		{true, true, true, false}, {false, true, false, false},
		{true, true, true, false},
	} {
		row.Forbidden, row.SafeToHaul = tc.forbidden, tc.safe
		next, need, err := ReviewEventLoot(domain.Known([]LootItem{row}), h)
		if err != nil || need != domain.Known(tc.want) || (len(next.Pending) > 0) != tc.want {
			t.Fatal(next, need, err)
		}
		if tc.want && next.Pending[0].Forbid != tc.forbid {
			t.Fatal(next)
		}
		h = next
	}
	_, need, err := ReviewEventLoot(domain.Unknown[[]LootItem](), h)
	if err != nil || need != domain.Unknown[bool]() {
		t.Fatal(need, err)
	}
	if _, _, err := ReviewEventLoot(domain.Known([]LootItem{row, row}), h); err == nil {
		t.Fatal("duplicate accepted")
	}
}

func TestOnlyKnownUnsafeLootIsEmergencyPriority(t *testing.T) {
	for _, tc := range []struct {
		forbidden, safe, known bool
		priority               int
	}{
		{true, true, true, 2}, {false, false, true, 0}, {false, false, false, 2},
	} {
		f := RoutineFacts{EventLoot: domain.Known([]LootItem{{Forbidden: tc.forbidden, SafeToHaul: tc.safe, SafetyKnown: tc.known}})}
		if got := supplySafetyPriority(f); got != tc.priority {
			t.Fatalf("%+v: priority %d", tc, got)
		}
	}
	if supplySafetyPriority(RoutineFacts{}) != 4 {
		t.Fatal("unavailable safety must not hold unrelated work")
	}
}
