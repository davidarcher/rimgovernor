package bridge

import (
	"context"
	"errors"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
)

// TestGovernorState covers the read and put adapters: blobs come back by
// key, a missing outcome is a contract error, an unloaded game is
// ErrUnavailable, and a non-ASCII put never reaches native (#600).
func TestGovernorState(t *testing.T) {
	var reply *l.GovernorStateReply
	var tools []string
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		tools = append(tools, arg.Tool)
		return pbResult(reply), nil
	}}
	client := testClient(t, s, testBudget)
	ctx := context.Background()

	reply = &l.GovernorStateReply{Outcome: &l.GovernorStateReply_Loaded{Loaded: &l.GovernorStateBlobs{Blobs: map[string]string{"goals": `{"v":1}`}}}}
	if blobs, err := client.GovernorState(ctx); err != nil || blobs["goals"] != `{"v":1}` {
		t.Fatalf("read %v %v", blobs, err)
	}
	reply = &l.GovernorStateReply{Outcome: &l.GovernorStateReply_Loaded{Loaded: &l.GovernorStateBlobs{}}}
	if blobs, err := client.PutGovernorState(ctx, "goals", ""); err != nil || blobs == nil || len(blobs) != 0 {
		t.Fatalf("delete %v %v", blobs, err)
	}
	reply = &l.GovernorStateReply{Outcome: &l.GovernorStateReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_LOADED.Enum()}}}
	if _, err := client.GovernorState(ctx); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("unloaded: %v", err)
	}
	reply = &l.GovernorStateReply{}
	if _, err := client.GovernorState(ctx); !errors.Is(err, ErrContract) {
		t.Fatalf("missing outcome: %v", err)
	}
	calls := len(tools)
	for _, bad := range [][2]string{{"", "x"}, {"gö", "x"}, {"k", "ö"}} {
		if _, err := client.PutGovernorState(ctx, bad[0], bad[1]); !errors.Is(err, ErrContract) {
			t.Fatalf("put %q: %v", bad, err)
		}
	}
	if len(tools) != calls || tools[1] != putGovernorStateMethod || tools[0] != readGovernorStateMethod {
		t.Fatalf("calls %v", tools)
	}
}
