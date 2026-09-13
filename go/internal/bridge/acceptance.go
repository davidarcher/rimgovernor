package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"time"
)

// GamesStart tells GABS to launch the configured game process per its DirectPath
// configuration. It never connects, loads or advances the game; ConnectGame or
// ConnectWithPoll must run afterward before any native tool is discoverable.
//
// GABS can refuse a games_start moments after a preceding games_stop for the same
// game ID: it briefly still holds a launch claim for the previous process while it
// finishes tearing down, and reports that in the refusal detail (mirroring the
// "a launch claim for ... was published while preparing this operation ... re-check
// games_status and retry" pattern that controller/rimgovernor/bridge.py's
// runtime_file_read documents for native reads). This is a harness-sequencing race,
// not a game mutation, so GamesStart retries it a bounded number of times, re-checking
// games_status in between exactly as the Python retry does.
func (c *Client) GamesStart(ctx context.Context) (Result, error) {
	const maxAttempts = 3
	var lastErr error
	for attempt := 0; attempt < maxAttempts; attempt++ {
		result, err := c.operation(ctx, func(ctx context.Context, live *liveSession) (Result, error) {
			return c.core(ctx, live, "games_start", encode(gameArgument{c.gameID}))
		})
		if err == nil {
			return result, nil
		}
		var refusal *Refusal
		if !errors.As(err, &refusal) || !isLaunchClaimRace(refusal) || attempt == maxAttempts-1 {
			return result, err
		}
		lastErr = err
		if ctx.Err() != nil {
			return Result{}, ctx.Err()
		}
		_, _ = c.GameStatus(ctx) // re-check status, matching Python's claim-changed retry; error ignored, only used to let GABS settle
		if err := sleepOrDone(ctx, time.Duration(attempt+1)*50*time.Millisecond); err != nil {
			return Result{}, err
		}
	}
	return Result{}, lastErr
}

// isLaunchClaimRace reports whether a games_start refusal's detail text matches the
// transient "a launch claim ... was published while preparing this operation ...
// re-check games_status and retry" pattern GABS uses when a previous game's process
// claim has not yet released. Detail text is drawn from every text content entry and
// the structured "message"/"error" fields, matching how BridgeError.detail is derived
// in controller/rimgovernor/bridge.py.
func isLaunchClaimRace(refusal *Refusal) bool {
	detail := strings.ToLower(refusalDetail(refusal))
	for _, part := range []string{
		"failed to claim runtime ownership", "a launch claim for",
		"was published while preparing this operation", "re-check games_status and retry",
	} {
		if !strings.Contains(detail, part) {
			return false
		}
	}
	return true
}

func refusalDetail(refusal *Refusal) string {
	var fields struct {
		Message string `json:"message"`
		Error   string `json:"error"`
	}
	if len(refusal.Result.Structured) > 0 {
		_ = json.Unmarshal(refusal.Result.Structured, &fields)
	}
	if fields.Message != "" {
		return fields.Message
	}
	if fields.Error != "" {
		return fields.Error
	}
	if len(refusal.Result.Text) > 0 {
		return strings.Join(refusal.Result.Text, "\n")
	}
	return ""
}

// GamesStop stops the process GABS owns for this game. It never retries a game
// mutation; callers observe the receipt for reconciliation.
func (c *Client) GamesStop(ctx context.Context) (Result, error) {
	return c.operation(ctx, func(ctx context.Context, live *liveSession) (Result, error) {
		return c.core(ctx, live, "games_stop", encode(gameArgument{c.gameID}))
	})
}

// NativeCall is a narrow escape hatch for acceptance harnesses (go/internal/nativeaccept)
// that must invoke native observation/read tools whose shapes vary too widely to justify
// a reviewed typed adapter for each one (unlike Identity/Status/PlacementPreviews in
// protobuf.go). It bypasses this package's stated "reviewed reads, never a generic native
// call API" boundary by design: the boundary protects runtime callers, not disposable,
// human-reviewed acceptance harnesses that already own full lifecycle control of a
// throwaway game process. Production runtime code must not use this method; prefer a
// typed adapter in reads.go/protobuf.go instead.
func (c *Client) NativeCall(ctx context.Context, tool string, arguments json.RawMessage) (Result, error) {
	if tool == "" || len(tool) > 256 {
		return Result{}, fmt.Errorf("%w: invalid native tool name", ErrContract)
	}
	if len(arguments) == 0 {
		arguments = json.RawMessage("{}")
	}
	return c.operation(ctx, func(ctx context.Context, live *liveSession) (Result, error) {
		return c.core(ctx, live, "games_call_tool", encode(nativeArgument{c.gameID, tool, arguments}))
	})
}

// ConnectWithPoll ports the Python BridgeClient.connect() reconnect/backoff behavior:
// it accepts the games_start receipt, short-circuits if GABS reports it is already
// GABP-connected, polls games_status while a backgroundConnect handoff is pending, or
// otherwise calls games_connect and polls the native tool catalog until it is
// discoverable (bounded by a 120s deadline in each branch, matching the Python asyncio
// timeouts). It never retries a load or other game mutation after an uncertain result.
func (c *Client) ConnectWithPoll(ctx context.Context, started Result) (Result, error) {
	var startup struct {
		GabpConnected     bool `json:"gabpConnected"`
		BackgroundConnect bool `json:"backgroundConnect"`
	}
	if len(started.Structured) > 0 {
		if err := json.Unmarshal(started.Structured, &startup); err != nil {
			return Result{}, fmt.Errorf("%w: invalid games_start reply", ErrContract)
		}
	}
	if startup.GabpConnected {
		return started, nil
	}
	if startup.BackgroundConnect {
		deadline := time.Now().Add(120 * time.Second)
		for {
			status, err := c.GameStatus(ctx)
			if err != nil {
				return Result{}, err
			}
			var state struct {
				Status    string `json:"status"`
				ToolCount int    `json:"toolCount"`
			}
			if err := json.Unmarshal(status.Structured, &state); err != nil {
				return Result{}, fmt.Errorf("%w: invalid games_status reply", ErrContract)
			}
			switch state.Status {
			case "stopped", "stale-runtime-cleaned", "disconnected":
				return Result{}, fmt.Errorf("native startup stopped before GABS published tools: %s", state.Status)
			case "running", "connected":
				if state.ToolCount > 0 {
					return status, nil
				}
			}
			if time.Now().After(deadline) {
				return Result{}, fmt.Errorf("timed out waiting for GABS background connect")
			}
			if err := sleepOrDone(ctx, 500*time.Millisecond); err != nil {
				return Result{}, err
			}
		}
	}
	result, err := c.ConnectGame(ctx)
	if err != nil {
		return result, err
	}
	deadline := time.Now().Add(120 * time.Second)
	for {
		_, nameErr := c.NativeNames(ctx, "", "rimworld/load_game_ready")
		if nameErr == nil {
			return result, nil
		}
		var refusal *Refusal
		if !errors.As(nameErr, &refusal) || time.Now().After(deadline) {
			return result, nameErr
		}
		if err := sleepOrDone(ctx, 250*time.Millisecond); err != nil {
			return Result{}, err
		}
	}
}

func sleepOrDone(ctx context.Context, d time.Duration) error {
	timer := time.NewTimer(d)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-timer.C:
		return nil
	}
}
