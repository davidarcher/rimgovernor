package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"google.golang.org/protobuf/proto"
)

// TestAcquisitionHoldsForOutdoorHazardWithAValidVerdict is the #2812 guard: an
// extreme-cold read reaches RoundsAcquisitionPlanner.step and the hold comes
// back as a valid wait on the outdoor_hazard subject; it used to panic in
// waitFor because unsafe_threat is no wait kind.
func TestAcquisitionHoldsForOutdoorHazardWithAValidVerdict(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	reviewer, _, _, _, native := roundsFixture(t)
	v := native.reply.GetObserved()
	v.OutdoorTemperatureC = proto.Float64(-30)
	reviewer.native = &healthyWorkNative{roundsMedicalNative: &roundsMedicalNative{roundsNative: native}}
	reviewer.methods = domain.Known([]policy.ConcernID{policy.MaintainResource})
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoundsAcquisitionPlanner(reviewer, policy.MaintainResource)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil {
		t.Fatal(err)
	}
	want := waitFor(policy.CauseRoomTemperature, policy.RemoteHoldHazard)
	if result.Verdict != want || result.Verdict.Validate() != nil {
		t.Fatalf("verdict %+v, want %+v", result.Verdict, want)
	}
}
