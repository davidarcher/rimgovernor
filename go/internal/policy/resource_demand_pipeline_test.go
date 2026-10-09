package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// demandFacts has one entry per demand source of ResourceDemandOf.
func demandFacts() RoundsFacts {
	f := stableRounds()
	f.Resources = domain.Known([]Amount{{Resource: "Steel", Count: 10}})
	f.ConstructionDeficit = domain.Known(map[Resource]int64{"Steel": 60})
	f.OpenBills = []OpenBill{{Recipe: "Make_Parka", Count: 2, Slots: domain.Known([][]Amount{{{Resource: "Cloth", Count: 5}}})}}
	f.ResourceNeeds = map[Resource]int64{"Gold": 7}
	f.ResourceConsumption = domain.Known(ResourceConsumption{WindowDays: 15})
	f.ResourceRunways = []ResourceRunway{{
		Resource: "MedicineIndustrial", Reserve: 5, Target: 30,
		ConsumptionPerDay: domain.Known(2.0), Deficit: domain.Known(true),
	}}
	return f
}

// The review's detectors and every planner read one value: the findings
// carry exactly what ResourceDemandOf computes for the review's facts and
// latches, with every source in it.
func TestInspectRoundsPublishesResourceDemandOfTheSameFacts(t *testing.T) {
	p, f := DefaultRoundsPolicy(), demandFacts()
	findings, err := InspectRounds(f, RoundsLatches{}, p)
	if err != nil {
		t.Fatal(err)
	}
	want, err := ResourceDemandOf(f, p, findings.Latches)
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(findings.ResourceDemand, want) {
		t.Fatalf("detector demand %+v, pipeline %+v", findings.ResourceDemand, want)
	}
	for resource, n := range map[Resource]int64{"Steel": 60, "Cloth": 10, "Gold": 7, "MedicineIndustrial": 30} {
		if got := want.Needs[resource]; got != n {
			t.Fatalf("%s demand %d, want %d in %v", resource, got, n, want.Needs)
		}
	}
}

// The trade detector and the trade planner retain the same stock: the
// runway's protected line plus the works' demand, the runway's own target
// counted once and an evidence need not at all.
func TestResourceDemandRetainedIsTheTradeRetainedStock(t *testing.T) {
	p, f := DefaultRoundsPolicy(), demandFacts()
	demand, err := ResourceDemandOf(f, p, RoundsLatches{})
	if err != nil {
		t.Fatal(err)
	}
	retained, known := demand.Retained.Value()
	if !known {
		t.Fatal("retained unknown with a read consumption")
	}
	line, _ := f.ResourceRunways[0].ProtectedLine()
	want := map[Resource]int64{"Steel": 60, "Cloth": 10, "MedicineIndustrial": line}
	if !reflect.DeepEqual(retained, want) {
		t.Fatalf("retained %v, want %v", retained, want)
	}
	f.ResourceConsumption = domain.Unknown[ResourceConsumption]()
	if demand, err = ResourceDemandOf(f, p, RoundsLatches{}); err != nil {
		t.Fatal(err)
	}
	if _, known := demand.Retained.Value(); known {
		t.Fatal("an unread consumption retained a number")
	}
}

// A source over the one cap is refused with its resource named, never
// dropped.
func TestResourceDemandOfRefusesDemandOverTheCap(t *testing.T) {
	f := stableRounds()
	f.Resources = domain.Known([]Amount{})
	f.ConstructionDeficit = domain.Known(map[Resource]int64{"Steel": maxResourceTarget + 1})
	if _, err := ResourceDemandOf(f, DefaultRoundsPolicy(), RoundsLatches{}); err == nil {
		t.Fatal("demand over the cap was accepted")
	}
}
