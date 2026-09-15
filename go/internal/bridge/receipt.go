package bridge

import (
	"context"
	"encoding/json"
	"fmt"
	"sync"

	"github.com/modelcontextprotocol/go-sdk/jsonrpc"
	"github.com/modelcontextprotocol/go-sdk/mcp"
)

// receiptOutcome is the raw wire result (or error) for one tools/call request,
// captured before SDK interface decoding can round int64 observations.
type receiptOutcome struct {
	raw json.RawMessage
	err error
}

type receiptWaiterKey struct{}

// withReceiptWaiter attaches ch to ctx so a subsequent tools/call Write on
// this connection, made with a context derived from ctx, can find it. The
// caller then reads its own outcome directly from ch — never from a shared
// slot — so concurrent tools/call requests on one connection do not race.
func withReceiptWaiter(ctx context.Context, ch chan receiptOutcome) context.Context {
	return context.WithValue(ctx, receiptWaiterKey{}, ch)
}

// receiptConnection retains tools/call results before SDK interface decoding
// can round int64 observations. Requests are correlated by JSON-RPC ID via
// pending, so multiple tools/call requests may be in flight concurrently on
// this connection: each caller only ever reads the outcome for its own ID.
type receiptConnection struct {
	mcp.Connection
	mu      sync.Mutex
	pending map[jsonrpc.ID]chan receiptOutcome
}

func (c *receiptConnection) Write(ctx context.Context, message jsonrpc.Message) error {
	if request, ok := message.(*jsonrpc.Request); ok && request.Method == "tools/call" {
		if ch, ok := ctx.Value(receiptWaiterKey{}).(chan receiptOutcome); ok {
			c.mu.Lock()
			if c.pending == nil {
				c.pending = make(map[jsonrpc.ID]chan receiptOutcome)
			}
			c.pending[request.ID] = ch
			c.mu.Unlock()
		}
	}
	return c.Connection.Write(ctx, message)
}
func (c *receiptConnection) Read(ctx context.Context) (jsonrpc.Message, error) {
	message, err := c.Connection.Read(ctx)
	if err != nil {
		return nil, err
	}
	if response, ok := message.(*jsonrpc.Response); ok {
		c.mu.Lock()
		ch, pending := c.pending[response.ID]
		if pending {
			delete(c.pending, response.ID)
		}
		c.mu.Unlock()
		if pending {
			var outcome receiptOutcome
			if len(response.Result) > maxResponseBytes {
				outcome.err = fmt.Errorf("%w: oversized native result", ErrContract)
			} else {
				outcome.raw = append(json.RawMessage(nil), response.Result...)
			}
			ch <- outcome
		}
	}
	return message, nil
}
