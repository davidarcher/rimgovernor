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
// building planner with ErrControl ("planner read stale or control unavailable") for
// hundreds of consecutive steps (#662).
func TestRoutineBuildingBoundaryAcceptsAnIdentityReadBehindTheReviewAnchor(t *testing.T) {
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Native: 1, Plan: "plan"}
	identity := func(tick domain.Tick) observation.Identity {
		return observation.Identity{Colony: snapshot.Colony, Load: snapshot.Load, Map: snapshot.Map, Tick: tick, NativeGeneration: domain.Known(snapshot.Native)}
	}
	for _, v := range []struct {
		name         string
		actual, tick domain.Tick
		want         bool
	}{
		{name: "exact", actual: 7, tick: 7, want: true},
		{name: "behind", actual: 7, tick: 5007, want: true},
		{name: "ahead", actual: 5007, tick: 7, want: true},
	} {
		t.Run(v.name, func(t *testing.T) {
			if got := routineBuildingBoundary(identity(v.actual), snapshot, v.tick); got != v.want {
				t.Fatalf("boundary(actual %d, anchor %d) = %v", v.actual, v.tick, got)
			}
		})
	}
	if routineBuildingBoundary(identity(7), domain.GenerationSnapshot{Colony: "colony", Load: "load", Native: 2, Plan: "plan"}, 507) {
		t.Fatal("a behind-anchor row crossed a generation flip")
	}
}

// End to end through the sleeping planner: the review anchors on a colony
// read the running window advanced past the step's identity row, and the
// planner must still produce its method instead of failing the step.
func TestRoutineSleepingPlansUnderAReviewAnchorAheadOfTheIdentityRead(t *testing.T) {
	reviewer, _, _, _, n := routineFixture(t)
	sleepingFacts(n)
	// The review reads the colony 500 ticks after the step opened.
	const opened, reviewed = 7, 507
	observed := n.reply.GetObserved()
	observed.Context.Tick = proto.Int64(reviewed)
	n.cells.Context = proto.Clone(observed.Context).(*c.ObservationContext)
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
