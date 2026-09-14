package buildingruntime

import (
	"os"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

// TestRoutineReviewerCachesConfiguredLongitudeAsSessionConstant covers Part A
// of the EnsureMood-* relief plumbing (#27 G01.07e): RoutineCapabilities.Longitude
// is threaded once at construction into RoutineReviewer, not re-derived from
// RoutineSource on every Step the way every other routine fact is -- the
// colony's home map tile cannot move within a session, so there is nothing to
// re-observe.
func TestRoutineReviewerCachesConfiguredLongitudeAsSessionConstant(t *testing.T) {
	t.Parallel()
	p, _, _, _ := playerFixture(t)
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	n := &routineNative{reply: &o.ColonyFactsReply{}}
	if err = protojson.Unmarshal(data, n.reply); err != nil {
		t.Fatal(err)
	}
	clock := testkit.NewManualClock(time.Now())

	// Omitted entirely: longitude stays unknown, exactly like every other
	// optional capability (Methods) does when callers compose it themselves.
	r, err := NewRoutineReviewer(p, n, clock, policy.DefaultRoutinePolicy(), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	if _, known := r.longitude.Value(); known {
		t.Fatal("expected unknown longitude when no capability supplied")
	}

	// Supplied: cached verbatim on the reviewer, available to every planner
	// sharing it without a fresh native round trip.
	r, err = NewRoutineReviewer(p, n, clock, policy.DefaultRoutinePolicy(), time.Second, RoutineCapabilities{Longitude: domain.Known(112.5)})
	if err != nil {
		t.Fatal(err)
	}
	got, known := r.longitude.Value()
	if !known || got != 112.5 {
		t.Fatalf("expected cached longitude 112.5, got %v known=%v", got, known)
	}

	// Out-of-range/non-finite longitude is refused at construction, matching
	// bridge.WorldRead's own decode-time validation, rather than silently
	// caching a value ExpectedScheduleDef would later trust.
	for _, bad := range []float64{181, -181} {
		if _, err = NewRoutineReviewer(p, n, clock, policy.DefaultRoutinePolicy(), time.Second, RoutineCapabilities{Longitude: domain.Known(bad)}); err == nil {
			t.Fatalf("expected out-of-range longitude %v to be refused", bad)
		}
	}
}
