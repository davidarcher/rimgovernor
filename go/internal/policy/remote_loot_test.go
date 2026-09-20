package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func remoteLootRow(thing string, cell domain.Cell, forbidden, safe bool) LootItem {
	return LootItem{Supply: StartingSupply{Thing: thing, Definition: "Steel", Cell: cell}, Forbidden: forbidden, SafeToHaul: safe, SafetyKnown: true,
		Count: 75, PathLength: domain.Known(120.0), StorageHeadroom: domain.Known(int64(300))}
}

func steelDemand() domain.Fact[[]ResourceDemand] {
	return domain.Known([]ResourceDemand{{Key: ResourceKey{Def: "Steel"}, Count: 200, Priority: 2}})
}

func TestRemoteLootFollowsReachStage(t *testing.T) {
	remote := domain.Cell{X: 95, Z: 95}
	for _, tt := range []struct {
		name   string
		edit   func(*ResourceReachRequest)
		kept   bool
		reason string
	}{
		{"base holds remote", func(r *ResourceReachRequest) { r.Armed = domain.Known(int64(1)) }, false, "outside_base:insufficient_defense"},
		{"near holds remote", func(r *ResourceReachRequest) {}, false, "outside_near:defense_limits_near"},
		{"far allows remote", func(r *ResourceReachRequest) {
			r.Armed = domain.Known(int64(6))
			r.FreeHaulers = domain.Known(int64(2))
		}, true, ""},
		{"threat collapses", func(r *ResourceReachRequest) {
			r.Armed = domain.Known(int64(6))
			r.FreeHaulers = domain.Known(int64(2))
			r.Threat = domain.Known(true)
		}, false, "threat_present"},
	} {
		t.Run(tt.name, func(t *testing.T) {
			r := tribal8Reach()
			tt.edit(&r)
			rows, holds, err := FilterLootReach(domain.Known([]LootItem{remoteLootRow("steel-1", remote, true, true)}), RemoteWorkRequest{Reach: r, Demand: steelDemand()})
			if err != nil {
				t.Fatal(err)
			}
			kept, _ := rows.Value()
			if (len(kept) == 1) != tt.kept || (len(holds) == 1) != !tt.kept {
				t.Fatalf("kept %v holds %v", kept, holds)
			}
			if !tt.kept && holds[0].Reason != tt.reason {
				t.Fatalf("reason %q", holds[0].Reason)
			}
		})
	}
}

func TestRemoteLootKeepsSafetySemantics(t *testing.T) {
	r := tribal8Reach()
	r.Armed = domain.Known(int64(1))
	remote := domain.Cell{X: 95, Z: 95}
	rows := []LootItem{
		remoteLootRow("unsafe-allowed", remote, false, true),
		remoteLootRow("unsafe-forbidden", remote, true, false),
		remoteLootRow("safe-allowed", remote, false, true),
		remoteLootRow("inside-extent", domain.Cell{X: 20, Z: 20}, true, true),
		remoteLootRow("remote-forbidden", remote, true, true),
	}
	rows[0].SafeToHaul = false
	kept, holds, err := FilterLootReach(domain.Known(rows), RemoteWorkRequest{Reach: r, Demand: steelDemand()})
	if err != nil {
		t.Fatal(err)
	}
	got, _ := kept.Value()
	if len(got) != 4 || len(holds) != 1 || holds[0].Thing != "remote-forbidden" {
		t.Fatalf("kept %v holds %v", got, holds)
	}
	next, need, err := ReviewEventLoot(kept, EventLootHistory{})
	if err != nil || need != domain.Known(true) || len(next.Pending) != 2 {
		t.Fatal(next, need, err)
	}
	if next.Pending[0].Thing != "inside-extent" || next.Pending[0].Forbid || next.Pending[1].Thing != "unsafe-allowed" || !next.Pending[1].Forbid {
		t.Fatal(next.Pending)
	}
	next.Held = holds
	if err := next.Validate(); err != nil {
		t.Fatal(err)
	}
	if _, _, err := FilterLootReach(domain.Unknown[[]LootItem](), RemoteWorkRequest{Reach: r}); err != nil {
		t.Fatal(err)
	}
}

