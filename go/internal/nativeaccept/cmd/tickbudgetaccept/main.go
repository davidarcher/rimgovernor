// Command tickbudgetaccept proves exact
// native execution tick boundaries and external clock ownership in a private game.
package main

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"flag"
	"fmt"
	"os"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func randomOwner() string {
	buf := make([]byte, 16)
	_, _ = rand.Read(buf)
	return "rimgovernor-" + hex.EncodeToString(buf)
}

const baselineSave = "RimGovernor-tribal8-baseline"

var speeds = []string{"Normal", "Fast", "Superfast"}
var budgets = []uint64{1, 37, 600}

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-tick-budget-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 20*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-tick-budget-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	report := na.NewReport("Verify exact native execution boundaries and external clock ownership in a private game.", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, !*rendered, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID string, headless bool, report na.Report) error {
	cfg := &na.Config{Root: root, Output: output, Headless: headless, GameID: gameID}
	// The save carries its own expansion list; a Core-only profile would
	// refuse it. OpenSession enables them for a Save start.
	s, err := na.OpenSession(ctx, cfg, report, na.Save{Name: baselineSave}, na.QuietIfAvailable)
	if err != nil {
		return err
	}
	defer s.Close()
	h := s.Harness

	loadBaseline := func(label string) error {
		_, err := h.Call(ctx, label, "rimworld/load_game_ready", map[string]any{
			"saveName": baselineSave, "readiness": "visual", "timeoutMs": 90000, "ignoreModCompatibility": false,
		})
		return err
	}
	status := func(label string) (map[string]any, error) {
		return h.Call(ctx, label, "home/supervised_play", map[string]any{"op": "status"})
	}
	stopped := func(label string) (map[string]any, error) {
		deadline := time.Now().Add(20 * time.Second)
		for {
			result, err := status(label)
			if err != nil {
				return nil, err
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

	clock := na.NewSupervisedPlayClock(randomOwner())
	if _, err := clock.Change(ctx, h, "initial-pause", "Paused", nil); err != nil {
		return err
	}

	var cases []any
	for _, speed := range speeds {
		for _, budget := range budgets {
			budget := budget
			label := fmt.Sprintf("%s-%d", speed, budget)
			if err := loadBaseline("load-" + label); err != nil {
				return err
			}
			clock = na.NewSupervisedPlayClock(randomOwner())
			if _, err := clock.Change(ctx, h, "pause-"+label, "Paused", nil); err != nil {
				return err
			}
			start, err := clock.Change(ctx, h, "start-"+label, speed, &budget)
			if err != nil {
				return err
			}
			active, _ := na.AsBool(start["active"])
			if !active && na.AsString(start["stopReason"]) != "tick_budget" {
				return fmt.Errorf("%s: unexpected start result %#v", label, start)
			}
			if active {
				if _, err := clock.Poll(ctx, h, "renew-"+label); err != nil {
					return err
				}
			}
			end, err := stopped("stopped-" + label)
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
			events, err := clock.Poll(ctx, h, "drain-"+label)
			if err != nil {
				return err
			}
			tickBudgetEvents := 0
			for _, raw := range events {
				row, _ := na.AsMap(raw)
				if na.AsString(row["kind"]) == "tick_budget" {
					tickBudgetEvents++
				}
			}
			if tickBudgetEvents > 1 {
				return fmt.Errorf("%s: expected at most one tick_budget event, found %d", label, tickBudgetEvents)
			}
			if clock.Hold != "" {
				return fmt.Errorf("%s: unexpected clock hold %q", label, clock.Hold)
			}
			cases = append(cases, map[string]any{"speed": speed, "budget": budget, "start": start, "stop": end, "readback": observed, "stable": stable})
			fmt.Printf("PASS %s: exactly %d ticks\n", speed, budget)
		}
	}
	report["cases"] = cases

	for _, external := range []struct{ speed, reason string }{{"Paused", "external_pause"}, {"Fast", "external_speed_changed"}} {
		maxTicks := uint64(3000)
		if _, err := clock.Change(ctx, h, "external-resume-"+external.reason, "Normal", &maxTicks); err != nil {
			return err
		}
		if _, err := h.Call(ctx, "external-override-"+external.reason, "rimworld/set_time_speed", map[string]any{
			"speed": external.speed, "ultraSpeedBoost": false,
		}); err != nil {
			return err
		}
		end, err := stopped("external-stopped-" + external.reason)
		if err != nil {
			return err
		}
		if na.AsString(end["stopReason"]) != external.reason {
			return fmt.Errorf("external-%s: unexpected stop result %#v", external.reason, end)
		}
		if _, err := clock.Poll(ctx, h, "external-poll-"+external.reason); err != nil {
			return err
		}
		resumeTicks := uint64(100)
		if _, err := clock.Change(ctx, h, "external-resume-attempt-"+external.reason, "Normal", &resumeTicks); err == nil {
			return fmt.Errorf("external hold %q was overridden", external.reason)
		} else if !strings.Contains(err.Error(), "player must enable") {
			return err
		}
		clock.AllowResume()
		cases = append(cases, map[string]any{"external_speed": external.speed, "stop": end})
		fmt.Printf("PASS %s: explicit resume required\n", external.reason)
	}
	report["cases"] = cases

	leaseReply, err := h.Call(ctx, "lease-start", "home/supervised_play", map[string]any{
		"op": "start", "owner": clock.Owner, "speed": "Normal", "leaseMs": 1000, "maxTicks": 3000,
		"hostileWithin": 40, "injuryStopCooldownMs": 0,
	})
	if err != nil {
		return err
	}
	if active, _ := na.AsBool(leaseReply["active"]); !active {
		return fmt.Errorf("lease-start: expected an active clock, got %#v", leaseReply)
	}
	expired, err := stopped("lease-expired")
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
	cases = append(cases, map[string]any{"lease_expiry": expired})
	report["cases"] = cases
	fmt.Println("PASS lease expiry before tick limit")

	// Loading a native save retires the old watcher without claiming the new
	// session's clock or carrying its old tick deadline over.
	if _, err := h.Call(ctx, "prelude-start", "home/supervised_play", map[string]any{
		"op": "start", "owner": clock.Owner, "speed": "Normal", "leaseMs": 15000, "maxTicks": 3000,
		"hostileWithin": 40, "injuryStopCooldownMs": 0,
	}); err != nil {
		return err
	}
	if err := loadBaseline("reload-baseline"); err != nil {
		return err
	}
	changed, err := stopped("session-changed")
	if err != nil {
		return err
	}
	if na.AsString(changed["stopReason"]) != "session_changed" {
		return fmt.Errorf("session-changed: unexpected stop result %#v", changed)
	}
	cases = append(cases, map[string]any{"load_change": changed})
	report["cases"] = cases

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}
