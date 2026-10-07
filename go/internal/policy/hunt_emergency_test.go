package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A squad hunt is the hunt origin of ActiveCombat but not an emergency
// (#2175): the review's Emergency list stays empty, so the EmergencySafeguard
// admits armory, gear and butcher-bill work, while a real fight still vetoes it.
func TestSquadHuntIsNotAnEmergencyButRealCombatIs(t *testing.T) {
	rows := []AcquisitionSource{preyRow("a", 1, 1, 0.05), preyRow("b", 2, 2, 0.05), preyRow("c", 3, 3, 0.05)}
	squads := HuntCandidates(rows, 4, domain.Fact[float64]{})
	f := stableRounds()
	f.FoodPlan = domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{{Channel: squads[0], Decision: FoodPlanOpen}}})

	emergency := func(f RoundsFacts) []ConcernID {
		var out []ConcernID
		for _, a := range needs(t, f, RoundsLatches{}).All() {
			if EmergencyNeed(a) {
				out = append(out, a.ID)
			}
		}
		return out
	}
	hunt := needs(t, f, RoundsLatches{})
	if !assessedDeficit(hunt, ActiveCombat) {
		t.Fatal("the squad hunt no longer raises the ActiveCombat incident")
	}
	for _, a := range hunt.All() {
		if a.ID == ActiveCombat && len(a.Hunt) != 3 {
			t.Fatalf("hunt origin lost: %+v", a)
		}
	}
	if got := emergency(f); len(got) != 0 {
		t.Fatalf("squad hunt is an emergency: %v", got)
	}
	huntCtx := SafeguardContext{Enabled: true, Emergency: emergency(f)}
	for _, need := range []ConcernID{MaintainEquipment, EnsureCooking, MaintainResource, EnsureBasicDefense, EnsureFoodSupply} {
		for _, priority := range []int{2, 3, 4} {
			if got := VetoProposal(huntCtx, SafeguardProposal{Need: need, Priority: priority}); got != "" {
				t.Errorf("%s at priority %d vetoed during a squad hunt: %q", need, priority, got)
			}
		}
	}

	f.Hostiles = domain.Known(int64(2))
	fight := emergency(f)
	if len(fight) != 1 || fight[0] != ActiveCombat {
		t.Fatalf("real combat emergency = %v", fight)
	}
	fightCtx := SafeguardContext{Enabled: true, Emergency: fight}
	if got := VetoProposal(fightCtx, SafeguardProposal{Need: MaintainEquipment, Priority: 3}); got == "" {
		t.Error("real ActiveCombat no longer vetoes gear work")
	}
}
