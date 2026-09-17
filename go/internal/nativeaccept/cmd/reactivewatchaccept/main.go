// Command reactivewatchaccept proves the reactive native control path of #10
// in a private game running the GuardedConstructionFixture build: a clock
// window armed with a watched construction attempt stops at the tick the
// attempt completes (STOP_REASON_WATCH_LATCHED) with the outcome and the stop
// on one long-polled clock_read_events page, the same construction without a
// watch plays its whole budget to STOP_REASON_TICK_BUDGET, and an authority
// change observed outside any epoch arrives as an owner-less AuthorityChanged
// row within one long poll.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const sessionOwner = "native-reactive-watch-acceptance"

const (
	windowTicks   = 600
	maxWindows    = 30
	pollWaitMs    = 4000
	wakeLatencyMs = 250
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-reactive-watch-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	speed := flag.String("speed", "Superfast", "clock speed for every window: Normal, Fast or Superfast")
	timeout := flag.Duration("timeout", 15*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-reactive-watch-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	entries, _ := os.ReadDir(*output)
	if len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport("Watched construction attempt latches a clock stop at completion; long-polled event "+
		"delivery; owner-less authority change outside an epoch.", !*rendered)
	report["speed"] = *speed
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, *speed, !*rendered, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID, speed string, headless bool, report na.Report) error {
	cfg := &na.Config{Root: root, Output: output, Headless: headless, GameID: gameID}
	if err := cfg.PrepareConfig(); err != nil {
		return fmt.Errorf("prepare profile: %w", err)
	}
	game, err := cfg.GameSection()
	if err != nil {
		return err
	}
	files, err := na.PackageFiles(fmt.Sprint(game["workingDir"]))
	if err != nil {
		return err
	}
	report["package_files"] = files
	held, err := na.OpenGame(ctx, cfg)
	if err != nil {
		return err
	}
	defer held.Close(report)
	h := na.NewHarness(held.Client, output)

	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	if _, err := na.StartDebugGame(ctx, h, names, na.QuietRequired); err != nil {
		return err
	}
	frozen, err := na.FreezeNeeds(ctx, h, names)
	if err != nil {
		return err
	}
	report["frozen_needs"] = frozen
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	identityReply, err := h.Wire(ctx, "identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, loaded, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return err
	}
	loadedContext, _ := na.AsMap(loaded["context"])
	identity, _ := na.AsMap(loadedContext["identity"])

	prepared, err := h.Call(ctx, "prepare", "test/guarded_construction_prepare", map[string]any{"siteCount": 2})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(prepared["success"]); !success {
		return fmt.Errorf("guarded_construction_prepare refused: %#v", prepared)
	}
	report["prepared"] = prepared
	sites := na.AsSlice(prepared["sites"])
	if len(sites) != 2 {
		return fmt.Errorf("expected exactly 2 prepared wall sites, found %d", len(sites))
	}
	watchedSite, _ := na.AsMap(sites[0])
	baselineSite, _ := na.AsMap(sites[1])

	supervisor := &na.ScenarioClock{Wire: h.WireFunc(), Identity: identity, Owner: sessionOwner, Report: report}
	if _, err := supervisor.Acquire(ctx, "acquire"); err != nil {
		return err
	}
	c := &controller{ctx: ctx, h: h, identity: identity, supervisor: supervisor, report: report, speed: speed}

	// Watched window: the construction attempt is armed as a watch and the
	// window must stop at its completion tick, not at its budget.
	watched, err := c.place("place-watched", 1, watchedSite)
	if err != nil {
		return err
	}
	watchedRun, err := c.runUntilOutcome("watched", watched, true)
	if err != nil {
		return err
	}
	report["watched"] = watchedRun
	fmt.Printf("PASS watched: latched at tick %v of deadline %v after %d window(s); wake latency %.0f ms; ticks saved %v\n",
		watchedRun["latched_tick"], watchedRun["tick_deadline"], watchedRun["windows"], watchedRun["wake_latency_ms"], watchedRun["ticks_saved"])

	// Baseline window: the same construction without a watch plays every
	// window to its full budget; completion is only visible afterwards.
	baseline, err := c.place("place-baseline", 2, baselineSite)
	if err != nil {
		return err
	}
	baselineRun, err := c.runUntilOutcome("baseline", baseline, false)
	if err != nil {
		return err
	}
	report["baseline"] = baselineRun
	fmt.Printf("PASS baseline: completed within %d full window(s) of %d ticks\n", baselineRun["windows"], windowTicks)

	// A long poll with nothing to deliver holds the call for its wait.
	idleStarted := time.Now()
	idle, err := c.readEvents("idle-long-poll", 2000)
	if err != nil {
		return err
	}
	idleHeld := time.Since(idleStarted)
	if len(na.AsSlice(idle["events"])) != 0 || idleHeld < 1800*time.Millisecond {
		return fmt.Errorf("idle long poll returned %d event(s) after %s; expected an empty page held for ~2 s", len(na.AsSlice(idle["events"])), idleHeld)
	}
	report["idle_long_poll_ms"] = idleHeld.Milliseconds()

	// Authority changes outside an epoch reach the journal without an owner
	// and wake a waiting long poll.
	authorityRun, err := c.authorityOutsideEpoch()
	if err != nil {
		return err
	}
	report["authority_outside_epoch"] = authorityRun
	fmt.Printf("PASS authority change outside an epoch delivered in %.0f ms\n", authorityRun["wake_latency_ms"])

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}

type controller struct {
	ctx        context.Context
	h          *na.Harness
	identity   map[string]any
	supervisor *na.ScenarioClock
	report     na.Report
	speed      string
	cursor     uint64
	controls   int
}

// place admits one WoodLog Wall placement and returns its attempt key.
func (c *controller) place(label string, number int, site map[string]any) (map[string]any, error) {
	if err := c.supervisor.RenewAuthority(c.ctx); err != nil {
		return nil, err
	}
	attempt := map[string]any{"controllerSessionId": sessionOwner, "actionId": fmt.Sprintf("watch-fixture-%d", number), "attemptId": "1"}
	request := map[string]any{
		"precondition": map[string]any{"identity": c.identity, "expectedGeneration": na.GrantGeneration(c.supervisor.Grant), "attempt": attempt},
		"operation":    map[string]any{"placeBuilding": map[string]any{"placement": site}},
	}
	reply, err := c.h.Wire(c.ctx, label, "operations_execute", request)
	if err != nil {
		return nil, err
	}
	_, receipt, err := na.Outcome(reply, "receipt")
	if err != nil {
		return nil, err
	}
	if _, ok := receipt["applied"]; !ok {
		return nil, fmt.Errorf("%s: expected an applied receipt, got %#v", label, receipt)
	}
	progress, err := c.progress(label+"-progress", attempt)
	if err != nil {
		return nil, err
	}
	if _, ok := progress["pending"]; !ok {
		return nil, fmt.Errorf("%s: expected a pending construction, got %#v", label, progress)
	}
	return attempt, nil
}

func (c *controller) progress(label string, attempt map[string]any) (map[string]any, error) {
	reply, err := c.h.Wire(c.ctx, label, "receipts_observe_progress", map[string]any{"identity": c.identity, "attempt": attempt})
	if err != nil {
		return nil, err
	}
	_, progress, err := na.Outcome(reply, "progress")
	return progress, err
}

func (c *controller) status(label string) (map[string]any, error) {
	reply, err := c.h.Wire(c.ctx, label, "clock_read_status", map[string]any{"identity": c.identity})
	if err != nil {
		return nil, err
	}
	_, status, err := na.Outcome(reply, "status")
	return status, err
}

// readEvents issues one clock_read_events from the current cursor and advances
// it past the returned page.
func (c *controller) readEvents(label string, waitMs int) (map[string]any, error) {
	request := map[string]any{"identity": c.identity, "afterCursor": fmt.Sprint(c.cursor), "limit": 128}
	if waitMs > 0 {
		request["waitMs"] = waitMs
	}
	reply, err := c.h.Wire(c.ctx, label, "clock_read_events", request)
	if err != nil {
		return nil, err
	}
	_, page, err := na.Outcome(reply, "page")
	if err != nil {
		return nil, err
	}
	if gap, _ := na.AsBool(page["gap"]); gap {
		return nil, fmt.Errorf("%s: unexpected journal gap: %#v", label, page)
	}
	next, err := na.ScenarioInteger(page["nextCursor"])
	if err != nil {
		return nil, err
	}
	if next < c.cursor {
		return nil, fmt.Errorf("%s: cursor moved backwards from %d to %d", label, c.cursor, next)
	}
	c.cursor = next
	return page, nil
}

// start admits one bounded window, optionally armed with watched attempts.
func (c *controller) start(label string, watched []map[string]any) (map[string]any, error) {
	if err := c.supervisor.RenewAuthority(c.ctx); err != nil {
		return nil, err
	}
	c.controls++
	policy := map[string]any{"mode": "WATCH_MODE_COLONY", "healthDropFraction": 0.1, "minHealthFraction": 0.5,
		"hostileWithin": 40, "injuryStopCooldownMs": 0}
	if len(watched) > 0 {
		policy["watchedAttempts"] = watched
	}
	request := map[string]any{
		"authority": map[string]any{"identity": c.identity, "expectedGeneration": na.GrantGeneration(c.supervisor.Grant),
			"attempt": map[string]any{"controllerSessionId": sessionOwner, "actionId": fmt.Sprintf("%s-start-%d", label, c.controls), "attemptId": "1"}},
		"speed": "SPEED_" + strings.ToUpper(c.speed), "policy": policy, "leaseMs": 30000, "maxTicks": windowTicks,
	}
	status, err := c.supervisor.Control(c.ctx, "start", request)
	if err != nil {
		return nil, fmt.Errorf("%s: %w", label, err)
	}
	return status, nil
}

// runUntilOutcome plays bounded windows until attempt completes. With a watch
// the completing window must stop as WATCH_LATCHED at the completion tick;
// without one every window stops at its TICK_BUDGET and completion is only
// observed between windows.
func (c *controller) runUntilOutcome(label string, attempt map[string]any, watch bool) (map[string]any, error) {
	// The native journal opens lazily; an events read opens it so the status
	// below reports the retained watermark, which may sit past rows left by
	// earlier profiles' runs (its reply is irrelevant here).
	if _, err := c.h.Wire(c.ctx, label+"-prime", "clock_read_events", map[string]any{"identity": c.identity, "afterCursor": "0", "limit": 1}); err != nil {
		return nil, err
	}
	before, err := c.status(label + "-cursor")
	if err != nil {
		return nil, err
	}
	cursor, ok := before["newestCursor"]
	if !ok {
		return nil, fmt.Errorf("%s: clock status reports no journal watermark: %#v", label, before)
	}
	if c.cursor, err = na.ScenarioInteger(cursor); err != nil {
		return nil, err
	}
	var watched []map[string]any
	if watch {
		watched = []map[string]any{attempt}
	}
	result := map[string]any{"windows": 0}
	for window := 1; window <= maxWindows; window++ {
		windowLabel := fmt.Sprintf("%s-%d", label, window)
		started, err := c.start(windowLabel, watched)
		if err != nil {
			return nil, err
		}
		startTick, _ := started["startTick"].(uint64)
		tickDeadline, _ := started["tickDeadline"].(uint64)
		if tickDeadline != startTick+windowTicks {
			return nil, fmt.Errorf("%s: window did not admit %d ticks: %#v", windowLabel, windowTicks, started)
		}
		result["windows"] = window
		stop, outcome, returned, err := c.pollUntilStopped(windowLabel)
		if err != nil {
			return nil, err
		}
		reason := na.AsString(stop["reason"])
		status, err := c.status(windowLabel + "-status")
		if err != nil {
			return nil, err
		}
		stopped, ok := na.AsMap(status["stopped"])
		if !ok {
			return nil, fmt.Errorf("%s: expected a stopped status after the page, got %#v", windowLabel, status)
		}
		epoch, _ := na.AsMap(stopped["epoch"])
		lastTick, _ := na.ScenarioInteger(epoch["lastTick"])
		if verified, _ := na.AsBool(stopped["pauseVerified"]); !verified || na.AsString(stopped["reason"]) != reason {
			return nil, fmt.Errorf("%s: status does not confirm a verified %s stop: %#v", windowLabel, reason, stopped)
		}
		switch reason {
		case "STOP_REASON_TICK_BUDGET":
			if outcome != nil {
				return nil, fmt.Errorf("%s: an operation outcome was journaled without a watch stop: %#v", windowLabel, outcome)
			}
			if lastTick != tickDeadline {
				return nil, fmt.Errorf("%s: budget stop at tick %d, deadline %d", windowLabel, lastTick, tickDeadline)
			}
			progress, err := c.progress(windowLabel+"-progress", attempt)
			if err != nil {
				return nil, err
			}
			if watch {
				if _, pending := progress["pending"]; !pending {
					return nil, fmt.Errorf("%s: the watched attempt left pending without latching: %#v", windowLabel, progress)
				}
				continue
			}
			if completed, ok := na.AsMap(progress["completed"]); ok {
				if err := completedWall(completed); err != nil {
					return nil, fmt.Errorf("%s: %w", windowLabel, err)
				}
				result["tick_deadline"] = tickDeadline
				result["last_tick"] = lastTick
				return result, nil
			}
			if _, pending := progress["pending"]; !pending {
				return nil, fmt.Errorf("%s: baseline construction neither pending nor completed: %#v", windowLabel, progress)
			}
			continue
		case "STOP_REASON_WATCH_LATCHED":
			if !watch {
				return nil, fmt.Errorf("%s: a window without watches stopped as WATCH_LATCHED", windowLabel)
			}
			if outcome == nil {
				return nil, fmt.Errorf("%s: the watch stop page carried no operation outcome", windowLabel)
			}
			evidence, _ := na.AsMap(stop["watch"])
			latched, _ := na.AsMap(evidence["outcome"])
			if !na.DeepEqual(latched["attempt"], attempt) || !na.DeepEqual(outcome["attempt"], attempt) {
				return nil, fmt.Errorf("%s: latched attempt mismatch: %#v / %#v", windowLabel, latched, outcome)
			}
			completed, ok := na.AsMap(latched["completed"])
			if !ok {
				return nil, fmt.Errorf("%s: expected a completed outcome, got %#v", windowLabel, latched)
			}
			if err := completedWall(completed); err != nil {
				return nil, fmt.Errorf("%s: %w", windowLabel, err)
			}
			latchedTick, err := na.ScenarioInteger(latched["latchedTick"])
			if err != nil {
				return nil, err
			}
			deadline, _ := na.ScenarioInteger(evidence["tickDeadline"])
			if deadline != tickDeadline || latchedTick >= tickDeadline || lastTick+1 < latchedTick || lastTick > latchedTick+1 {
				return nil, fmt.Errorf("%s: stop precision: latched %d, last %d, deadline %d/%d", windowLabel, latchedTick, lastTick, deadline, tickDeadline)
			}
			observedAt, _ := na.ScenarioInteger(stop["observedAtUnixMs"])
			wake := float64(returned.UnixMilli()) - float64(observedAt)
			if wake > wakeLatencyMs {
				return nil, fmt.Errorf("%s: wake latency %.0f ms exceeds %d ms", windowLabel, wake, wakeLatencyMs)
			}
			progress, err := c.progress(windowLabel+"-progress", attempt)
			if err != nil {
				return nil, err
			}
			if _, ok := progress["completed"]; !ok {
				return nil, fmt.Errorf("%s: receipts do not confirm completion: %#v", windowLabel, progress)
			}
			result["latched_tick"] = latchedTick
			result["last_tick"] = lastTick
			result["tick_deadline"] = tickDeadline
			result["ticks_saved"] = tickDeadline - latchedTick
			result["wake_latency_ms"] = wake
			return result, nil
		default:
			return nil, fmt.Errorf("%s: unexpected stop %s: %#v", windowLabel, reason, stop)
		}
	}
	return nil, fmt.Errorf("%s: construction did not complete within %d windows", label, maxWindows)
}

// pollUntilStopped long-polls until the window's Stopped event arrives and
// returns it, any OperationOutcome on the same page, and the page's return time.
func (c *controller) pollUntilStopped(label string) (stop, outcome map[string]any, returned time.Time, err error) {
	deadline := time.Now().Add(90 * time.Second)
	for poll := 1; time.Now().Before(deadline); poll++ {
		page, err := c.readEvents(fmt.Sprintf("%s-poll-%d", label, poll), pollWaitMs)
		if err != nil {
			return nil, nil, time.Time{}, err
		}
		returned = time.Now()
		var stopped map[string]any
		for _, raw := range na.AsSlice(page["events"]) {
			event, _ := na.AsMap(raw)
			if _, ok := event["owner"]; !ok {
				return nil, nil, time.Time{}, fmt.Errorf("%s: an epoch event without an owner: %#v", label, event)
			}
			if value, ok := na.AsMap(event["operationOutcome"]); ok {
				if outcome != nil {
					return nil, nil, time.Time{}, fmt.Errorf("%s: more than one operation outcome: %#v", label, page)
				}
				outcome = value
			}
			if value, ok := na.AsMap(event["stopped"]); ok {
				if stopped != nil {
					return nil, nil, time.Time{}, fmt.Errorf("%s: more than one stop on a page: %#v", label, page)
				}
				stopped = value
				stopped["observedAtUnixMs"] = event["observedAtUnixMs"]
			}
		}
		if stopped != nil {
			if outcome != nil && na.AsString(stopped["reason"]) == "STOP_REASON_WATCH_LATCHED" {
				// The outcome row precedes the stop row on the same page.
				events := na.AsSlice(page["events"])
				last, _ := na.AsMap(events[len(events)-1])
				previous, _ := na.AsMap(events[len(events)-2])
				if _, ok := last["stopped"]; !ok {
					return nil, nil, time.Time{}, fmt.Errorf("%s: the stop is not the last row of its page", label)
				}
				if _, ok := previous["operationOutcome"]; !ok {
					return nil, nil, time.Time{}, fmt.Errorf("%s: the outcome does not precede its stop", label)
				}
			}
			return stopped, outcome, returned, nil
		}
	}
	return nil, nil, time.Time{}, fmt.Errorf("%s: no stop within 90 s", label)
}

// authorityOutsideEpoch revokes the harness grant while no epoch runs and
// expects the owner-less AuthorityChanged row on the next long poll, which
// must answer at once rather than hold for its wait. (Harness calls are
// sequential, so the wake of an already-waiting poll is covered by the
// watched window above, which shares the journal's append path.)
func (c *controller) authorityOutsideEpoch() (map[string]any, error) {
	if err := c.supervisor.RenewAuthority(c.ctx); err != nil {
		return nil, err
	}
	generation := na.GrantGeneration(c.supervisor.Grant)
	reply, err := c.h.Wire(c.ctx, "revoke-outside-epoch", "authority_control", map[string]any{"revoke": map[string]any{
		"identity": c.identity, "expectedGeneration": fmt.Sprint(generation), "reason": "REVOCATION_REASON_MANUAL",
	}})
	if err != nil {
		return nil, err
	}
	if _, _, err := na.Outcome(reply, "revoked"); err != nil {
		return nil, err
	}
	started := time.Now()
	page, err := c.readEvents("authority-long-poll", pollWaitMs)
	if err != nil {
		return nil, err
	}
	latency := float64(time.Since(started).Milliseconds())
	events := na.AsSlice(page["events"])
	if len(events) != 1 {
		return nil, fmt.Errorf("authority long poll: expected exactly one row, got %#v", page)
	}
	event, _ := na.AsMap(events[0])
	if _, ok := event["owner"]; ok {
		return nil, fmt.Errorf("authority change outside an epoch carried an owner: %#v", event)
	}
	changed, ok := na.AsMap(event["authorityChanged"])
	if !ok {
		return nil, fmt.Errorf("expected an authorityChanged row, got %#v", event)
	}
	previous, _ := na.ScenarioInteger(changed["previousGeneration"])
	next, _ := na.ScenarioInteger(changed["generation"])
	active, _ := na.AsBool(changed["active"])
	if previous != generation || next != generation+1 || active {
		return nil, fmt.Errorf("authority change does not describe the manual revoke of generation %d: %#v", generation, changed)
	}
	if latency > 500 {
		return nil, fmt.Errorf("a long poll with a row available took %.0f ms; it must not wait", latency)
	}
	return map[string]any{"event": event, "wake_latency_ms": latency}, nil
}

func completedWall(completed map[string]any) error {
	evidence, _ := na.AsMap(completed["evidence"])
	effect, _ := na.AsMap(evidence["construction"])
	present, _ := na.AsBool(effect["present"])
	if na.AsString(effect["stage"]) != "CONSTRUCTION_STAGE_BUILDING" || !present || na.AsString(effect["defName"]) != "Wall" {
		return fmt.Errorf("expected a present built Wall, got %#v", effect)
	}
	return nil
}
