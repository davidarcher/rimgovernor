package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func remoteSteel(id string, count int64) SupplyCandidate {
	return SourceCandidate(CandidateLoot, id, domain.Known(0.0), domain.Known(10.0), true, lootUnitsPerTrip,
		SourceYield(ResourceKey{Def: "Steel"}, count, 0, domain.Known(int64(500))))
}

// The plan opens the best stack first, still admits a stack a better one
// already covers the deficit for, and holds one nothing wants or urgent work
// outranks, with the stable demand words.
func TestPlanRemoteSupplyOpensBestAndHoldsWithDemandWords(t *testing.T) {
	demand := domain.Known([]ResourceDemand{{Key: ResourceKey{Def: "Steel"}, Count: 60, Priority: 2}})
	near, far := remoteSteel("near", 60), remoteSteel("far", 60)
	far.PathDistance = domain.Known(50.0)
	other := remoteSteel("other", 60)
	other.Yields[0].Good = ResourceKey{Def: "WoodLog"}
	unknown := remoteSteel("unknown", 60)
	unknown.PathDistance = domain.Unknown[float64]()

	got, err := PlanRemoteSupply(RemoteWorkRequest{Demand: demand}, []SupplyCandidate{far, near, other, unknown})
	if err != nil || len(got.Opened) != 1 || got.Opened[0] != "near" {
		t.Fatalf("opened %v %v", got, err)
	}
	if got.Held["other"] != "no_demand" || got.Held["unknown"] != "unknown_demand_or_cost" || len(got.Held) != 2 {
		t.Fatalf("held %v", got.Held)
	}
	if _, covered := got.Held["far"]; covered {
		t.Fatal("a stack the better one covers the deficit for must stay admissible")
	}
	urgent, err := PlanRemoteSupply(RemoteWorkRequest{Demand: demand, Competition: AcquisitionCompetition{UrgentPriority: UrgentWorkPriority}}, []SupplyCandidate{near})
	if err != nil || len(urgent.Opened) != 0 || urgent.Held["near"] != "competing_urgent_work" {
		t.Fatalf("urgent %v %v", urgent, err)
	}
	blind, err := PlanRemoteSupply(RemoteWorkRequest{Demand: domain.Unknown[[]ResourceDemand]()}, []SupplyCandidate{near})
	if err != nil || blind.Held["near"] != "unknown_demand_or_cost" {
		t.Fatalf("blind %v %v", blind, err)
	}
}
