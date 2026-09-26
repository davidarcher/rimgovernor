package smoke

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// The held poll and how the case establishes that the host is holding it.
//
// The claim is an ordering, not a latency: an independent read issued while
// clock_read_events is held must be answered before the poll is released. A
// serial host can only write the read's response after the poll's, so
// comparing the two completion instants decides it without a wall-clock
// bound (#617).
//
// The precondition — the host has entered the wait, not merely that the
// client goroutine started — comes from home/runtime_health's
// journal.waiters, the count of long polls the host holds right now. A
// build that predates the field leaves the case on the timing
// approximation: a fixed settle, a floor on how long the poll was held and
// a bound on the read under it (#115 measured ~300 ms per call, so a read
// answered behind a 5 s poll is unmistakable). The approximation is
// reported as such and never silently substituted.
//
// dispatchHoldMs is a hang guard, not a deadline anything asserts on. It is
// the host's own ceiling (NativeClockTools.MaxWaitMs); a larger wait is
// refused outright rather than held.
const (
	dispatchHoldMs      = 5000
	dispatchHoldTimeout = dispatchHoldMs * time.Millisecond
	dispatchWakeMargin  = 500 * time.Millisecond
	dispatchObserveFor  = 2 * time.Second
	dispatchProbeEvery  = 150 * time.Millisecond
	dispatchSettle      = 300 * time.Millisecond
	dispatchReadBound   = 1500 * time.Millisecond
	dispatchHeldFloor   = 2500 * time.Millisecond
	dispatchHoldTrials  = 3
	dispatchObservedWay = "observed"
	dispatchTimedWay    = "timing-approximation"
)

func init() {
	cases.Register(cases.Case{
		Name:   "smoke/dispatch",
		Scope:  "#227/#617: companion tools dispatch off the GABP reader. home/runtime_health reports the extension dispatch patch installed with every discovered tool rewrapped, and with a clock_read_events long poll established as held (journal.waiters, or a declared timing approximation on a build without it) an independent identity read is answered before the poll is released.",
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
			// Whether this build can be asked how many polls it holds
			// decides which precondition the trials establish.
			journal, _ := na.AsMap(health["journal"])
			_, observable := journal["waiters"]
			way := dispatchTimedWay
			if observable {
				way = dispatchObservedWay
			}
			s.Report()["held_poll_precondition"] = way

			// Hold the poll on its own goroutine (the client admits up to
			// bridge.MaxConcurrentCalls calls, #105) and read identity under
			// it. A poll the host never entered, or released before the read
			// was issued, proves nothing about dispatch, so the trial is
			// retried — an unestablished precondition is a failure, never a
			// pass.
			var trials []map[string]any
			for trial := 1; trial <= dispatchHoldTrials; trial++ {
				// The cursor to hold from, re-read each trial: a row appended
				// between the read and the poll answers the poll at once, so a
				// cursor from an earlier trial is no use.
				cursor, err := newestCursor(ctx, s, trial)
				if err != nil {
					return err
				}
				request, _ := json.Marshal(map[string]any{"identity": s.Identity(), "afterCursor": cursor, "limit": 128, "waitMs": dispatchHoldMs})
				pollArgs, _ := json.Marshal(map[string]any{"request": string(request)})
				type pollResult struct {
					at   time.Time
					held time.Duration
					err  error
				}
				done := make(chan pollResult, 1)
				released := make(chan struct{})
				began := time.Now()
				go func() {
					// The poll goes straight to the client: Harness.Call
					// serializes its evidence, and this call is meant to
					// overlap the ones under it.
					result, err := h.Client.NativeCall(ctx, "rimgovernor/clock_read_events", pollArgs)
					at := time.Now()
					if err == nil {
						err = pollRefusal(result.Structured)
					}
					close(released)
					done <- pollResult{at, time.Since(began), err}
				}()

				row := map[string]any{"trial": trial, "precondition": way}
				held, probes, observeErr := establishHeld(ctx, s, observable, trial, released)
				row["probes"] = probes
				row["establish_ms"] = time.Since(began).Milliseconds()
				if observeErr != nil {
					row["precondition_error"] = observeErr.Error()
				}

				var read time.Duration
				var readAt time.Time
				var readErr error
				if held {
					readBegan := time.Now()
					_, readErr = h.Wire(ctx, fmt.Sprintf("identity-under-poll-%d", trial), "lifecycle_read_identity", map[string]any{})
					readAt, read = time.Now(), time.Since(readBegan)
					row["identity_ms"] = read.Milliseconds()
				}
				poll := <-done
				row["poll_held_ms"] = poll.held.Milliseconds()
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
				if !held {
					continue // the host never held the poll; nothing was tested
				}
				// The ordering: the read's response arrived while the poll's
				// had not. A serial host answers in the opposite order.
				answeredFirst := readAt.Before(poll.at)
				row["read_before_release"] = answeredFirst
				if !answeredFirst {
					if poll.held < dispatchHoldTimeout-dispatchWakeMargin {
						// A journal row woke the poll while the read was in
						// flight: the ordering says nothing, so the trial is
						// inconclusive rather than a regression.
						row["inconclusive"] = "poll woken by a journal row under the read"
						continue
					}
					return fmt.Errorf("identity (%s) was answered only after the %s poll was released: companion calls are serial on the host (trials %v)", read, poll.held, trials)
				}
				if !observable {
					// The approximation additionally needs the poll to have
					// been held well past a round trip, and the read to have
					// come back inside one.
					if poll.held < dispatchHeldFloor {
						continue // the journal moved; the poll was not held long enough to tell
					}
					if read > dispatchReadBound {
						return fmt.Errorf("identity took %s under a %s held poll: companion calls are serial on the host (trials %v)", read, poll.held, trials)
					}
				}
				return nil
			}
			return fmt.Errorf("never established a held clock_read_events poll (%s) across %d trials: %v", way, dispatchHoldTrials, trials)
		},
	})
}

