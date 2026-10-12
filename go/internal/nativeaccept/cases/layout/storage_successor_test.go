package layout

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestStockFaultParses(t *testing.T) {
	for _, n := range []int{storageSeed, storageLever} {
		entry := stockFault(n)
		value, ok := strings.CutPrefix(entry, buildingruntime.FaultsEnv+"=")
		if !ok {
			t.Fatalf("%q is not a %s entry", entry, buildingruntime.FaultsEnv)
		}
		f, err := buildingruntime.ParseFaults(value)
		if err != nil || f.StockDemand[storageStockDef] != int64(n) {
			t.Fatalf("%q parsed to %+v, %v", entry, f, err)
		}
	}
}

func TestSuccessorOfAndInPlan(t *testing.T) {
	plan := policy.LayoutPlan{Rooms: []policy.PlannedRoom{
		{ID: 1, Role: policy.PlannedStorage, Region: rect(0, 0, 3, 3)},
		{ID: 7, Role: policy.PlannedStorage, Replaces: 1, Region: rect(10, 0, 5, 3)},
	}}
	got, ok := successorOf(plan, 1)
	if !ok || got.ID != 7 {
		t.Fatalf("successorOf = %+v, %v", got, ok)
	}
	if _, ok := successorOf(plan, 2); ok {
		t.Fatal("room 2 has no successor")
	}
	if !inPlan(plan, 1) || inPlan(plan, 9) {
		t.Fatal("inPlan wrong")
	}
}

func TestNeverAbsent(t *testing.T) {
	if err := (stockMap{}).neverAbsent(); err != nil {
		t.Fatal(err)
	}
	if err := (stockMap{{X: 1, Z: 1}: ""}).neverAbsent(); err == nil {
		t.Fatal("stock under no zone passed")
	}
	if err := (stockMap{{X: 1, Z: 1}: "", {X: 2, Z: 1}: "z1"}).neverAbsent(); err != nil {
		t.Fatal(err)
	}
}

func TestHandoff(t *testing.T) {
	succ := rect(10, 0, 3, 3)
	old := zoneRead{ID: "z1", Priority: "Low", Allowed: []string{"Steel"}, Cells: rect(0, 0, 3, 3).Cells()}
	fresh := zoneRead{ID: "z2", Priority: "Low", Allowed: []string{"Steel"}, Cells: succ.Cells()}
	stock := stockMap{{X: 10, Z: 0}: "z2"}
	if _, failed := handoff(old, []zoneRead{fresh}, succ, stock); len(failed) != 0 {
		t.Fatalf("a finished hand-off failed: %v", failed)
	}
	if _, failed := handoff(old, []zoneRead{old, fresh}, succ, stock); len(failed) != 1 || !strings.Contains(failed[0], "old zone") {
		t.Fatalf("a standing old zone: %v", failed)
	}
	wrong := fresh
	wrong.Priority = "Normal"
	if _, failed := handoff(old, []zoneRead{wrong}, succ, stock); len(failed) != 1 || !strings.Contains(failed[0], "priority") {
		t.Fatalf("changed priority: %v", failed)
	}
	if _, failed := handoff(old, []zoneRead{fresh}, succ, stockMap{{X: 1, Z: 1}: "z1"}); len(failed) != 1 || !strings.Contains(failed[0], "not the new zone") {
		t.Fatalf("stock left in the old zone: %v", failed)
	}
	if _, failed := handoff(old, nil, succ, stock); len(failed) < 2 {
		t.Fatalf("no zones: %v", failed)
	}
}

func TestStockIn(t *testing.T) {
	zones := []zoneRead{{ID: "z1", Cells: []domain.Cell{{X: 0, Z: 0}}}}
	held := map[domain.Cell]bool{{X: 0, Z: 0}: true, {X: 1, Z: 0}: true, {X: 50, Z: 50}: true}
	got := stockIn(held, zones, rect(0, 0, 3, 3))
	if len(got) != 2 || got[domain.Cell{X: 0, Z: 0}] != "z1" || got[domain.Cell{X: 1, Z: 0}] != "" {
		t.Fatalf("stockIn = %v", got)
	}
}

func TestStorageSuccessorIsRegisteredAndLints(t *testing.T) {
	c, ok := cases.Lookup("layout/storage-successor")
	if !ok {
		t.Fatal("layout/storage-successor is not registered")
	}
	if err := c.Lint(); err != nil {
		t.Fatal(err)
	}
}
