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