// establishHeld waits until the host is holding the trial's poll and
// reports how it was established: home/runtime_health's journal.waiters
// when the installed build exposes it (each probe is itself an independent
// call travelling under the poll), otherwise a fixed settle. It returns
// false when the poll was released, or the count never rose, before the
// independent read could be issued — the trial then tested nothing and must
// not pass.
func establishHeld(ctx context.Context, s cases.Session, observable bool, trial int, released <-chan struct{}) (bool, int, error) {
	if !observable {
		select {
		case <-time.After(dispatchSettle):
		case <-released:
			return false, 0, fmt.Errorf("poll released inside the %s settle", dispatchSettle)
		case <-ctx.Done():
			return false, 0, ctx.Err()
		}
		return true, 0, nil
	}
	h := s.Harness()
	deadline := time.Now().Add(dispatchObserveFor)
	for probe := 1; ; probe++ {
		health, err := h.Call(ctx, fmt.Sprintf("waiters-%d-%d", trial, probe), "home/runtime_health", map[string]any{})
		if err != nil {
			return false, probe, err
		}
		journal, _ := na.AsMap(health["journal"])
		waiters, _ := journal["waiters"].(float64)
		if waiters >= 1 {
			return true, probe, nil
		}
		select {
		case <-released:
			return false, probe, fmt.Errorf("poll released before journal.waiters rose")
		case <-ctx.Done():
			return false, probe, ctx.Err()
		case <-time.After(dispatchProbeEvery):
		}
		if time.Now().After(deadline) {
			return false, probe, fmt.Errorf("journal.waiters stayed 0 for %s under a held poll", dispatchObserveFor)
		}
	}
}

// newestCursor is the journal cursor a poll can wait past: the newest row
// retained right now. It is newestCursor, not the page's nextCursor -- a
// window is capped at limit rows, so on a journal with more retained rows
// than that nextCursor still has rows behind it and a poll from there is
// answered at once instead of waiting.
func newestCursor(ctx context.Context, s cases.Session, trial int) (string, error) {
	h := s.Harness()
	reply, err := h.Wire(ctx, fmt.Sprintf("events-cursor-%d", trial), "clock_read_events", map[string]any{"identity": s.Identity(), "afterCursor": "0", "limit": 1})
	if err != nil {
		return "", err
	}
	_, page, err := na.Outcome(reply, "page")
	if err != nil {
		return "", err
	}
	cursor := na.AsString(page["newestCursor"])
	if cursor == "" {
		return "", fmt.Errorf("events page without newestCursor: %#v", page)
	}
	return cursor, nil
}

// pollRefusal is the failure a held clock_read_events reply carries, if any:
// a refused request (a wait past the host ceiling, a cursor past the
// journal) returns as promptly as a released poll would, and must never be
// read as one.
func pollRefusal(structured json.RawMessage) error {
	var envelope struct {
		Payload string `json:"payload"`
	}
	if err := json.Unmarshal(structured, &envelope); err != nil {
		return fmt.Errorf("held clock_read_events reply: %w", err)
	}
	var reply map[string]any
	if err := json.Unmarshal([]byte(envelope.Payload), &reply); err != nil {
		return fmt.Errorf("held clock_read_events payload: %w", err)
	}
	if failure, ok := na.AsMap(reply["failure"]); ok {
		return fmt.Errorf("held clock_read_events refused: %v", failure)
	}
	return nil
}
