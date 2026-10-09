package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func buildingAtPlan(t *testing.T, id, definition string, cells ...domain.Cell) domain.PlanSpec {
	t.Helper()
	var actions []domain.Action
	for i, c := range cells {
		b, err := domain.NewBuilding(definition, c, domain.North, "")
		if err != nil {
			t.Fatal(err)
		}
		a, err := domain.NewBuildingAction(domain.ActionID(id+"-"+string(rune('a'+i))), b, domain.TierExpand)
		if err != nil {
			t.Fatal(err)
		}
		actions = append(actions, a)
	}
	plan, err := domain.NewPlan(domain.PlanID(id), 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	return plan
}

// Building plans on disjoint cells coexist under one owner with no
// observation between them; a plan that touches an open plan's cell, or any
// non-building open work, still waits.
func TestCommitBuildingMethodsOnDisjointCellsStayOpenTogether(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := foodDeficitRoundsRequest()
	out := reviewRounds(t, s, &r)
	g := roundsGoal(t, out, policy.EnsureFoodSupply)
	first := buildingAtPlan(t, "wall-plan-1", "SunLamp", domain.Cell{X: 1, Z: 1}, domain.Cell{X: 2, Z: 1})
	g1, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "wall-1", first)
	if err != nil {
		t.Fatal(err)
	}
	second := buildingAtPlan(t, "wall-plan-2", "SunLamp", domain.Cell{X: 5, Z: 5})
	g2, err := s.CommitMethod(ctx, g1.Standard.ID, g1.Revision, "wall-2", second)
	if err != nil {
		t.Fatal("open building plan blocked a disjoint building plan", err)
	}
	overlap := buildingAtPlan(t, "wall-plan-3", "SunLamp", domain.Cell{X: 6, Z: 5}, domain.Cell{X: 2, Z: 1})
	if _, err = s.CommitMethod(ctx, g2.Standard.ID, g2.Revision, "wall-3", overlap); err == nil {
		t.Fatal("overlapping building plan was admitted beside open work")
	}
}
