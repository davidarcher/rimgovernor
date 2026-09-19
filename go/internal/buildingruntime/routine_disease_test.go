package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func TestDiseaseDemandFreshRecoveryAndWorldScope(t *testing.T) {
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 1}
	previous := store.RoutineReview{Snapshot: snapshot, Tick: 100, MedicalCare: policy.MedicalCareHistory{Resting: []policy.DiseaseRest{{Pawn: "patient", Conditions: []string{"Flu"}}}}}
	facts := observation.ColonyProjection{Identity: observation.Identity{Tick: 101}}
	for _, stage := range []string{"unknown", "immune", "new world", "rewind"} {
		facts.Facts.MedicalPawns = domain.Unknown[[]policy.CarePawn]()
		current := snapshot
		want := 0
		switch stage {
		case "unknown":
			want = 1
		case "immune":
			facts.Facts.MedicalPawns = domain.Known([]policy.CarePawn{{ID: "patient", Dead: domain.Known(false), Conditions: domain.Known([]policy.CareCondition{{DefName: domain.Known("Flu"), Immunity: domain.Known(1.0)}})}})
		case "new world":
			current.Load = "other"
		case "rewind":
			facts.Identity.Tick = 1
		}
		demand, err := routineDiseaseDemand(facts, false, previous, current)
		if err != nil || len(demand.Resting) != want {
			t.Fatal(stage, demand, err)
		}
	}
}
