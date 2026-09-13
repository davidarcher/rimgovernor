package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestAutonomousConstructionClaimsSurviveRetirementAndManual(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "ownership.db")
	s := open(t, path)
	request := routineRequest()
	request.Facts.Colonists = domain.Known(int64(3))
	request.Facts.BedCapacity = domain.Known(int64(0))
	request.Facts.IndoorCapacity = domain.Known(int64(0))
	g := routineGoal(t, reviewRoutine(t, s, &request), policy.EnsureInitialShelter)
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
	if _, err = s.Observe(ctx, "method", domain.Observation{Action: "placed", Attempt: 1, Snapshot: scope, Tick: request.Tick, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch, Construction: &domain.ConstructionIdentity{Origin: "blueprint", Current: "wall"}}, scope); err != nil {
		t.Fatal(err)
	}
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
	if err != nil || !known || len(rows) != 1 || rows[0].Identity.Current != "wall" || rows[0].Action != "placed" {
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
