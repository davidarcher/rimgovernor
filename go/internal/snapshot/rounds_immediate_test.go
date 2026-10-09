package snapshot

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func TestImmediateReviewRecordingReplaysItsScope(t *testing.T) {
	facts := policy.RoundsFacts{Hostiles: domain.Known(int64(2)), CriticalPatients: domain.Known(int64(1)), SafeAreaOwed: domain.Known(true)}
	limits := policy.DefaultRoundsPolicy()
	latches := policy.RoundsLatches{Wood: true, Food: true}
	want, err := policy.InspectImmediateRounds(facts, latches, limits)
	if err != nil {
		t.Fatal(err)
	}
	result := store.RoundsResult{Review: store.Rounds{Immediate: true, Tick: 20, OrdinaryTick: 10}, Detection: &store.RoundsDetection{Facts: facts, Latches: latches, Policy: limits}}
	record, ok := FromReview(domain.GenerationSnapshot{}, 20, result, observation.ColonyProjection{})
	if !ok || !record.Immediate {
		t.Fatal("urgent inspection lost scope")
	}
	data, err := Encode(record)
	if err != nil {
		t.Fatal(err)
	}
	recorded, err := decodeRounds(data)
	if err != nil {
		t.Fatal(err)
	}
	got, err := recorded.Detect()
	if err != nil || !reflect.DeepEqual(got, want) {
		t.Fatalf("urgent replay: %+v %v", got, err)
	}
	if _, err := recorded.Assessment(policy.MaintainResource); err == nil {
		t.Fatal("partial recording manufactured an ordinary assessment")
	}
	record.Immediate = false
	data, err = Encode(record)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = decodeRounds(data); err == nil {
		t.Fatal("conflicting recording/review scope accepted")
	}
}
