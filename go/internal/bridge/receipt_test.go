package bridge

import (
	"context"
	"encoding/json"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/jsonrpc"
)

type receiptPipe struct{ message jsonrpc.Message }

func (p *receiptPipe) Read(context.Context) (jsonrpc.Message, error) { return p.message, nil }
func (*receiptPipe) Write(context.Context, jsonrpc.Message) error    { return nil }
func (*receiptPipe) Close() error                                    { return nil }
func (*receiptPipe) SessionID() string                               { return "fixture" }

func TestReceiptIDsIgnoreLateCanceledReplies(t *testing.T) {
	pipe := &receiptPipe{}
	connection := &receiptConnection{Connection: pipe}
	first, _ := jsonrpc.MakeID("first")
	second, _ := jsonrpc.MakeID("second")

	// "first" is abandoned: registered, then never read from, mirroring a
	// caller that gave up (context canceled) before a response arrived.
	abandoned := make(chan receiptOutcome, 1)
	_ = connection.Write(withReceiptWaiter(context.Background(), abandoned), &jsonrpc.Request{ID: first, Method: "tools/call"})

	current := make(chan receiptOutcome, 1)
	_ = connection.Write(withReceiptWaiter(context.Background(), current), &jsonrpc.Request{ID: second, Method: "tools/call"})

	// The late reply for "first" arrives after "second" was already sent; it
	// must land on its own (abandoned) channel, not cross-talk into "second".
	pipe.message = &jsonrpc.Response{ID: first, Result: json.RawMessage(`{"late":true}`)}
	if _, err := connection.Read(context.Background()); err != nil {
		t.Fatal(err)
	}
	pipe.message = &jsonrpc.Response{ID: second, Result: json.RawMessage(`{"current":true}`)}
	if _, err := connection.Read(context.Background()); err != nil {
		t.Fatal(err)
	}

	select {
	case outcome := <-current:
		if outcome.err != nil || string(outcome.raw) != `{"current":true}` {
			t.Fatalf("wrong receipt %s: %v", outcome.raw, outcome.err)
		}
	default:
		t.Fatal("current call missing its receipt")
	}
	select {
	case outcome := <-abandoned:
		if string(outcome.raw) != `{"late":true}` {
			t.Fatalf("wrong abandoned receipt %s", outcome.raw)
		}
	default:
		t.Fatal("abandoned call never observed its late receipt")
	}

	// Each request ID is delivered exactly once and then forgotten.
	connection.mu.Lock()
	pending := len(connection.pending)
	connection.mu.Unlock()
	if pending != 0 {
		t.Fatalf("pending receipts leaked: %d", pending)
	}
}

func TestReceiptConcurrentCallsDoNotCrossTalk(t *testing.T) {
	pipe := &receiptPipe{}
	connection := &receiptConnection{Connection: pipe}

	const n = 5
	waiters := make([]chan receiptOutcome, n)
	ids := make([]jsonrpc.ID, n)
	for i := 0; i < n; i++ {
		ids[i], _ = jsonrpc.MakeID(string(rune('a' + i)))
		waiters[i] = make(chan receiptOutcome, 1)
		if err := connection.Write(withReceiptWaiter(context.Background(), waiters[i]), &jsonrpc.Request{ID: ids[i], Method: "tools/call"}); err != nil {
			t.Fatal(err)
		}
	}
	// Deliver responses out of request order.
	for i := n - 1; i >= 0; i-- {
		pipe.message = &jsonrpc.Response{ID: ids[i], Result: json.RawMessage(`{"index":` + string(rune('0'+i)) + `}`)}
		if _, err := connection.Read(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	for i := 0; i < n; i++ {
		select {
		case outcome := <-waiters[i]:
			want := `{"index":` + string(rune('0'+i)) + `}`
			if string(outcome.raw) != want {
				t.Fatalf("waiter %d got %s, want %s", i, outcome.raw, want)
			}
		case <-time.After(time.Second):
			t.Fatalf("waiter %d never received its receipt", i)
		}
	}
}
