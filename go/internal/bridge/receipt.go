package bridge

import (
	"context"
	"encoding/json"
	"fmt"
	"sync"

	"github.com/modelcontextprotocol/go-sdk/jsonrpc"
	"github.com/modelcontextprotocol/go-sdk/mcp"
)

// receiptConnection retains tools/call results before SDK interface decoding
// can round int64 observations. Client serializes calls; IDs reject late replies.
type receiptConnection struct {
	mcp.Connection
	mu      sync.Mutex
	id      jsonrpc.ID
	pending bool
	raw     json.RawMessage
	err     error
}

func (c *receiptConnection) Write(ctx context.Context, message jsonrpc.Message) error {
	if request, ok := message.(*jsonrpc.Request); ok && request.Method == "tools/call" {
		c.mu.Lock()
		c.id = request.ID
		c.pending = true
		c.raw = nil
		c.err = nil
		c.mu.Unlock()
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
		if c.pending && response.ID == c.id {
			c.pending = false
			if len(response.Result) > maxResponseBytes {
				c.err = fmt.Errorf("%w: oversized native result", ErrContract)
			} else {
				c.raw = append(json.RawMessage(nil), response.Result...)
			}
			err = c.err
		}
		c.mu.Unlock()
		if err != nil {
			return nil, err
		}
	}
	return message, nil
}
func (c *receiptConnection) receipt() (json.RawMessage, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	raw, err := c.raw, c.err
	c.pending = false
	c.raw = nil
	c.err = nil
	return raw, err
}
