package bridge

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"

	"github.com/google/jsonschema-go/jsonschema"
)

func encode(value any) json.RawMessage {
	data, err := json.Marshal(value)
	if err != nil {
		panic(err)
	}
	return data
}

type gameArgument struct {
	GameID string `json:"gameId"`
}
type detailArgument struct {
	GameID string `json:"gameId"`
	Tool   string `json:"tool"`
}
type nativeArgument struct {
	GameID    string          `json:"gameId"`
	Tool      string          `json:"tool"`
	Arguments json.RawMessage `json:"arguments"`
}

// ConnectGame attaches GABS to an already-running game. It does not start, load,
// stop or advance the game. There is no implicit reconnect or retry.
func (c *Client) ConnectGame(ctx context.Context) (Result, error) {
	return c.connectGame(ctx, false)
}

// ConnectGameWithTakeover explicitly transfers an existing GABS attachment.
// Callers must own the external session handoff; normal runtime uses ConnectGame.
func (c *Client) ConnectGameWithTakeover(ctx context.Context) (Result, error) {
	return c.connectGame(ctx, true)
}

func (c *Client) connectGame(ctx context.Context, force bool) (Result, error) {
	return c.operation(ctx, func(ctx context.Context, live *liveSession) (Result, error) {
		args := struct {
			GameID        string `json:"gameId"`
			Timeout       int    `json:"timeout"`
			ForceTakeover bool   `json:"forceTakeover,omitempty"`
		}{c.gameID, int(c.timeout.Seconds()), force}
		if args.Timeout < 1 {
			args.Timeout = 1
		}
		result, err := c.core(ctx, live, "games_connect", encode(args))
		if err != nil {
			return result, err
		}
		var ownership struct {
			ForeignOwner bool `json:"foreignOwner"`
		}
		if json.Unmarshal(result.Structured, &ownership) != nil {
			return result, contract("invalid connection reply")
		}
		if ownership.ForeignOwner {
			return result, &Refusal{Tool: "games_connect", Result: result}
		}
		return result, nil
	})
}
func (c *Client) GameStatus(ctx context.Context) (Result, error) {
	return c.operation(ctx, func(ctx context.Context, live *liveSession) (Result, error) {
		return c.core(ctx, live, "games_status", encode(gameArgument{c.gameID}))
	})
}

// NativeNames and Describe inspect metadata only. A discovered name can never be
// passed through these methods to games_call_tool.
func (c *Client) NativeNames(ctx context.Context, cursor, query string) (Result, error) {
	if len(cursor) > 4096 || len(query) > 256 {
		return Result{}, fmt.Errorf("%w: discovery filter too long", ErrContract)
	}
	return c.operation(ctx, func(ctx context.Context, live *liveSession) (Result, error) {
		args := struct {
			GameID string `json:"gameId"`
			Cursor string `json:"cursor"`
			Query  string `json:"query"`
		}{c.gameID, cursor, query}
		return c.core(ctx, live, "games_tool_names", encode(args))
	})
}
func (c *Client) Describe(ctx context.Context, name string) (Result, error) {
	if name == "" || len(name) > 256 || strings.ContainsAny(name, "\x00\r\n") {
		return Result{}, fmt.Errorf("%w: invalid tool name", ErrContract)
	}
	return c.operation(ctx, func(ctx context.Context, live *liveSession) (Result, error) { return c.describe(ctx, live, name) })
}
func (c *Client) describe(ctx context.Context, live *liveSession, name string) (Result, error) {
	return c.core(ctx, live, "games_tool_detail", encode(detailArgument{c.gameID, name}))
}

func validateInput(detail, args json.RawMessage) error {
	var envelope struct {
		InputSchema json.RawMessage `json:"inputSchema"`
	}
	if json.Unmarshal(detail, &envelope) != nil || len(envelope.InputSchema) == 0 {
		return fmt.Errorf("%w: native input schema missing", ErrContract)
	}
	var schema jsonschema.Schema
	if err := json.Unmarshal(envelope.InputSchema, &schema); err != nil {
		return fmt.Errorf("%w: native input schema: %w", ErrContract, err)
	}
	if schema.Type != "object" {
		return fmt.Errorf("%w: native input schema must be object", ErrContract)
	}
	// The SDK accepts JSON-shaped values here. Keep this dynamic representation
	// inside the adapter, after typed read construction and before dispatch.
	var value map[string]any
	if err := json.Unmarshal(args, &value); err != nil || value == nil {
		return fmt.Errorf("%w: invalid argument object", ErrContract)
	}
	for key := range value {
		if schema.Properties[key] == nil {
			return fmt.Errorf("%w: unknown native argument %s", ErrContract, key)
		}
	}
	resolved, err := schema.Resolve(nil) // no network schema loader
	if err != nil {
		return fmt.Errorf("%w: resolve native schema: %w", ErrContract, err)
	}
	if err = resolved.Validate(value); err != nil {
		return fmt.Errorf("%w: native arguments: %w", ErrContract, err)
	}
	return nil
}
