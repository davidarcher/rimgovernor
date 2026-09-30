package bridge

import (
	"context"
	"encoding/json"
	"testing"
	"time"

	obs "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// A deferred context sends defer_snapshot on Actions/Apply; a plain one
// leaves it unset, and the flush is its own control-class call (#1274).
func TestDeferredApplyAndFlush(t *testing.T) {
	var deferred []bool
	var flushes int
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		var wrapper struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &wrapper); err != nil {
			return nil, err
		}
		switch arg.Tool {
		case ActionsApplyMethod:
			request := &o.ApplyRequest{}
			if err := protojson.Unmarshal([]byte(wrapper.Request), request); err != nil {
				return nil, err
			}
			deferred = append(deferred, request.DeferSnapshot != nil && request.GetDeferSnapshot())
			return pbResult(&o.ApplyReply{Results: []*o.ActionResult{{Key: proto.String("a"), Outcome: &o.ActionResult_Applied{Applied: &r.Receipt{AdmittedContext: pbContext(),
				Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: &r.EffectEvidence{}}}}}}}}), nil
		case methodFlushSnapshot:
			flushes++
			return pbResult(&obs.FlushSnapshotReply{Outcome: &obs.FlushSnapshotReply_Flushed{Flushed: true}}), nil
		}
		t.Fatal(arg.Tool)
		return nil, nil
	}}, time.Second)
	writer, err := NewActionsWriter(client)
	if err != nil {
		t.Fatal(err)
	}
	for _, ctx := range []context.Context{context.Background(), WithDeferredSnapshot(context.Background())} {
		if _, _, err = writer.Apply(ctx, pbIdentity(), []*o.Action{probeAction("a")}); err != nil {
			t.Fatal(err)
		}
	}
	if err = client.FlushSnapshot(context.Background()); err != nil {
		t.Fatal(err)
	}
	if len(deferred) != 2 || deferred[0] || !deferred[1] || flushes != 1 {
		t.Fatalf("deferred %v flushes %d", deferred, flushes)
	}
	if admissionClassOf(methodFlushSnapshot) != AdmissionControl || nativeReadMethod(methodFlushSnapshot) {
		t.Fatal("flush is a control-class write")
	}
}

// A read marked WithAnyFrame (map bounds) takes the newest frame even
// when it predates the last write; other reads keep waiting past it.
func TestAnyFrameSkipsTheWriteWait(t *testing.T) {
	client, server, ring := frameClient(t)
	ring.publish(t, server.snapshot, 0)
	server.familyReads(t, context.Background(), client)
	<-server.opens
	ring.mu.Lock()
	ring.writes = 1
	ring.mu.Unlock()
	client.noteFrameWrite()
	ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
	defer cancel()
	if _, _, err := client.ReadResearch(WithAnyFrame(ctx), pbIdentity()); err != nil {
		t.Fatalf("any-frame read behind a write: %v", err)
	}
	if _, _, err := client.ReadResearch(ctx, pbIdentity()); err == nil {
		t.Fatal("a plain read served a frame from before the write")
	}
}