func TestRemoteLootScoresAgainstDemand(t *testing.T) {
	r := tribal8Reach()
	r.Armed = domain.Known(int64(6))
	r.FreeHaulers = domain.Known(int64(2))
	remote := domain.Cell{X: 95, Z: 95}
	for _, tt := range []struct {
		name   string
		row    LootItem
		demand domain.Fact[[]ResourceDemand]
		reason string
	}{
		{"unknown demand", remoteLootRow("s", remote, true, true), domain.Unknown[[]ResourceDemand](), "demand:unknown_demand_or_cost"},
		{"no demand", remoteLootRow("s", remote, true, true), domain.Known([]ResourceDemand{}), "demand:no_demand"},
		{"other demand", remoteLootRow("s", remote, true, true), domain.Known([]ResourceDemand{{Key: ResourceKey{Def: "WoodLog"}, Count: 50, Priority: 2}}), "demand:no_demand"},
		{"no storage", func() LootItem {
			row := remoteLootRow("s", remote, true, true)
			row.StorageHeadroom = domain.Known(int64(0))
			return row
		}(), steelDemand(), "missing_storage"},
		{"unknown path", func() LootItem {
			row := remoteLootRow("s", remote, true, true)
			row.PathLength = domain.Unknown[float64]()
			return row
		}(), steelDemand(), "demand:unknown_demand_or_cost"},
		{"demanded", remoteLootRow("s", remote, true, true), steelDemand(), ""},
	} {
		t.Run(tt.name, func(t *testing.T) {
			kept, holds, err := FilterLootReach(domain.Known([]LootItem{tt.row}), RemoteWorkRequest{Reach: r, Demand: tt.demand})
			if err != nil {
				t.Fatal(err)
			}
			rows, _ := kept.Value()
			if tt.reason == "" {
				if len(rows) != 1 || len(holds) != 0 {
					t.Fatalf("kept %v holds %v", rows, holds)
				}
				return
			}
			if len(rows) != 0 || len(holds) != 1 || holds[0].Reason != tt.reason {
				t.Fatalf("kept %v holds %v", rows, holds)
			}
		})
	}
}

func TestLootDemandAndReachFromFacts(t *testing.T) {
	p := DefaultRoutinePolicy()
	p.ResourceTargets = map[Resource]int64{"Steel": 300}
	p.ResourceReserves = map[Resource]int64{"WoodLog": 100}
	f := RoutineFacts{Resources: domain.Known([]Amount{{Resource: "Steel", Count: 120}, {Resource: "WoodLog", Count: 500}})}
	demand, err := LootDemand(p, f)
	if err != nil {
		t.Fatal(err)
	}
	rows, known := demand.Value()
	if !known || len(rows) != 1 || rows[0].Key.Def != "Steel" || rows[0].Count != 180 {
		t.Fatal(rows, known)
	}
	if demand, err = LootDemand(p, RoutineFacts{}); err != nil {
		t.Fatal(err)
	} else if _, known = demand.Value(); known {
		t.Fatal("unknown stock certified demand")
	}
	f.Hostiles = domain.Known(int64(0))
	f.Armed = domain.Known(int64(6))
	f.RaidPoints = domain.Known(100.0)
	f.LootReadiness = LootReadiness{FreeHaulers: domain.Known(int64(2)), StorytellerQuiet: domain.Known(true)}
	f.EventLoot = domain.Known([]LootItem{remoteLootRow("s", domain.Cell{X: 95, Z: 95}, true, true)})
	extent := tribal8Reach().Extent
	r := LootReach(f, domain.Known(Bounds{Width: 100, Height: 100}), extent)
	if d := ResourceReach(r); d.Stage != ResourceReachFar {
		t.Fatal(d)
	}
	f.Hostiles = domain.Known(int64(1))
	if d := ResourceReach(LootReach(f, domain.Known(Bounds{Width: 100, Height: 100}), extent)); d.Reason != "threat_present" {
		t.Fatal(d)
	}
	f.Hostiles = domain.Unknown[int64]()
	f.EventLoot = domain.Known([]LootItem{})
	r = LootReach(f, domain.Known(Bounds{Width: 100, Height: 100}), extent)
	if _, known := r.Threat.Value(); known {
		t.Fatal("unknown hostiles became a threat verdict")
	}
	if headroom, known := r.StorageHeadroom.Value(); !known || headroom != 0 {
		t.Fatal(r.StorageHeadroom)
	}
}
