package policy

import (
	"math"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestImmediateRoundsInspectOnlySafetyAndKeepOrdinaryLatches(t *testing.T) {
	previous := RoundsLatches{Food: true, Wood: true, Cold: true, Upkeep: UpkeepHistory{Repairs: true, Cleaning: true}}
	f := RoundsFacts{Hostiles: domain.Known(int64(1)), CriticalPatients: domain.Known(int64(1)), CleanupPawns: domain.Known(true), SafeAreaOwed: domain.Known(true), Wood: domain.Known(int64(-5)), FoodDays: domain.Known(math.NaN()), Upkeep: UpkeepObservation{Fires: domain.Known([]UpkeepFire{{ID: "fire", Home: true, Size: domain.Known(1.0)}}), Structures: domain.Known([]UpkeepStructure{{ID: "invalid", HitPoints: -1}})}}
	r, err := InspectImmediateRounds(f, previous, DefaultRoundsPolicy())
	if err != nil {
		t.Fatal(err)
	}
	if len(r.All()) != 6 {
		t.Fatalf("inspection scope: %+v", r.All())
	}
	for _, n := range r.All() {
		if !ImmediateConcern(n.ID) {
			t.Fatalf("ordinary concern inspected: %+v", n)
		}
	}
	want := previous
	want.Upkeep.Fire = true
	if !reflect.DeepEqual(r.Latches, want) {
		t.Fatalf("ordinary latches changed: %+v", r.Latches)
	}
	if len(r.ResourceDemand.Needs) != 0 {
		t.Fatal("immediate review computed ordinary resource demand")
	}
}

func TestImmediateRoundsDoNotInferDisasterServiceRecovery(t *testing.T) {
	h := &DisasterHistory{Started: 4, Observed: 10, Phase: DisasterUnknown}
	r, err := InspectImmediateRounds(RoundsFacts{Disaster: h, DisasterTick: 100}, RoundsLatches{}, DefaultRoundsPolicy())
	if err != nil {
		t.Fatal(err)
	}
	if r.Disaster != h || r.Disaster.Observed != 10 {
		t.Fatal("recovery history aged without inputs")
	}
	for _, n := range r.Incidents {
		if n.ID == RecoverDisasterServices && n.Finding != domain.FindingUnclear {
			t.Fatal(n)
		}
	}
}
