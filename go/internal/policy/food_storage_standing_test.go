package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestFoodStorageStandingIsAnOwnedFoodRoleZone(t *testing.T) {
	zones := func(counts ...StockpileRoleCount) RoundsFacts {
		return RoundsFacts{StockpileZones: domain.Known(counts)}
	}
	for _, tc := range []struct {
		name  string
		f     RoundsFacts
		known bool
		want  bool
	}{
		{"claims unread", RoundsFacts{}, false, false},
		{"no owned zones", zones(), true, false},
		{"warehouse and untagged only (a player food zone is not owned)", zones(StockpileRoleCount{Role: "general", Zones: 1}, StockpileRoleCount{Role: "untagged", Zones: 2}), true, false},
		{"meals", zones(StockpileRoleCount{Role: "meals", Zones: 1}), true, true},
		{"rawmeat", zones(StockpileRoleCount{Role: "rawmeat", Zones: 1}), true, true},
		{"perishables", zones(StockpileRoleCount{Role: "perishables", Zones: 1}), true, true},
		{"ingredients", zones(StockpileRoleCount{Role: "ingredients", Zones: 1}), true, true},
		{"food role with no zone left", zones(StockpileRoleCount{Role: "meals"}), true, false},
	} {
		got, known := FoodStorageStanding(tc.f).Value()
		if known != tc.known || got != tc.want {
			t.Errorf("%s: got (%v, %v), want (%v, %v)", tc.name, got, known, tc.want, tc.known)
		}
	}
}

func TestFoodStorageGateTimingFollowsTheOwnedStore(t *testing.T) {
	p := DefaultRoundsPolicy()
	general := domain.Known([]StockpileRoleCount{{Role: "general", Zones: 1}})
	meals := domain.Known([]StockpileRoleCount{{Role: "meals", Zones: 1}})
	f := RoundsFacts{Cooking: domain.Known(true), StockpileZones: general}
	// The foothold stage blocks on a missing owned food zone, not on an
	// unread census, and clears once the zone stands.
	stage := func(f RoundsFacts) ColonyStageRecord {
		facts := StageColonyFacts(RoundsFindings{}, f, p, nil)
		facts.Shelter, facts.FoodDays, facts.Armed = domain.Known(true), domain.Known(5.0), domain.Known(true)
		return ReviewColonyStage(ColonyStageRecord{}, facts, p.Stages(), 1)
	}
	if r := stage(f); r.Stage != StageFoothold || r.Blocker != StageBlockerFoodStorage {
		t.Fatalf("no owned food zone: %+v", r)
	}
	f.StockpileZones = domain.Unknown[[]StockpileRoleCount]()
	if r := stage(f); r.Stage != StageFoothold || r.Blocker != StageBlockerUnknown {
		t.Fatalf("unread claims: %+v", r)
	}
	f.StockpileZones = meals
	if r := stage(f); r.Blocker == StageBlockerFoodStorage || r.Blocker == StageBlockerUnknown {
		t.Fatalf("owned food zone: %+v", r)
	}
	// The disaster gate and the storage work follow the same fact.
	f.StockpileZones = general
	if got := DisasterServiceFacts(f, p)[DisasterStorage]; got != domain.Known(false) {
		t.Fatalf("disaster storage, no owned food zone: %v", got)
	}
	if foodStoragePriority(t, f, p) != 2 {
		t.Fatal("a missing owned food zone must raise storage work")
	}
	f.StockpileZones = domain.Unknown[[]StockpileRoleCount]()
	if got := DisasterServiceFacts(f, p)[DisasterStorage]; got != domain.Unknown[bool]() {
		t.Fatalf("disaster storage, unread claims: %v", got)
	}
	if foodStoragePriority(t, f, p) == 2 {
		t.Fatal("unread claims raised storage work")
	}
	f.StockpileZones = meals
	if got := DisasterServiceFacts(f, p)[DisasterStorage]; got != domain.Known(true) {
		t.Fatalf("disaster storage, owned meals zone: %v", got)
	}
	if foodStoragePriority(t, f, p) == 2 {
		t.Fatal("an owned food zone still raised storage work")
	}
}

func foodStoragePriority(t *testing.T, f RoundsFacts, p RoundsPolicy) int {
	t.Helper()
	r, err := InspectRounds(f, RoundsLatches{}, p)
	if err != nil {
		t.Fatal(err)
	}
	for _, n := range r.Assessments {
		if n.ID == MaintainFoodStorage {
			return n.Priority
		}
	}
	return -1
}
