package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// completedStockpile commits one stockpile method on the fixture goal and
// observes its creation completed; zone is the identity the receipt named
// (empty for a completion recorded without one).
func completedStockpile(t *testing.T, zone string) (*Store, string, domain.GenerationSnapshot) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "zones.db")
	s := open(t, path)
	r := foodStorageDeficitRoutineRequest()
	g := routineGoal(t, reviewRoutine(t, s, &r), policy.EnsureFoodStorage)
	p := stockpilePlan(t, "storage-plan", []domain.Cell{{X: 3, Z: 7}})
	a := p.Actions()[0]
	current := r.Current
	current.Plan, current.Revision = p.ID(), p.Revision()
	preview := policy.Preview{Action: a, Snapshot: current, Tick: 10, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known([]domain.Cell{{X: 3, Z: 7}}), Costs: domain.Known([]policy.Amount{})}
	request := BuildingMethodRequest{Goal: g.Goal.ID, Revision: g.Revision, Method: "food-storage", Plan: p, Current: current, Tick: 10, Bounds: domain.Known(policy.Bounds{Width: 100, Height: 100}), Purpose: policy.Routine,
		Stock: policy.StockObservation{Snapshot: current, Tick: 10, Values: []policy.Stock{}}, Previews: []policy.Preview{preview}}
	d, err := s.AdmitBuildingMethod(ctx, request)
	if err != nil || !d.Admitted {
		t.Fatal(d, err)
	}
	if _, err = s.PrepareZone(ctx, p.ID(), a.ID(), ZoneAdmission{Snapshot: current, Tick: 10, SnapshotToken: "token"}); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, p.ID(), a.ID(), current, 10); err != nil {
		t.Fatal(err)
	}
	observed := domain.Observation{Action: a.ID(), Attempt: 1, Snapshot: current, Tick: 11, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch, Zone: zone}
	if _, err = s.Observe(ctx, p.ID(), observed, current); err != nil {
		t.Fatal(err)
	}
	return s, path, r.Current
}

// A stockpile claim carries the native zone identity the creation receipt
// returned, the form the Home coverage census names stockpiles by (#315);
// the action ID never matched a census row.
func TestStockpileClaimsCarryTheReceiptZoneIdentity(t *testing.T) {
	t.Parallel()
	s, path, current := completedStockpile(t, "Zone_7")
	s.Close()
	s = open(t, path)
	defer s.Close()
	got, err := s.StockpileClaims(context.Background(), current, 12)
	rows, known := got.Value()
	if err != nil || !known || len(rows) != 1 || rows[0].ID != "Zone_7" || len(rows[0].Cells) != 1 || rows[0].Cells[0] != (domain.Cell{X: 3, Z: 7}) {
		t.Fatal(rows, known, err)
	}
	if got, err = s.StockpileClaims(context.Background(), current, 10); err != nil {
		t.Fatal(err)
	} else if rows, known = got.Value(); !known || len(rows) != 0 {
		t.Fatal("a future completion owns a zone", rows)
	}
}

func TestStockpileClaimsIgnoreCompletionsWithoutZoneIdentity(t *testing.T) {
	t.Parallel()
	s, _, current := completedStockpile(t, "")
	got, err := s.StockpileClaims(context.Background(), current, 12)
	rows, known := got.Value()
	if err != nil || !known || len(rows) != 0 {
		t.Fatal(rows, known, err)
	}
}
