package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestPermitCallsBecomeAbilityActions(t *testing.T) {
	orders := []policy.CombatOrder{
		{Pawn: "d1", Kind: policy.OrderAttack, Target: "h1"},
		{Pawn: "d1", Kind: policy.OrderPermit, Cell: domain.Cell{X: 4, Z: 5}, Faction: "Empire", Permit: "CallMilitaryAidSmall", Reason: policy.ReasonPermit},
	}
	rest, calls := splitPermitCalls(orders)
	if len(rest) != 1 || rest[0].Kind != policy.OrderAttack || len(calls) != 1 {
		t.Fatalf("rest %+v calls %+v", rest, calls)
	}
	actions, err := permitCallActions("plan-1", calls)
	if err != nil || len(actions) != 1 {
		t.Fatal(actions, err)
	}
	ability, ok := actions[0].Ability()
	if !ok || ability.Pawn() != "d1" || ability.Source().Def() != "CallMilitaryAidSmall" || ability.Target().Cell() != (domain.Cell{X: 4, Z: 5}) {
		t.Fatalf("ability %+v", ability)
	}
	if _, err = domain.NewPlan("plan-1", 1, actions); err != nil {
		t.Fatal(err)
	}
}

func TestPsycastCastsBecomeAbilityActions(t *testing.T) {
	orders := []policy.CombatOrder{
		{Pawn: "d1", Kind: policy.OrderCast, Permit: "Stun", Arm: policy.PsycastTargetPawn, Target: "h1", Reason: policy.ReasonCast},
		{Pawn: "d1", Kind: policy.OrderCast, Permit: "Flashstorm", Arm: policy.PsycastTargetCell, Cell: domain.Cell{X: 4, Z: 5}, Reason: policy.ReasonCast},
		{Pawn: "d2", Kind: policy.OrderCast, Permit: "Skipshield", Arm: policy.PsycastTargetSelf, Reason: policy.ReasonCast},
	}
	rest, calls := splitPermitCalls(orders)
	if len(rest) != 0 || len(calls) != 3 {
		t.Fatalf("rest %+v calls %+v", rest, calls)
	}
	actions, err := permitCallActions("plan-1", calls)
	if err != nil || len(actions) != 3 {
		t.Fatal(actions, err)
	}
	want := []domain.AbilityTargetKind{domain.AbilityTargetPawn, domain.AbilityTargetCell, domain.AbilityTargetNone}
	for i, action := range actions {
		ability, ok := action.Ability()
		if !ok || ability.Source().Kind() != domain.AbilityPsycast || ability.Source().Def() != orders[i].Permit || ability.Target().Kind() != want[i] {
			t.Fatalf("ability %d: %+v", i, ability)
		}
	}
	if _, err = permitCallActions("plan-1", []policy.CombatOrder{{Pawn: "d1", Kind: policy.OrderCast, Permit: "Stun"}}); err == nil {
		t.Fatal("a cast with no target arm built an action")
	}
}
