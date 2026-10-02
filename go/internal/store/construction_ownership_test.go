package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// builtCensus is a complete census holding the plan() helper's wall.
func builtCensus(t *testing.T, id string) domain.Fact[policy.CurrentConstruction] {
	t.Helper()
	b, err := domain.NewBuilding("Modded_Wall", domain.Cell{X: 3, Z: 7}, domain.East, "GraniteBlocks")
	if err != nil {
		t.Fatal(err)
	}
	return domain.Known(policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{{ID: id, Building: b, Cells: []domain.Cell{b.Cell()}}}})
}

func TestAutonomousConstructionClaimsSurviveRetirementAndManual(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	request := routineRequest()
	request.Facts.Colonists = domain.Known(int64(3))
	request.Facts.BedCapacity = domain.Known(int64(0))
	request.Facts.IndoorCapacity = domain.Known(int64(0))
	g := routineGoal(t, reviewRoutine(t, s, &request), policy.MaintainHousing)
	if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "build", plan(t, "method", "placed")); err != nil {
		t.Fatal(err)
	}
	scope := request.Current
	scope.Plan = "method"
	if _, err := s.Prepare(ctx, "method", "placed", scope, request.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "method", "placed", scope, request.Tick); err != nil {
		t.Fatal(err)
	}
	before, err := s.ConstructionClaims(ctx, request.Current, request.Tick)
	rows, known := before.Value()
	if err != nil || !known || len(rows) != 0 {
		t.Fatal("dispatch conferred ownership", rows, err)
	}
	request.Tick++
	if _, err = s.RecordReceipt(ctx, "method", "placed", 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	// Retirement reads the census: the intent's building stands built (#856).
	request.Facts.CurrentConstruction = builtCensus(t, "wall")
	reviewRoutine(t, s, &request)
	retired, err := s.LoadPlan(ctx, "method")
	if err != nil || !retired.Retired {
		t.Fatal("fixture method not retired", err)
	}
	request.Enabled = false
	reviewRoutine(t, s, &request)
	s.Close()
	s = open(t, path)
	defer s.Close()
	claims, err := s.ConstructionClaims(ctx, request.Current, request.Tick)
	rows, known = claims.Value()
	if err != nil || !known || len(rows) != 1 || rows[0].Action != "placed" {
		t.Fatal(rows, known, err)
	}
	other := request.Current
	other.Load = "replacement"
	claims, err = s.ConstructionClaims(ctx, other, request.Tick)
	rows, known = claims.Value()
	if err != nil || !known || len(rows) != 0 {
		t.Fatal("ownership crossed load", rows, err)
	}
}
