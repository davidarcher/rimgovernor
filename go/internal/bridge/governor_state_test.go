package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"testing"

	"google.golang.org/protobuf/encoding/protojson"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
)

// TestPutGovernorStateBatch covers the batch adapter (#2357): the whole set
// travels in one call (an empty batch included, which clears every key), the
// request carries exactly the given keys so absent keys are deleted natively,
// and a non-ASCII key or blob never reaches native.
func TestPutGovernorStateBatch(t *testing.T) {
	var requests []*l.PutGovernorStateBatchRequest
	reply := &l.GovernorStateReply{Outcome: &l.GovernorStateReply_Loaded{Loaded: &l.GovernorStateBlobs{}}}
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		if arg.Tool != putGovernorStateBatchMethod {
			t.Fatalf("tool %q", arg.Tool)
		}
		var envelope struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &envelope); err != nil {
			t.Fatal(err)
		}
		req := &l.PutGovernorStateBatchRequest{}
		if err := protojson.Unmarshal([]byte(envelope.Request), req); err != nil {
			t.Fatalf("request: %v", err)
		}
		requests = append(requests, req)
		return pbResult(reply), nil
	}}
	client := testClient(t, s, testBudget)
	ctx := context.Background()

	if err := client.PutGovernorStateBatch(ctx, map[string]string{"standard/a": `{"v":1}`, "project/b": "x"}); err != nil {
		t.Fatalf("replace: %v", err)
	}
	if got := requests[0].GetBlobs(); len(got) != 2 || got["standard/a"] != `{"v":1}` || got["project/b"] != "x" {
		t.Fatalf("replace request %v", got)
	}
	// A later batch without project/b: absence is the delete.
	if err := client.PutGovernorStateBatch(ctx, map[string]string{"standard/a": "y"}); err != nil {
		t.Fatalf("delete by absence: %v", err)
	}
	if got := requests[1].GetBlobs(); len(got) != 1 || got["standard/a"] != "y" {
		t.Fatalf("absence request %v", got)
	}
	if err := client.PutGovernorStateBatch(ctx, nil); err != nil {
		t.Fatalf("empty batch: %v", err)
	}
	if len(requests) != 3 || len(requests[2].GetBlobs()) != 0 {
		t.Fatalf("empty request %v", requests)
	}
	for _, bad := range []map[string]string{{"": "x"}, {"gö": "x"}, {"k": "ö"}} {
		if err := client.PutGovernorStateBatch(ctx, bad); !errors.Is(err, ErrContract) {
			t.Fatalf("batch %q: %v", bad, err)
		}
	}
	if len(requests) != 3 {
		t.Fatalf("invalid batches reached native: %d", len(requests))
	}
	reply = &l.GovernorStateReply{Outcome: &l.GovernorStateReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_LOADED.Enum()}}}
	if err := client.PutGovernorStateBatch(ctx, nil); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("unloaded: %v", err)
	}
}

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
	if err := client.PutGovernorState(ctx, "goals", ""); err != nil {
		t.Fatalf("delete %v", err)
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
		if err := client.PutGovernorState(ctx, bad[0], bad[1]); !errors.Is(err, ErrContract) {
			t.Fatalf("put %q: %v", bad, err)
		}
	}
	if len(tools) != calls || tools[1] != putGovernorStateMethod || tools[0] != readGovernorStateMethod {
		t.Fatalf("calls %v", tools)
	}
}
