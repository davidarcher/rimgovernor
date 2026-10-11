package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestSurfaceMiningReachDemandAndSafety(t *testing.T) {
	safety := quietSafety()
	ore := ResourceSource{ThingID: "ore", Method: ResourceSourceMine, Yield: 40, Distance: 80,
		Cell: domain.Cell{X: 90, Z: 90}, Safety: "open_surface", Reachable: domain.Known(true)}
	for _, tt := range []struct {
		name  string
		stock int64
		edit  func(*ResourceSource, *RemoteSafety)
		want  int
	}{
		{"far deposit while demand open", 0, nil, 1},
		{"one unit needs at most one rock", 39, nil, 1},
		{"satisfied", 40, nil, 0},
		{"surplus", 50, nil, 0},
		{"mountain interior without safe route", 0, func(s *ResourceSource, _ *RemoteSafety) { s.Reachable = domain.Known(false) }, 0},
		{"unknown route", 0, func(s *ResourceSource, _ *RemoteSafety) { s.Reachable = domain.Unknown[bool]() }, 0},
		{"roof support", 0, func(s *ResourceSource, _ *RemoteSafety) { s.Safety = "roofed" }, 0},
		{"supported roof steel", 0, func(s *ResourceSource, _ *RemoteSafety) { s.Safety = MineSafetySupportedRoof }, 1},
		{"foreign designation covers demand", 0, func(s *ResourceSource, _ *RemoteSafety) { s.Designated = true }, 0},
		{"threat present", 0, func(_ *ResourceSource, r *RemoteSafety) { r.Threat = domain.Known(true) }, 0},
		{"threat unknown", 0, func(_ *ResourceSource, r *RemoteSafety) { r.Threat = domain.Unknown[bool]() }, 0},
	} {
		t.Run(tt.name, func(t *testing.T) {
			s, r := ore, safety
			if tt.edit != nil {
				tt.edit(&s, &r)
			}
			other := ore
			other.ThingID = "other"
			if tt.want == 0 && !s.Designated {
				other = s
				other.ThingID = "other"
			}
			got, _ := SelectReachableResourceSources([]ResourceSource{s, other}, 40, tt.stock, RemoteWorkRequest{Safety: r})
			if len(got) != tt.want {
				t.Fatalf("selected %v, want %d", got, tt.want)
			}
		})
	}
}

// A buried deposit native reports as supported is held as buried, never as
// a roof support risk.
func TestBuriedSourceHeldAsBuried(t *testing.T) {
	buried := ResourceSource{ThingID: "ore", Method: ResourceSourceMine, Buried: true, Yield: 40, Safety: "supported_roof", Distance: 5, Cell: domain.Cell{X: 20, Z: 20}, Reachable: domain.Known(false)}
	selected, holds := SelectReachableResourceSources([]ResourceSource{buried}, 200, 0, RemoteWorkRequest{Safety: quietSafety()})
	if len(selected) != 0 || len(holds) != 1 || holds[0].Reason != RemoteHoldBuried {
		t.Fatal(selected, holds)
	}
}

// The tunnel goes to the nearest buried deposit, and none while a threat holds.
func TestBuriedResourceSourcePicksNearest(t *testing.T) {
	far := ResourceSource{ThingID: "far", Method: ResourceSourceMine, Buried: true, Yield: 40, Safety: "supported_roof", Distance: 40}
	near := ResourceSource{ThingID: "near", Method: ResourceSourceMine, Buried: true, Yield: 40, Safety: "supported_roof", Distance: 9}
	r := RemoteWorkRequest{Safety: quietSafety()}
	if got, ok := BuriedResourceSource([]ResourceSource{far, near}, r); !ok || got.ThingID != "near" {
		t.Fatal(got, ok)
	}
	r.Safety.Threat = domain.Known(true)
	if got, ok := BuriedResourceSource([]ResourceSource{far, near}, r); ok {
		t.Fatal(got)
	}
}

// A solo colony has no armed or hauler surplus, yet scattered steel 40 cells
// from the colony is a candidate: the loot filter keeps it and the supply
// planner prices it by path distance, ranking the nearer stack first.
func TestSoloColonyScatteredSteelAt40CellsIsPricedByDistance(t *testing.T) {
	far := remoteLootRow("steel-far", domain.Cell{X: 60, Z: 20}, true, true)
	far.PathLength = domain.Known(40.0)
	near := remoteLootRow("steel-near", domain.Cell{X: 30, Z: 20}, true, true)
	near.PathLength = domain.Known(10.0)
	kept, holds, err := FilterLootReach(domain.Known([]LootItem{far, near}), RemoteWorkRequest{Safety: quietSafety(), Extent: tribal8Extent(), Demand: steelDemand()})
	if err != nil {
		t.Fatal(err)
	}
	rows, _ := kept.Value()
	if len(rows) != 2 || len(holds) != 0 {
		t.Fatalf("kept %v holds %v", rows, holds)
	}
	plan, err := PlanSupply(SupplyPlanRequest{
		Demands:    domain.Known([]SupplyDemand{{Good: ResourceKey{Def: "Steel"}, Units: 150, Priority: 2}}),
		Candidates: domain.Known([]SupplyCandidate{lootCandidate(far), lootCandidate(near)}),
		Labor:      domain.Known(1000.0),
	})
	if err != nil {
		t.Fatal(err)
	}
	if len(plan.Portfolio) != 2 || plan.Portfolio[0].Candidate.ID != "steel-near" || plan.Portfolio[1].Candidate.ID != "steel-far" ||
		plan.Portfolio[0].Score <= plan.Portfolio[1].Score {
		t.Fatal(plan.Explain())
	}
}
