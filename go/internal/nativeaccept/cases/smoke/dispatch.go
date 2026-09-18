package smoke

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// The held poll and the bound on the read issued under it. A serial host
// answers the read only after the poll returns (>= holdMs); an unblocked
// one answers it in a transport round trip, well under the bound even with
// peer games loading the machine (#115 measured ~300 ms per call then).
const (
	dispatchHoldMs     = 4000
	dispatchReadBound  = 1500 * time.Millisecond
	dispatchHeldFloor  = 2500 * time.Millisecond
	dispatchHoldTrials = 3
)

func init() {
	cases.Register(cases.Case{
		Name:   "smoke/dispatch",
		Scope:  "#227: companion tools dispatch off the GABP reader. home/runtime_health reports the extension dispatch patch installed with every discovered tool rewrapped, and an identity read issued while a 4 s clock_read_events long poll is held returns within a round trip instead of behind the poll.",
		Start:  cases.DebugStart{},
		Budget: 5 * time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			h := s.Harness()
			health, err := h.Call(ctx, "runtime-health", "home/runtime_health", map[string]any{})
			if err != nil {
				return err
			}
			dispatch, _ := na.AsMap(health["extensionDispatch"])
			installed, _ := na.AsBool(dispatch["installed"])
			rewrapped, _ := dispatch["rewrapped"].(float64)
			s.Report()["extension_dispatch"] = dispatch
			if !installed || rewrapped < 1 {
				return fmt.Errorf("extension dispatch patch not in place: %#v", dispatch)
			}
			if !na.Contains(s.Names(), "rimgovernor/clock_read_events") {
				return fmt.Errorf("missing rimgovernor/clock_read_events in discovery")
			}

			// The journal cursor to hold from: a page past everything retained.
			first, err := h.Wire(ctx, "events-cursor", "clock_read_events", map[string]any{"identity": s.Identity(), "afterCursor": "0", "limit": 128})
			if err != nil {
				return err
			}
			_, page, err := na.Outcome(first, "page")
			if err != nil {
				return err
			}
			cursor := na.AsString(page["nextCursor"])
			if cursor == "" {
				return fmt.Errorf("events page without nextCursor: %#v", page)
			}

			// Hold the poll on its own goroutine (the client admits up to
			// bridge.MaxConcurrentCalls calls, #105) and read identity under
			// it. A poll that returns early on a journal event proves
			// nothing about blocking, so retry the hold a few times.
			var trials []map[string]any
			for trial := 1; trial <= dispatchHoldTrials; trial++ {
				request, _ := json.Marshal(map[string]any{"identity": s.Identity(), "afterCursor": cursor, "limit": 128, "waitMs": dispatchHoldMs})
				pollArgs, _ := json.Marshal(map[string]any{"request": string(request)})
				type pollResult struct {
					held time.Duration
					err  error
				}
				done := make(chan pollResult, 1)
				began := time.Now()
				go func() {
					_, err := h.Client.NativeCall(ctx, "rimgovernor/clock_read_events", pollArgs)
					done <- pollResult{time.Since(began), err}
				}()
				time.Sleep(300 * time.Millisecond)
				readBegan := time.Now()
				_, readErr := h.Wire(ctx, fmt.Sprintf("identity-under-poll-%d", trial), "lifecycle_read_identity", map[string]any{})
				read := time.Since(readBegan)
				poll := <-done
				row := map[string]any{"trial": trial, "poll_held_ms": poll.held.Milliseconds(), "identity_ms": read.Milliseconds()}
				if poll.err != nil {
					row["poll_error"] = poll.err.Error()
				}
				trials = append(trials, row)
				s.Report()["dispatch_trials"] = trials
				if readErr != nil {
					return readErr
				}
				if poll.err != nil {
					return fmt.Errorf("held clock_read_events: %w", poll.err)
				}
				if poll.held < dispatchHeldFloor {
					continue // the journal moved; the poll was not held long enough to tell
				}
				if read > dispatchReadBound {
					return fmt.Errorf("identity took %s under a %s held poll: companion calls are serial on the host (trials %v)", read, poll.held, trials)
				}
				return nil
			}
			return fmt.Errorf("clock_read_events never held for %s across %d trials: %v", dispatchHeldFloor, dispatchHoldTrials, trials)
		},
	})
}
