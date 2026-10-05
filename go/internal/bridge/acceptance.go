package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"time"
)

// GamesStart attaches to the game recorded running under the launch state
// dir, or launches it. It never connects, loads or advances the game;
// ConnectGame or ConnectWithPoll must run afterward before any native tool
// is discoverable.
func (c *Client) GamesStart(ctx context.Context) (Result, error) {
	return c.operation(ctx, AdmissionControl, func(ctx context.Context, live *liveSession) (Result, error) {
		return c.core(ctx, live, "games_start", encode(gameArgument{c.gameID}))
	})
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

// GamesStop stops the game recorded under the launch state dir (a game not
// running is already stopped). It never retries a game mutation; callers
// observe the receipt for reconciliation.
func (c *Client) GamesStop(ctx context.Context) (Result, error) {
	return c.operation(ctx, AdmissionControl, func(ctx context.Context, live *liveSession) (Result, error) {
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
	return c.operation(ctx, admissionClassOf(tool), func(ctx context.Context, live *liveSession) (Result, error) {
		return c.core(ctx, live, "games_call_tool", encode(nativeArgument{c.gameID, tool, arguments}))
	})
}

// ConnectWithPoll connects after games_start: it short-circuits when the
// start receipt reports the session already connected, otherwise calls
// games_connect until the game's endpoint answers (a booting game refuses
// with "no attachable endpoint yet"; other refusals get a short retry
// limit) and then polls the native tool catalog until it is discoverable,
// each bounded by 120s. It never retries a load or other game mutation
// after an uncertain result.
func (c *Client) ConnectWithPoll(ctx context.Context, started Result) (Result, error) {
	var startup struct {
		GabpConnected bool `json:"gabpConnected"`
	}
	if len(started.Structured) > 0 {
		if err := json.Unmarshal(started.Structured, &startup); err != nil {
			return Result{}, fmt.Errorf("%w: invalid games_start reply", ErrContract)
		}
	}
	if startup.GabpConnected {
		return started, nil
	}
	result, err := c.ConnectGame(ctx)
	connectDeadline := time.Now().Add(120 * time.Second)
	for attempt := 0; err != nil && time.Now().Before(connectDeadline); attempt++ {
		var refusal *Refusal
		if !errors.As(err, &refusal) {
			break
		}
		if attempt >= 5 && !strings.Contains(refusalDetail(refusal), "no attachable endpoint yet") {
			break
		}
		if waitErr := sleepOrDone(ctx, min(time.Duration(attempt+1)*300*time.Millisecond, time.Second)); waitErr != nil {
			return result, waitErr
		}
		result, err = c.ConnectGame(ctx)
	}
	if err != nil {
		var refusal *Refusal
		if errors.As(err, &refusal) {
			return result, fmt.Errorf("%w: %s", err, refusalDetail(refusal))
		}
		return result, err
	}
	deadline := time.Now().Add(120 * time.Second)
	for {
		_, nameErr := c.NativeNames(ctx, "", "rimgovernor/load_game_ready")
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
