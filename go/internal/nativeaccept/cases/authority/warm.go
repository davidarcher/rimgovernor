// authority/warm (the former warmauthorityaccept) is the native acceptance
// for #119: a RimWorld process that hosted a controller killed with its
// authority and typed clock epoch still granted must keep the next
// controller's authority after the game is unloaded and another save is
// loaded into the same process, and the next controller must still read
// the clock journal.
//
// Phase 1 plays the killed controller on a bridge session: grant Auto, start
// a typed epoch, then drop the session with the epoch running and the grant
// active (Release, exactly as a serve-driven case hands the game to its
// service, then nothing). Close returns the process to the main menu with
// the epoch's static state still pointing at the disposed game. Phase 2 is
// the next controller, a second harness in practice: it prepares the
// profile again, attaches to the kept process, loads the cached debug start
// again (a new load token), pages the clock journal from cursor 0 -- the
// rows phase 1 wrote must still be there, continuity intact -- then grants
// Auto and must hold it for the whole hold window without any native
// revocation.
//
// The Go side of #119 was the profile preparation wiping the journal
// directory under the kept process: every clock_read_events then failed
// cursor continuity, the service's clock inbox died, and its authority
// refresh went with it. That is what the phase-2 journal read catches.
package authority

import (
	"context"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const (
	warmController = "authority-warm"
	// warmHoldTicks is how long phase 2's grant must stay active, in game
	// time: an in-game hour the game actually runs (RunUntil), so the hold
	// costs seconds at Ultrafast and the same ticks at any speed (#267).
	warmHoldTicks = na.TicksPerHour
)

func init() {
	cases.Register(cases.Case{
		Name: "authority/warm",
		Scope: "A process that hosted a controller killed mid-epoch (authority Auto, typed clock epoch running, session " +
			"dropped) keeps the next controller's Auto grant after unload and reload of another save into the same process (#119).",
		Start:  cases.Owned{},
		NoKeep: true,
		Budget: 5 * time.Minute,
		Run:    runWarm,
	})
}

// runWarm owns the process: phase 1 keeps it whatever the environment
// says, phase 2 must attach to it, and the case retires it at the end.
func runWarm(ctx context.Context, s cases.Session) error {
	cfg, report := s.Config(), s.Report()
	output := cfg.Output
	killed, err := na.OpenGame(ctx, cfg)
	if err != nil {
		return err
	}
	killed.Keep = true
	phase1 := map[string]any{"reused": killed.Reused}
	report["phase1_killed_controller"] = phase1
	h := na.NewHarness(killed.Client, output)
	identity, err := warmStage(ctx, h, "p1")
	if err != nil {
		killed.Close(report)
		return err
	}
	granted, err := na.GrantAuto(ctx, h.WireFunc(), "p1-grant", identity)
	if err != nil {
		killed.Close(report)
		return err
	}
	generation := na.GrantGeneration(granted)
	phase1["identity"], phase1["generation"] = identity, generation
	startReply, err := h.Wire(ctx, "p1-clock-start", "clock_start", map[string]any{
		"authority": map[string]any{
			"identity":           identity,
			"expectedGeneration": fmt.Sprint(generation),
			"attempt":            map[string]any{"controllerSessionId": warmController, "actionId": "clock-start", "attemptId": "1"},
		},
		"speed":    "SPEED_SUPERFAST",
		"leaseMs":  30000,
		"maxTicks": 600000,
		"policy": map[string]any{
			"mode": "WATCH_MODE_COLONY", "healthDropFraction": 0.5, "minHealthFraction": 0.2,
			"hostileWithin": 40, "injuryStopCooldownMs": 0,
		},
	})
	if err != nil {
		killed.Close(report)
		return fmt.Errorf("p1-clock-start: %w", err)
	}
	if _, receipt, err := na.Outcome(startReply, "receipt"); err != nil {
		killed.Close(report)
		return fmt.Errorf("p1-clock-start: expected a receipt: %w", err)
	} else if applied, _ := na.AsMap(receipt["applied"]); applied != nil {
		status, _ := na.AsMap(applied["status"])
		if _, running := status["running"]; !running {
			killed.Close(report)
			return fmt.Errorf("p1-clock-start: expected a running epoch, got %v", receipt)
		}
	}
	// The controller vanishes: the session drops with the epoch running and
	// the grant active. Close reattaches and unloads to the main menu.
	if err := killed.Release(); err != nil {
		return err
	}
	killed.Keep = true
	killed.Close(report)
	if !killed.Keep {
		return fmt.Errorf("phase 1 did not keep the process: %v", report["game_reuse"])
	}
	phase1["stop"] = report["stop"]
	return warmPhase2(ctx, cfg, output, na.AsString(identity["loadToken"]), report)
}

// warmPhase2 is the next controller attaching to the kept process. It is
// a separate harness in practice, so it prepares the profile again.
func warmPhase2(ctx context.Context, cfg *na.Config, output, previousLoadToken string, report na.Report) error {
	if err := cfg.PrepareConfig(); err != nil {
		return fmt.Errorf("phase 2 prepare profile: %w", err)
	}
	next, err := na.OpenGame(ctx, cfg)
	if err != nil {
		return err
	}
	// An Owned case leaves the root's process stopped.
	next.Keep = false
	defer next.Close(report)
	phase2 := map[string]any{"reused": next.Reused}
	report["phase2_next_controller"] = phase2
	if !next.Reused {
		return fmt.Errorf("phase 2 launched a fresh process instead of attaching to the kept one")
	}
	h := na.NewHarness(next.Client, output)
	identity, err := warmStage(ctx, h, "p2")
	if err != nil {
		return err
	}
	if na.AsString(identity["loadToken"]) == previousLoadToken {
		return fmt.Errorf("phase 2 reloaded under the same load token %v", identity)
	}
	clockBefore, err := h.Wire(ctx, "p2-clock-before", "clock_read_status", map[string]any{"identity": identity})
	if err != nil {
		return err
	}
	phase2["clock_before_grant"] = clockBefore
	// The journal phase 1 wrote to belongs to the kept process: a page from
	// cursor 0 must succeed with its rows intact.
	eventsReply, err := h.Wire(ctx, "p2-clock-events", "clock_read_events", map[string]any{"identity": identity, "afterCursor": "0", "limit": 50})
	if err != nil {
		return err
	}
	_, page, err := na.Outcome(eventsReply, "page")
	if err != nil {
		return fmt.Errorf("phase 2 could not page the kept process's clock journal from cursor 0: %w", err)
	}
	newest := int64(na.AsNumber(page["newestCursor"]))
	lost := int64(na.AsNumber(page["lostCount"]))
	phase2["clock_journal"] = map[string]any{"newestCursor": newest, "lostCount": lost, "gap": page["gap"], "events": len(na.AsSlice(page["events"]))}
	if gap, _ := page["gap"].(bool); gap || lost != 0 {
		return fmt.Errorf("phase 2 found the kept process's clock journal broken (gap %v, lost %d): %v", gap, lost, page)
	}
	if newest < 1 {
		return fmt.Errorf("phase 2 found no journal rows from phase 1's epoch: %v", page)
	}
	statusBefore, before, err := na.AuthorityStatus(ctx, h.WireFunc(), "p2-status", identity)
	if err != nil {
		return err
	}
	phase2["authority_before_grant"] = statusBefore
	granted, err := na.GrantAutoAt(ctx, h.WireFunc(), "p2-grant", identity, before)
	if err != nil {
		return err
	}
	generation := na.GrantGeneration(granted)
	phase2["identity"], phase2["generation"] = identity, generation
	// The grant must hold for the whole window, with the game running:
	// every read Active(Auto) at the granted generation. The first loss is
	// the finding.
	reads := 0
	held := time.Now()
	elapsed, err := na.RunUntil(ctx, h, "p2-hold", warmHoldTicks, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		status, observed, err := na.AuthorityStatus(ctx, h.WireFunc(), fmt.Sprintf("p2-hold-%03d", reads), identity)
		if err != nil {
			return "", false, err
		}
		reads++
		active, isActive := na.AsMap(status["active"])
		if !isActive || na.AsString(active["mode"]) != "MODE_AUTO" || observed != generation {
			clock, _ := h.Wire(ctx, "p2-clock-at-loss", "clock_read_status", map[string]any{"identity": identity})
			phase2["loss"] = map[string]any{"after_reads": reads, "elapsed_ms": time.Since(held).Milliseconds(),
				"authority": status, "generation": observed, "clock": clock}
			return "", false, fmt.Errorf("phase 2 lost the Auto grant at generation %d after %d reads: %v", generation, reads, status)
		}
		return "", false, nil
	})
	if err != nil {
		return err
	}
	phase2["held_reads"], phase2["held_ticks"] = reads, elapsed
	if _, err := na.RevokeManual(ctx, h.WireFunc(), "p2-release", identity, granted); err != nil {
		return err
	}
	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), cfg.Headless)
}

// warmStage loads the cached debug start, pauses it and returns its identity.
func warmStage(ctx context.Context, h *na.Harness, prefix string) (map[string]any, error) {
	if _, err := na.StartDebugGame(ctx, h, nil, na.QuietRequired); err != nil {
		return nil, err
	}
	if _, err := h.Call(ctx, prefix+"-pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return nil, err
	}
	identityReply, err := h.Wire(ctx, prefix+"-identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return nil, err
	}
	_, loaded, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return nil, err
	}
	loadedContext, _ := na.AsMap(loaded["context"])
	identity, _ := na.AsMap(loadedContext["identity"])
	if identity == nil {
		return nil, fmt.Errorf("%s: no identity in %v", prefix, identityReply)
	}
	return identity, nil
}
