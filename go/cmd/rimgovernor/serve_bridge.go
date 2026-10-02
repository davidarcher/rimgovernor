package main

import (
	"context"
	"fmt"
	"io"
	"time"
)

// bridgeReattacher is the part of bridge.Client the supervisor drives:
// Disconnected reports the loss of the game session, Reattach restores it
// against the game that kept running.
type bridgeReattacher interface {
	Disconnected() <-chan struct{}
	Reattach(context.Context) error
	GameClosed(context.Context) bool
}

const (
	// bridgeReattachTimeout bounds one attempt: a fresh game connection plus the
	// games_start/connect handshake, whose own poll deadlines are 120s.
	bridgeReattachTimeout = 3 * time.Minute
	bridgeReattachBackoff = time.Second
	bridgeReattachMaxWait = 30 * time.Second
	// bridgeClosedWait bounds the wait for a closing game to exit before the
	// first reattach; later attempts look again without waiting.
	bridgeClosedWait = 10 * time.Second
)

// superviseBridge restores the game session for the life of the service
// (#87). A lost session is reattached with exponential backoff until it
// succeeds or ctx ends; nothing else is retried, so in-flight and later
// native calls fail with bridge.ErrDisconnected until the reattach lands and
// the controller re-observes before writing. Authority native revoked while
// the bot was away (DISCONNECT after the typed clock lease lapsed) is
// re-acquired by the auto resumer, not here.
//
// A game the player closed (it exited 0) is not relaunched: closed runs and
// the supervisor returns, so the service ends with the game. A crash, a kill
// or a silent death still reattaches.
func superviseBridge(ctx context.Context, client bridgeReattacher, out io.Writer, closed func()) {
	wait := bridgeReattachBackoff
	for {
		select {
		case <-ctx.Done():
			return
		case <-client.Disconnected():
		}
		if ctx.Err() != nil {
			return
		}
		fmt.Fprintln(out, "bridge: game session lost; reattaching to the running game")
		for attempt := 1; ; attempt++ {
			bound := bridgeClosedWait
			if attempt > 1 {
				bound = 0
			}
			checkCtx, cancel := context.WithTimeout(ctx, bound)
			gone := client.GameClosed(checkCtx)
			cancel()
			if gone {
				fmt.Fprintln(out, "bridge: the game was closed; stopping the service")
				closed()
				return
			}
			attemptCtx, cancel := context.WithTimeout(ctx, bridgeReattachTimeout)
			err := client.Reattach(attemptCtx)
			cancel()
			if err == nil {
				fmt.Fprintf(out, "bridge: reattached after %d attempt(s)\n", attempt)
				wait = bridgeReattachBackoff
				break
			}
			if ctx.Err() != nil {
				return
			}
			fmt.Fprintf(out, "bridge: reattach attempt %d failed: %v; retrying in %s\n", attempt, err, wait)
			timer := time.NewTimer(wait)
			select {
			case <-ctx.Done():
				timer.Stop()
				return
			case <-timer.C:
			}
			wait = min(wait*2, bridgeReattachMaxWait)
		}
	}
}
