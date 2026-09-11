package bridge

import (
	"context"
	"encoding/json"
	"testing"

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
	ctx := context.Background()
	_ = connection.Write(ctx, &jsonrpc.Request{ID: first, Method: "tools/call"})
	if raw, err := connection.receipt(); len(raw) != 0 || err != nil {
		t.Fatal("canceled call had receipt")
	}
	_ = connection.Write(ctx, &jsonrpc.Request{ID: second, Method: "tools/call"})
	pipe.message = &jsonrpc.Response{ID: first, Result: json.RawMessage(`{"late":true}`)}
	if _, err := connection.Read(ctx); err != nil {
		t.Fatal(err)
	}
	pipe.message = &jsonrpc.Response{ID: second, Result: json.RawMessage(`{"current":true}`)}
	if _, err := connection.Read(ctx); err != nil {
		t.Fatal(err)
	}
	raw, err := connection.receipt()
	if err != nil || string(raw) != `{"current":true}` {
		t.Fatalf("wrong receipt %s: %v", raw, err)
	}
	if raw, err := connection.receipt(); len(raw) != 0 || err != nil {
		t.Fatal("receipt retained after consumption")
	}
}
