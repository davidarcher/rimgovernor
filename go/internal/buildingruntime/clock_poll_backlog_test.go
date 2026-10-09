package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

// backlogNative is a native journal of newest rows: each read answers the
// row after the request's cursor.
type backlogNative struct {
	*clockPollNative
	f        *schedulerNative
	newest   int64
	requests []int64
}

func (n *backlogNative) ReadClockEvents(ctx context.Context, request *k.EventsRequest) (*k.EventsReply, bridge.Result, error) {
	n.requests = append(n.requests, request.GetAfterCursor())
	page := clockPollPage(n.f, request.GetAfterCursor(), "speed")
	page.NewestCursor = proto.Int64(n.newest)
	return &k.EventsReply{Outcome: &k.EventsReply_Page{Page: page}}, bridge.Result{}, nil
}

// A fresh journal against a long native backlog adopts all but its newest
// page instead of replaying it a page per poll.
func TestClockPollAdoptsNativeBacklog(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	s, f, _ := clockPollFixture(t)
	native := &backlogNative{clockPollNative: &clockPollNative{core: f.clockCoreFake}, f: f, newest: 1000}
	result, _ := s.PollEvents(context.Background(), native, 128)
	if len(native.requests) != 2 || native.requests[0] != 0 || native.requests[1] != 1000-128 {
		t.Fatal(native.requests)
	}
	if !result.Captured || result.Review.InboxCursor != 1000-127 {
		t.Fatal(result)
	}
	// Once the journal holds history it reads on from its cursor.
	native.requests = nil
	_, _ = s.PollEvents(context.Background(), native, 128)
	if len(native.requests) != 1 || native.requests[0] != 1000-127 {
		t.Fatal(native.requests)
	}
}
