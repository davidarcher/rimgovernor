package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// completedStockpile commits one stockpile method on the fixture goal and
// records its applied receipt; zone is the identity the receipt named
// (empty for a completion recorded without one).
func completedStockpile(t *testing.T, zone string) (*Store, string, domain.GenerationSnapshot) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "zones.db")
	s := open(t, path)
	r := secureSuppliesRoutineRequest()
	g := routineGoal(t, reviewRoutine(t, s, &r), policy.SecureSupplies)
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
	if _, err = s.Prepare(ctx, p.ID(), a.ID(), current, 11); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, p.ID(), a.ID(), current, 11); err != nil {
		t.Fatal(err)
	}
	if zone == "" {
		_, err = s.RecordReceipt(ctx, p.ID(), a.ID(), 1, domain.ReceiptAccepted)
	} else {
		_, err = s.RecordZoneReceipt(ctx, p.ID(), a.ID(), 1, zone)
	}
	if err != nil {
		t.Fatal(err)
	}
	return s, path, r.Current
}

// A zone claim carries the native zone identity the creation receipt
// returned, the form the Home coverage census names stockpiles by (#315);
// the action ID never matched a census row.
func TestZoneClaimsCarryTheReceiptZoneIdentity(t *testing.T) {
	t.Parallel()
	s, path, current := completedStockpile(t, "Zone_7")
	s.Close()
	s = open(t, path)
	defer s.Close()
	got, err := s.ZoneClaims(context.Background(), current, 12)
	rows, known := got.Value()
	if err != nil || !known || len(rows) != 1 || rows[0].ID != "Zone_7" || len(rows[0].Cells) != 1 || rows[0].Cells[0] != (domain.Cell{X: 3, Z: 7}) {
		t.Fatal(rows, known, err)
	}
	if got, err = s.ZoneClaims(context.Background(), current, 10); err != nil {
		t.Fatal(err)
	} else if rows, known = got.Value(); !known || len(rows) != 0 {
		t.Fatal("a future completion owns a zone", rows)
	}
}

func TestZoneClaimsIgnoreCompletionsWithoutZoneIdentity(t *testing.T) {
	t.Parallel()
	s, _, current := completedStockpile(t, "")
	got, err := s.ZoneClaims(context.Background(), current, 12)
	rows, known := got.Value()
	if err != nil || !known || len(rows) != 0 {
		t.Fatal(rows, known, err)
	}
}
