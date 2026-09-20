package bridge

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"
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
	return c.operation(ctx, AdmissionControl, func(ctx context.Context, live *liveSession) (Result, error) {
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
	return c.operation(ctx, AdmissionControl, func(ctx context.Context, live *liveSession) (Result, error) {
		return c.core(ctx, live, "games_status", encode(gameArgument{c.gameID}))
	})
}

type attentionArgument struct {
	GameID      string `json:"gameId"`
	AttentionID string `json:"attentionId"`
}

// GetAttention reads the current GABS attention without acknowledging it.
func (c *Client) GetAttention(ctx context.Context) (Result, error) {
	return c.operation(ctx, AdmissionControl, func(ctx context.Context, live *liveSession) (Result, error) {
		return c.core(ctx, live, "games_get_attention", encode(gameArgument{c.gameID}))
	})
}

// AckAttention acknowledges a blocking GABS attention item (raised when GABS
// observes a game-side log line it treats as noteworthy, e.g. an error-level
// message) so that games_call_tool stops refusing further calls for this
// game on its account. It never inspects or filters what is being
// acknowledged; callers decide that from the Refusal detail they observed.
func (c *Client) AckAttention(ctx context.Context, attentionID string) (Result, error) {
	if attentionID == "" {
		return Result{}, fmt.Errorf("%w: attention id required", ErrContract)
	}
	return c.operation(ctx, AdmissionControl, func(ctx context.Context, live *liveSession) (Result, error) {
		return c.core(ctx, live, "games_ack_attention", encode(attentionArgument{c.gameID, attentionID}))
	})
}

// NativeNames and Describe inspect metadata only. A discovered name can never be
// passed through these methods to games_call_tool.
func (c *Client) NativeNames(ctx context.Context, cursor, query string) (Result, error) {
	if len(cursor) > 4096 || len(query) > 256 {
		return Result{}, fmt.Errorf("%w: discovery filter too long", ErrContract)
	}
	return c.operation(ctx, AdmissionControl, func(ctx context.Context, live *liveSession) (Result, error) {
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
	return c.operation(ctx, AdmissionControl, func(ctx context.Context, live *liveSession) (Result, error) { return c.describe(ctx, live, name) })
}
func (c *Client) describe(ctx context.Context, live *liveSession, name string) (Result, error) {
	return c.core(ctx, live, "games_tool_detail", encode(detailArgument{c.gameID, name}))
}
