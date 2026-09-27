package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Both stockpile write kinds persist through the zone payload column and
// take the building-patch admission record a zone deletion takes; a
// role-tagged filtered stockpile zone_create round-trips its filter and
// role, and a legacy preset row stays role-less.
func TestStockpileWriteActionsRoundTripAndAdmission(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "stockpile_write.db"))
	defer s.Close()
	edit, _ := domain.NewZoneCellEdit("Zone_7", "before-cas", domain.AddZoneCells, []domain.Cell{{X: 4, Z: 7}, {X: 3, Z: 7}})
	ea, _ := domain.NewZoneCellEditAction("edit", edit)
	filter, _ := domain.NewStockpileFilter(domain.BaseEverything, []domain.FilterSelector{domain.ThingDef("Steel")}, []domain.FilterSelector{domain.SpecialFilter("AllowRotten")})
	filter, _ = filter.WithQuality("Normal", "Legendary")
	patch, _ := domain.NewStockpilePatch(domain.StorageBuildingTarget, "Shelf_9", "storage-cas", filter, domain.CriticalPriority, "shelf:Shelf_9")
	pa, _ := domain.NewStockpilePatchAction("patch", patch)
	zone, _ := domain.NewFilteredStockpileZone(filter, domain.LowPriority, []domain.Cell{{X: 1, Z: 1}})
	zone, _ = zone.WithRole("dump:worn")
	za, _ := domain.NewZoneCreateAction("zone", zone)
	legacy, _ := domain.NewFilteredStockpileZone(domain.GeneralFilter(), domain.NormalPriority, []domain.Cell{{X: 1, Z: 3}})
	la, _ := domain.NewZoneCreateAction("legacy", legacy)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{ea, pa, za, la})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil {
		t.Fatal(err)
	}
	actions := state.Spec.Actions()
	if got, ok := actions[0].ZoneCellEdit(); !ok || got != edit {
		t.Fatal("zone cell edit did not round trip", actions[0])
	}
	if got, ok := actions[1].StockpilePatch(); !ok || got != patch {
		t.Fatal("stockpile patch did not round trip", actions[1])
	}
	if got, ok := actions[2].ZoneCreate(); !ok || got != zone || got.Role() != "dump:worn" {
		t.Fatal("filtered zone did not round trip", actions[2])
	}
	if got, ok := actions[3].ZoneCreate(); !ok || got != legacy || got.Role() != "" {
		t.Fatal("legacy zone did not round trip", actions[3])
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
	for _, a := range []struct{ id, thing, token string }{{"edit", "Zone_7", "before-cas"}, {"patch", "Shelf_9", "storage-cas"}} {
		v := BuildingTemperatureAdmission{Snapshot: snapshot, Tick: 12, Thing: a.thing, SnapshotToken: a.token}
		if _, err := s.Prepare(ctx, "plan", domain.ActionID(a.id), v.Snapshot, v.Tick); err == nil {
			t.Fatal("generic prepare accepted", a.id)
		}
		if _, err := s.PrepareBuildingTemperature(ctx, "plan", domain.ActionID(a.id), v); err != nil {
			t.Fatal(a.id, err)
		}
	}
}
