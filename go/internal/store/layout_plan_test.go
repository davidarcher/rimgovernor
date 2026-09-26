package store

import (
	"context"
	"path/filepath"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestLayoutPlanRoundTripsAndForgetsOnRewind(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	db, err := Open(ctx, filepath.Join(t.TempDir(), "state.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	w := extentWorld("colony", "load", 3)
	first := policy.LayoutPlan{
		Spine: []policy.SpineSegment{{From: domain.Cell{X: 10, Z: 20}, To: domain.Cell{X: 40, Z: 20}}},
		Rooms: []policy.LayoutRoom{{Role: policy.ModuleStorage, Interior: policy.Rectangle{X: 12, Z: 22, Width: 7, Height: 5}, Door: domain.Cell{X: 15, Z: 21}, DoorRot: domain.South, Dug: true}},
		Zones: []policy.LayoutZone{{Kind: policy.ZoneField, Runs: []policy.RowRun{{Z: 5, X: 3, Length: 9}}}},
		Reservations: []policy.LayoutReservation{
			{Kind: policy.ReserveTurbine, Area: policy.Rectangle{X: 50, Z: 50, Width: 5, Height: 2}, Pair: 1},
			{Kind: policy.ReserveMortar, Area: policy.Rectangle{X: 30, Z: 30, Width: 1, Height: 1}},
		},
	}
	grown := first
	grown.Rooms = append(append([]policy.LayoutRoom(nil), first.Rooms...), policy.LayoutRoom{Role: policy.ModuleHousing, Interior: policy.Rectangle{X: 20, Z: 22, Width: 4, Height: 4}, DoorRot: domain.North})
	if err := db.RecordLayoutPlan(ctx, w, 100, first); err != nil {
		t.Fatal(err)
	}
	if err := db.RecordLayoutPlan(ctx, w, 200, grown); err != nil {
		t.Fatal(err)
	}
	if r, ok, err := db.LayoutPlan(ctx, w, 250); err != nil || !ok || r.Tick != 200 || !reflect.DeepEqual(r.Plan, grown) {
		t.Fatal(r, ok, err)
	}
	// A same-load rewind past the replan forgets it.
	if r, ok, err := db.LayoutPlan(ctx, w, 150); err != nil || !ok || !reflect.DeepEqual(r.Plan, first) {
		t.Fatal(r, ok, err)
	}
	if r, ok, err := db.LayoutPlan(ctx, w, 250); err != nil || !ok || r.Tick != 100 {
		t.Fatal("the rewind kept the replan", r, ok, err)
	}
	if err := db.RecordLayoutPlan(ctx, w, 300, policy.LayoutPlan{}); err == nil {
		t.Fatal("recorded a plan without rooms")
	}
}
