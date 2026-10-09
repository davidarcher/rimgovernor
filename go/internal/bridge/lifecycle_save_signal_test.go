package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"testing"
	"time"

	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
)

// TestSaveSignalWaitAndAck covers the pre_save handshake adapters
// against a fake native: a signal yields its token, a timeout yields
// ok=false, an ack of the held token succeeds, and a stale token is
// ErrStaleSaveToken rather than an ack.
func TestSaveSignalWaitAndAck(t *testing.T) {
	held := "tok1"
	var waitReq *l.WaitSaveSignalRequest
	var acked []string
	waitReply := &l.WaitSaveSignalReply{Outcome: &l.WaitSaveSignalReply_Signal{Signal: &l.SaveSignal{Kind: proto.String(SaveSignalPreSave), Token: proto.String("tok1")}}}
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		var envelope struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &envelope); err != nil {
			t.Fatal(err)
		}
		switch arg.Tool {
		case waitSaveSignalMethod:
			waitReq = &l.WaitSaveSignalRequest{}
			if err := protojson.Unmarshal([]byte(envelope.Request), waitReq); err != nil {
				t.Fatal(err)
			}
			return pbResult(waitReply), nil
		case flushDoneMethod:
			req := &l.FlushDoneRequest{}
			if err := protojson.Unmarshal([]byte(envelope.Request), req); err != nil {
				t.Fatal(err)
			}
			if req.GetToken() == held {
				acked = append(acked, req.GetToken())
				held = ""
				return pbResult(&l.FlushDoneReply{Outcome: &l.FlushDoneReply_Acked{Acked: &l.FlushDoneAcked{}}}), nil
			}
			return pbResult(&l.FlushDoneReply{Outcome: &l.FlushDoneReply_Failure{Failure: &c.Failure{
				Code: c.FailureCode_FAILURE_CODE_NOT_FOUND.Enum(), Detail: proto.String("Stale or unknown save token.")}}}), nil
		}
		t.Fatalf("tool %q", arg.Tool)
		return nil, nil
	}}
	client := testClient(t, s, testBudget)
	ctx := context.Background()

	token, ok, err := client.WaitSaveSignal(ctx, 2*time.Second)
	if err != nil || !ok || token != "tok1" {
		t.Fatalf("wait: %q %v %v", token, ok, err)
	}
	if waitReq.GetTimeoutMs() != 2000 {
		t.Fatalf("timeout_ms %d", waitReq.GetTimeoutMs())
	}
	if err := client.FlushDone(ctx, token); err != nil || len(acked) != 1 {
		t.Fatalf("ack: %v %v", err, acked)
	}
	// The same token again, or one from before a restart, is stale.
	if err := client.FlushDone(ctx, token); !errors.Is(err, ErrStaleSaveToken) {
		t.Fatalf("second ack: %v", err)
	}
	if err := client.FlushDone(ctx, "from-before-restart"); !errors.Is(err, ErrStaleSaveToken) {
		t.Fatalf("stale ack: %v", err)
	}

	waitReply = &l.WaitSaveSignalReply{Outcome: &l.WaitSaveSignalReply_Timeout{Timeout: &l.SaveSignalTimeout{}}}
	if token, ok, err := client.WaitSaveSignal(ctx, 0); err != nil || ok || token != "" {
		t.Fatalf("timeout: %q %v %v", token, ok, err)
	}
	if waitReq.TimeoutMs != nil {
		t.Fatalf("zero timeout sent %d", waitReq.GetTimeoutMs())
	}

	waitReply = &l.WaitSaveSignalReply{Outcome: &l.WaitSaveSignalReply_Signal{Signal: &l.SaveSignal{Kind: proto.String("post_save"), Token: proto.String("x")}}}
	if _, _, err := client.WaitSaveSignal(ctx, time.Second); !errors.Is(err, ErrContract) {
		t.Fatalf("wrong kind: %v", err)
	}
	waitReply = &l.WaitSaveSignalReply{}
	if _, _, err := client.WaitSaveSignal(ctx, time.Second); !errors.Is(err, ErrContract) {
		t.Fatalf("missing outcome: %v", err)
	}
	if _, _, err := client.WaitSaveSignal(ctx, -time.Second); !errors.Is(err, ErrContract) {
		t.Fatalf("negative: %v", err)
	}
	if err := client.FlushDone(ctx, ""); !errors.Is(err, ErrContract) {
		t.Fatalf("empty token: %v", err)
	}
}
