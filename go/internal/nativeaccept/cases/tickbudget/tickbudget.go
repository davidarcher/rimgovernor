// Package tickbudget proves exact native execution tick boundaries and
// external clock ownership in a private game: every speed runs exactly
// its tick budget and pauses verified, an external pause or speed change
// stops the supervised clock and holds it until the player resumes, a
// lease expires before its tick limit, and loading a save retires the
// old watcher without claiming the new session's clock.
package tickbudget

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"os"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func randomOwner() string {
	buf := make([]byte, 16)
	_, _ = rand.Read(buf)
	return "rimgovernor-" + hex.EncodeToString(buf)
}

const baselineSave = "RimGovernor-tribal8-baseline"

var speeds = []string{"Normal", "Fast", "Superfast"}
var budgets = []uint64{1, 37, 600}

func init() {
	cases.Register(cases.Case{
		Name:   "tickbudget/boundaries",
		Scope:  "Verify exact native execution boundaries and external clock ownership in a private game.",
		Start:  cases.Save{Name: baselineSave},
		Quiet:  na.QuietIfAvailable,
		Reason: "also runs against the production mod build, which has no quiet-storyteller fixture; the assertions are about the clock, not events",
		Budget: 5 * time.Minute,
		Matrix: true,
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h := s.Harness()
	// load reloads the baseline and returns a typed clock, holding Auto, on
	// the fresh load's identity: a load changes the identity and the
	// authority generation, so neither carries across.
	load := func(label string) (*na.ScenarioClock, error) {
		if _, err := h.Call(ctx, label, "rimworld/load_game_ready", map[string]any{
			"saveName": baselineSave, "readiness": "visual", "timeoutMs": 90000, "ignoreModCompatibility": false,
		}); err != nil {
			return nil, err
		}
		identity, err := na.ReadIdentity(ctx, h, label+"-identity")
		if err != nil {
			return nil, err
		}
		clock := &na.ScenarioClock{Wire: h.WireFunc(), Identity: identity, Owner: randomOwner(), Report: report}
		if _, err := clock.Acquire(ctx, label+"-acquire"); err != nil {
			return nil, err
		}
		return clock, nil
	}
	stopped := func(clock *na.ScenarioClock, label string) (map[string]any, error) {
		deadline := time.Now().Add(20 * time.Second)
		for {
			result, err := clock.Call(ctx, "status", nil)
			if err != nil {
				return nil, fmt.Errorf("%s: %w", label, err)
			}
			if active, _ := na.AsBool(result["active"]); !active {
				return result, nil
			}
			if time.Now().After(deadline) {
				return nil, fmt.Errorf("%s: timed out waiting for the native clock to stop", label)
			}
			select {
			case <-ctx.Done():
				return nil, ctx.Err()
			case <-time.After(100 * time.Millisecond):
			}
		}
	}

	var clock *na.ScenarioClock
	var results []any
	for _, speed := range speeds {
		for _, budget := range budgets {
			label := fmt.Sprintf("%s-%d", speed, budget)
			var err error
			if clock, err = load("load-" + label); err != nil {
				return err
			}
			start, err := clock.Change(ctx, speed, budget)
			if err != nil {
				return fmt.Errorf("start-%s: %w", label, err)
			}
			clock.SeekEvents(start)
			end, err := stopped(clock, "stopped-"+label)
			if err != nil {
				return err
			}
			pauseVerified, _ := na.AsBool(end["pauseVerified"])
			if na.AsString(end["stopReason"]) != "tick_budget" || !pauseVerified {
				return fmt.Errorf("%s: unexpected stop result %#v", label, end)
			}
			if na.AsNumber(end["tickDeadline"]) != na.AsNumber(start["startTick"])+float64(budget) {
				return fmt.Errorf("%s: tick deadline did not match start tick plus budget: start=%#v end=%#v", label, start, end)
			}
			observed, err := h.Call(ctx, "readback-"+label, "home/status", map[string]any{"colonists": false, "threats": false})
			if err != nil {
				return err
			}
			observedTime, _ := na.AsMap(observed["time"])
			if na.AsNumber(observedTime["ticksGame"]) != na.AsNumber(end["tickDeadline"]) {
				return fmt.Errorf("%s: readback ticksGame did not match tickDeadline: %#v", label, observed)
			}
			if paused, _ := na.AsBool(observedTime["paused"]); !paused {
				return fmt.Errorf("%s: readback was not paused: %#v", label, observed)
			}
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-time.After(250 * time.Millisecond):
			}
			stable, err := h.Call(ctx, "stable-"+label, "home/status", map[string]any{"colonists": false, "threats": false})
			if err != nil {
				return err
			}
			stableTime, _ := na.AsMap(stable["time"])
			if na.AsNumber(stableTime["ticksGame"]) != na.AsNumber(end["tickDeadline"]) {
				return fmt.Errorf("%s: game ticks moved after the verified pause: %#v", label, stable)
			}
			events, err := clock.Poll(ctx)
			if err != nil {
				return fmt.Errorf("drain-%s: %w", label, err)
			}
			tickBudgetEvents := 0
			for _, raw := range events {
				if strings.Contains(fmt.Sprint(raw), "STOP_REASON_TICK_BUDGET") {
					tickBudgetEvents++
				}
			}
			if tickBudgetEvents > 1 {
				return fmt.Errorf("%s: expected at most one tick_budget event, found %d", label, tickBudgetEvents)
			}
			if clock.Hold != "" {
				return fmt.Errorf("%s: unexpected clock hold %q", label, clock.Hold)
			}
			results = append(results, map[string]any{"speed": speed, "budget": budget, "start": start, "stop": end, "readback": observed, "stable": stable})
			fmt.Printf("PASS %s: exactly %d ticks\n", speed, budget)
		}
	}
	report["results"] = results

	for _, external := range []struct{ speed, reason string }{{"Paused", "external_pause"}, {"Fast", "external_speed_changed"}} {
		if _, err := clock.Change(ctx, "Normal", 3000); err != nil {
			return fmt.Errorf("external-resume-%s: %w", external.reason, err)
		}
		if _, err := h.Call(ctx, "external-override-"+external.reason, "rimworld/set_time_speed", map[string]any{
			"speed": external.speed, "ultraSpeedBoost": false,
		}); err != nil {
			return err
		}
		end, err := stopped(clock, "external-stopped-"+external.reason)
		if err != nil {
			return err
		}
		if na.AsString(end["stopReason"]) != external.reason {
			return fmt.Errorf("external-%s: unexpected stop result %#v", external.reason, end)
		}
		if _, err := clock.Change(ctx, "Normal", 100); err == nil {
			return fmt.Errorf("external hold %q was overridden", external.reason)
		} else if !strings.Contains(err.Error(), "external clock hold") {
			return err
		}
		clock.Hold = ""
		results = append(results, map[string]any{"external_speed": external.speed, "stop": end})
		fmt.Printf("PASS %s: explicit resume required\n", external.reason)
	}
	report["results"] = results

	// A lease that is never renewed stops the clock before its tick limit.
	clock.LeaseMs = 1000
	if _, err := clock.Change(ctx, "Normal", 3000); err != nil {
		return fmt.Errorf("lease-start: %w", err)
	}
	clock.LeaseMs = 0
	expired, err := stopped(clock, "lease-expired")
	if err != nil {
		return err
	}
	expiredVerified, _ := na.AsBool(expired["pauseVerified"])
	if na.AsString(expired["stopReason"]) != "lease_expired" || !expiredVerified {
		return fmt.Errorf("lease-expired: unexpected stop result %#v", expired)
	}
	if na.AsNumber(expired["lastTick"]) >= na.AsNumber(expired["tickDeadline"]) {
		return fmt.Errorf("lease-expired: expected lastTick before tickDeadline: %#v", expired)
	}
	results = append(results, map[string]any{"lease_expiry": expired})
	report["results"] = results
	fmt.Println("PASS lease expiry before tick limit")

	// Loading a native save retires the old watcher without claiming the new
	// session's clock or carrying its old tick deadline over.
	if clock, err = load("prelude-load"); err != nil {
		return err
	}
	if _, err := clock.Change(ctx, "Normal", 3000); err != nil {
		return fmt.Errorf("prelude-start: %w", err)
	}
	if clock, err = load("reload-baseline"); err != nil {
		return err
	}
	changed, err := stopped(clock, "session-changed")
	if err != nil {
		return err
	}
	if na.AsString(changed["stopReason"]) != "session_changed" {
		return fmt.Errorf("session-changed: unexpected stop result %#v", changed)
	}
	results = append(results, map[string]any{"load_change": changed})
	report["results"] = results

	return checkStartupLog(s)
}

// checkStartupLog is the run's last assertion: no native error in the
// game's startup log.
func checkStartupLog(s cases.Session) error {
	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}
