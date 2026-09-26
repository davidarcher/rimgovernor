package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

// A routine planner's own identity read is served from the step's fact
// cache at the tick the step opened on, while the review it plans against
// anchored on the colony read that followed it: under a running window the
// row sits behind the anchor, and demanding row >= anchor refused every
// building planner with ErrControl ("writer authority unavailable") for
// hundreds of consecutive steps (#662).
func TestRoutineBuildingBoundaryAcceptsAnIdentityReadBehindTheReviewAnchor(t *testing.T) {
	drift := domain.LiveDrift()
	t.Cleanup(func() { domain.SetLiveDrift(drift) })
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Native: 1, Plan: "plan"}
	identity := func(tick domain.Tick) observation.Identity {
		return observation.Identity{Colony: snapshot.Colony, Load: snapshot.Load, Map: snapshot.Map, Tick: tick, NativeGeneration: domain.Known(snapshot.Native)}
	}
	for _, v := range []struct {
		name         string
		live         domain.Tick
		actual, tick domain.Tick
		want         bool
	}{
		{name: "paused exact", actual: 7, tick: 7, want: true},
		{name: "paused behind", actual: 7, tick: 107, want: false},
		{name: "live behind", live: 5000, actual: 7, tick: 507, want: true},
		{name: "live behind past the drift", live: 100, actual: 7, tick: 507, want: false},
		{name: "live ahead within the tolerance", live: 5000, actual: 507, tick: 7, want: true},
		{name: "rewind past the drift", live: 100, actual: 7, tick: 5007, want: false},
	} {
		t.Run(v.name, func(t *testing.T) {
			domain.SetLiveDrift(v.live)
			if got := routineBuildingBoundary(identity(v.actual), snapshot, v.tick); got != v.want {
				t.Fatalf("boundary(actual %d, anchor %d, drift %d) = %v", v.actual, v.tick, v.live, got)
			}
		})
	}
	domain.SetLiveDrift(5000)
	if routineBuildingBoundary(identity(7), domain.GenerationSnapshot{Colony: "colony", Load: "load", Native: 2, Plan: "plan"}, 507) {
		t.Fatal("a behind-anchor row crossed a generation flip")
	}
}

// End to end through the sleeping planner: the review anchors on a colony
// read the running window advanced past the step's identity row, and the
// planner must still produce its method instead of failing the step.
func TestRoutineSleepingPlansUnderAReviewAnchorAheadOfTheIdentityRead(t *testing.T) {
	drift := domain.LiveDrift()
	domain.SetLiveDrift(5000)
	t.Cleanup(func() { domain.SetLiveDrift(drift) })
	reviewer, _, _, _, n := routineFixture(t)
	sleepingFacts(n)
	// The review reads the colony 500 ticks after the step opened, inside
	// the running window's drift.
	const opened, reviewed = 7, 507
	observed := n.reply.GetObserved()
	observed.Context.Tick = proto.Int64(reviewed)
	observed.GetPlanning().GetObserved().GetCells().Context = proto.Clone(observed.Context).(*c.ObservationContext)
	review, err := reviewer.Step(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if review.Review.Tick != reviewed {
		t.Fatalf("review anchored on tick %d, not the colony read's", review.Review.Tick)
	}
	planner, err := NewRoutineSleepingPlanner(reviewer, &sleepingNative{routineNative: n})
	if err != nil {
		t.Fatal(err)
	}
	// The planner's identity read is the row the step's fact cache holds,
	// seeded when the step opened and so behind the review's anchor.
	n.identityTick = proto.Int64(opened)
	result, err := planner.Step(context.Background())
	if err != nil {
		t.Fatalf("planner failed on an identity read behind the review anchor: %v", err)
	}
	if result.Reason != BuildingMethodAdmitted || !result.Decision.Admitted {
		t.Fatal(result)
	}
}
