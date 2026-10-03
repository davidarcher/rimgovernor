package buildingruntime

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

// The lab-choke recording's stop 7 (#1035): raider Thing_Human7385 is
// panic-fleeing and bleeding (blood loss 0.02 at 4.8/day, 4.9 h to
// death), so below the population target the fight leaves it to go down:
// the formation asks no geometry about it and no role targets it, while
// the other fleeing raider, not bleeding, is still engaged.
func TestCombatFrameSparesFleeingBleeder(t *testing.T) {
	t.Parallel()
	stops, err := snap.CombatStops("testdata/combat/lab-choke.json.gz")
	if err != nil {
		t.Fatal(err)
	}
	s := stops[7]
	combat, err := bridge.DecodeCombat(s.Frame)
	if err != nil {
		t.Fatal(err)
	}
	in, _, err := combatFrameInputs(combat, nil)
	if err != nil {
		t.Fatal(err)
	}
	var layout domain.Fact[policy.CombatLayout]
	if s.Layout != nil {
		layout = domain.Known(*s.Layout)
	}
	view := combatView(combat, in, s.Orderable, layout)
	if pop, ok := view.Population.Value(); !ok || pop >= domain.PopulationTarget {
		t.Fatalf("population %v", view.Population)
	}
	_, ask, _ := policy.DecideCombat(view, policy.GeometryReply{}, s.Stop, s.MemoryIn)
	if ask == nil || !reflect.DeepEqual(ask.Hostiles, []domain.PawnID{"Thing_Human7424"}) {
		t.Fatalf("ask %+v", ask)
	}
	orders, _, memory := policy.DecideCombat(view, s.Reply, s.Stop, s.MemoryIn)
	for _, r := range memory.Roles {
		if r.Target == "Thing_Human7385" {
			t.Fatalf("role on the bleeder: %+v", r)
		}
	}
	if slices.ContainsFunc(orders, func(o policy.CombatOrder) bool { return o.Target == "Thing_Human7385" }) {
		t.Fatalf("order on the bleeder: %+v", orders)
	}
}
