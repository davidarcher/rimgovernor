// Command disconnectaccept is the native-acceptance run for #35 M1: a bot
// that vanishes mid-epoch loses authority. The only native-observable sign
// of a dropped controller is a typed clock lease lapsing, so the harness
// grants Auto, starts a typed epoch with the shortest admissible lease and
// never renews it. Native must stop the clock as lease_expired, revoke
// authority as REVOCATION_REASON_DISCONNECT at the next generation, and then
// admit a fresh SetMode(Auto) at that generation exactly as a reconnecting
// controller would issue it. A legacy home/supervised_play lease lapsing must
// not touch authority: it carries no authority precondition.
//
// The final case is the Go transport side (#87): the harness kills its own
// GABS process mid-epoch, the bridge client must report the loss and fail
// fast, Reattach must restore service against the game that kept running,
// and the re-observed authority must be the DISCONNECT revocation that a
// fresh SetMode(Auto) then clears.
package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"os"
	"sync/atomic"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const controller = "disconnectaccept"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-disconnect-acceptance)")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	timeout := flag.Duration("timeout", 600*time.Second, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-disconnect-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	report := na.NewReport("Typed clock lease expiry revokes native authority as REVOCATION_REASON_DISCONNECT at generation+1 with the clock stopped lease_expired and pause verified; a fresh SetMode(Auto) at that generation is granted; a legacy supervised_play lease lapsing leaves authority untouched. Killing the harness's own GABS mid-epoch disconnects the Go bridge client, Reattach restores it against the running game, and the re-observed DISCONNECT revocation is cleared by a fresh grant.", !*rendered)
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
	if err := cfg.PrepareConfig(); err != nil {
		return fmt.Errorf("prepare profile: %w", err)
	}
	gabsExecutable, err := na.GABSExecutable(root, cfg.Configuration)
	if err != nil {
		return err
	}
	var gabsPID atomic.Int64
	client, err := na.OpenSessionWith(ctx, bridge.ProcessConfig{
		Executable: gabsExecutable, ConfigDir: cfg.Configuration, GameID: gameID, Timeout: 90 * time.Second,
		Spawned: func(pid int) { gabsPID.Store(int64(pid)) },
	})
	if err != nil {
		return err
	}
	defer func() {
		stopCtx, stopCancel := context.WithTimeout(context.Background(), 60*time.Second)
		defer stopCancel()
		if stopped, err := client.GamesStop(stopCtx); err == nil {
			report["stop"] = string(stopped.Envelope)
		} else {
			report["stop_error"] = err.Error()
		}
		_ = client.Close()
	}()
	h := na.NewHarness(client, output)
	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	for _, tool := range []string{"rimgovernor/authority_read_status", "rimgovernor/authority_control", "rimgovernor/clock_start", "rimgovernor/clock_read_status", "home/supervised_play"} {
		if !na.Contains(names, tool) {
			return fmt.Errorf("missing %s in discovery", tool)
		}
	}
	if _, err := na.StartDebugGame(ctx, h, names, na.Loud); err != nil {
		return err
	}
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

	// Setup: grant Auto at the fresh game's generation. Authority is
	// initialized by the game's own update polling; a read that lands before
	// that reports it unavailable, and the first generation is always 1.
	generation, state, err := authority(ctx, h, "status-fresh", identity)
	if err != nil {
		return err
	}
	if _, unavailable := state["unavailable"]; unavailable {
		generation = 1
	}
	granted, err := grant(ctx, h, "grant", identity, generation)
	if err != nil {
		return err
	}
	if granted != generation+1 {
		return fmt.Errorf("grant: expected generation %d, got %d", generation+1, granted)
	}
	report["setup"] = map[string]any{"generationBefore": generation, "generationGranted": granted}

	// Case 1: a typed epoch whose lease is never renewed. 1000ms is the
	// shortest lease clock_start admits.
	startReply, err := h.Wire(ctx, "clock-start", "clock_start", map[string]any{
		"authority": map[string]any{
			"identity":           identity,
			"expectedGeneration": fmt.Sprint(granted),
			"attempt":            map[string]any{"controllerSessionId": controller, "actionId": "clock-start", "attemptId": "1"},
		},
		"speed":    "SPEED_NORMAL",
		"leaseMs":  1000,
		"maxTicks": 60000,
		"policy": map[string]any{
			"mode": "WATCH_MODE_COLONY", "healthDropFraction": 0.5, "minHealthFraction": 0.2,
			"hostileWithin": 40, "injuryStopCooldownMs": 0,
		},
	})
	if err != nil {
		return fmt.Errorf("clock-start: %w", err)
	}
	_, receipt, err := na.Outcome(startReply, "receipt")
	if err != nil {
		return fmt.Errorf("clock-start: expected a receipt: %w", err)
	}
	applied, _ := na.AsMap(receipt["applied"])
	appliedStatus, _ := na.AsMap(applied["status"])
	if _, ok := appliedStatus["running"]; !ok {
		return fmt.Errorf("clock-start: expected a running epoch, got %v", receipt)
	}

	stopped, err := waitStopped(ctx, h, identity, "clock-poll")
	if err != nil {
		return err
	}
	if na.AsString(stopped["reason"]) != "STOP_REASON_LEASE_EXPIRED" {
		return fmt.Errorf("clock-stopped: expected STOP_REASON_LEASE_EXPIRED, got %v", stopped)
	}
	if verified, _ := na.AsBool(stopped["pauseVerified"]); !verified {
		return fmt.Errorf("clock-stopped: pause was not verified: %v", stopped)
	}
	afterGeneration, state, err := authority(ctx, h, "status-after-expiry", identity)
	if err != nil {
		return err
	}
	inactive, ok := na.AsMap(state["inactive"])
	if !ok {
		return fmt.Errorf("status-after-expiry: expected inactive authority, got %v", state)
	}
	if na.AsString(inactive["reason"]) != "REVOCATION_REASON_DISCONNECT" {
		return fmt.Errorf("status-after-expiry: expected REVOCATION_REASON_DISCONNECT, got %v", inactive)
	}
	if afterGeneration != granted+1 {
		return fmt.Errorf("status-after-expiry: expected generation %d, got %d", granted+1, afterGeneration)
	}
	report["case_lease_expiry_disconnect"] = map[string]any{"stopped": stopped, "inactive": inactive, "generation": afterGeneration}

	// Case 2: the reconnecting controller grants Auto again at the observed
	// generation; a grant at the pre-disconnect generation must be stale.
	staleReply, err := h.Wire(ctx, "regrant-stale", "authority_control", map[string]any{"setMode": map[string]any{
		"identity": identity, "expectedGeneration": fmt.Sprint(granted), "mode": "MODE_AUTO",
	}})
	if err != nil {
		return fmt.Errorf("regrant-stale: %w", err)
	}
	if code, failed := na.FailureCode(staleReply); !failed || code != "FAILURE_CODE_STALE_GENERATION" {
		return fmt.Errorf("regrant-stale: expected FAILURE_CODE_STALE_GENERATION, got %v", staleReply)
	}
	regranted, err := grant(ctx, h, "regrant", identity, afterGeneration)
	if err != nil {
		return err
	}
	if regranted != afterGeneration+1 {
		return fmt.Errorf("regrant: expected generation %d, got %d", afterGeneration+1, regranted)
	}
	report["case_regrant"] = map[string]any{"generation": regranted}

	// Case 3: a legacy supervised_play lease lapsing carries no authority
	// precondition and must leave the fresh Auto grant alone.
	legacy, err := h.Call(ctx, "legacy-start", "home/supervised_play", map[string]any{
		"op": "start", "owner": controller, "speed": "Normal", "leaseMs": 1000, "maxTicks": 60000,
		"hostileWithin": 40, "injuryStopCooldownMs": 0,
	})
	if err != nil {
		return fmt.Errorf("legacy-start: %w", err)
	}
	if active, _ := na.AsBool(legacy["active"]); !active {
		return fmt.Errorf("legacy-start: expected an active clock, got %v", legacy)
	}
	deadline := time.Now().Add(20 * time.Second)
	for {
		status, err := h.Call(ctx, "legacy-status", "home/supervised_play", map[string]any{"op": "status", "owner": controller})
		if err != nil {
			return fmt.Errorf("legacy-status: %w", err)
		}
		if active, _ := na.AsBool(status["active"]); !active {
			if na.AsString(status["stopReason"]) != "lease_expired" {
				return fmt.Errorf("legacy-status: expected lease_expired, got %v", status)
			}
			break
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("legacy-status: lease did not expire in time")
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(250 * time.Millisecond):
		}
	}
	legacyGeneration, legacyState, err := authority(ctx, h, "status-after-legacy", identity)
	if err != nil {
		return err
	}
	active, ok := na.AsMap(legacyState["active"])
	if !ok || na.AsString(active["mode"]) != "MODE_AUTO" || legacyGeneration != regranted {
		return fmt.Errorf("status-after-legacy: expected Auto at generation %d untouched, got %v at %d", regranted, legacyState, legacyGeneration)
	}
	report["case_legacy_lease_untouched"] = map[string]any{"generation": legacyGeneration}

	// Case 4 (#87): the GABS process itself dies mid-epoch. The typed lease
	// is again the only native-observable sign; on the Go side the bridge
	// client must notice the loss without a call, fail fast, and reattach.
	transport, err := transportDrop(ctx, h, identity, legacyGeneration, &gabsPID)
	if err != nil {
		return err
	}
	report["case_transport_drop"] = transport

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}

// authority reads the native generation and the state message (one of
// unavailable/inactive/active as its single key) for the identity.
func authority(ctx context.Context, h *na.Harness, label string, identity map[string]any) (uint64, map[string]any, error) {
	reply, err := h.Wire(ctx, label, "authority_read_status", map[string]any{"identity": identity})
	if err != nil {
		return 0, nil, fmt.Errorf("%s: %w", label, err)
	}
	_, status, err := na.Outcome(reply, "status")
	if err != nil {
		return 0, nil, fmt.Errorf("%s: expected a status outcome: %w", label, err)
	}
	context, _ := na.AsMap(status["context"])
	generation := uint64(na.AsNumber(context["nativeGeneration"]))
	state := map[string]any{}
	for _, key := range []string{"unavailable", "inactive", "active"} {
		if value, ok := status[key]; ok {
			state[key] = value
		}
	}
	if len(state) != 1 {
		return 0, nil, fmt.Errorf("%s: expected exactly one authority state, got %v", label, status)
	}
	if _, unavailable := state["unavailable"]; !unavailable && generation == 0 {
		return 0, nil, fmt.Errorf("%s: missing native generation: %v", label, status)
	}
	return generation, state, nil
}

// grant issues SetMode(Auto) at the expected generation and returns the
// granted generation.
func grant(ctx context.Context, h *na.Harness, label string, identity map[string]any, expected uint64) (uint64, error) {
	reply, err := h.Wire(ctx, label, "authority_control", map[string]any{"setMode": map[string]any{
		"identity": identity, "expectedGeneration": fmt.Sprint(expected), "mode": "MODE_AUTO",
	}})
	if err != nil {
		return 0, fmt.Errorf("%s: %w", label, err)
	}
	_, granted, err := na.Outcome(reply, "granted")
	if err != nil {
		return 0, fmt.Errorf("%s: expected a granted outcome: %w", label, err)
	}
	authority, _ := na.AsMap(granted["authority"])
	if na.AsString(authority["mode"]) != "MODE_AUTO" {
		return 0, fmt.Errorf("%s: expected MODE_AUTO, got %v", label, granted)
	}
	context, _ := na.AsMap(granted["context"])
	return uint64(na.AsNumber(context["nativeGeneration"])), nil
}

// waitStopped polls clock_read_status until the typed epoch reports Stopped.
func waitStopped(ctx context.Context, h *na.Harness, identity map[string]any, label string) (map[string]any, error) {
	var stopped, status map[string]any
	attempt := 0
	err := na.WaitProgress(ctx, na.Wait{Ceiling: 20 * time.Second, Interval: 250 * time.Millisecond}, func(ctx context.Context) (string, bool, error) {
		reply, err := h.Wire(ctx, fmt.Sprintf("%s-%d", label, attempt), "clock_read_status", map[string]any{"identity": identity})
		attempt++
		if err != nil {
			return "", false, fmt.Errorf("%s: %w", label, err)
		}
		if _, status, err = na.Outcome(reply, "status"); err != nil {
			return "", false, fmt.Errorf("%s: expected a status outcome: %w", label, err)
		}
		var ok bool
		stopped, ok = na.AsMap(status["stopped"])
		return na.Signature(status), ok, nil
	})
	if err != nil {
		return nil, fmt.Errorf("%s: epoch did not stop (%v): %w", label, status, err)
	}
	return stopped, nil
}

// transportDrop starts a typed epoch at generation, kills the harness's own
// GABS (never the game), and checks the bridge client's loss signal, its
// fail-fast error, the reattach against the running game, the DISCONNECT
// revocation native recorded meanwhile and the fresh grant that clears it.
func transportDrop(ctx context.Context, h *na.Harness, identity map[string]any, generation uint64, gabsPID *atomic.Int64) (map[string]any, error) {
	startReply, err := h.Wire(ctx, "drop-clock-start", "clock_start", map[string]any{
		"authority": map[string]any{
			"identity":           identity,
			"expectedGeneration": fmt.Sprint(generation),
			"attempt":            map[string]any{"controllerSessionId": controller, "actionId": "drop-clock-start", "attemptId": "1"},
		},
		"speed":    "SPEED_NORMAL",
		"leaseMs":  1000,
		"maxTicks": 60000,
		"policy": map[string]any{
			"mode": "WATCH_MODE_COLONY", "healthDropFraction": 0.5, "minHealthFraction": 0.2,
			"hostileWithin": 40, "injuryStopCooldownMs": 0,
		},
	})
	if err != nil {
		return nil, fmt.Errorf("drop-clock-start: %w", err)
	}
	if _, _, err := na.Outcome(startReply, "receipt"); err != nil {
		return nil, fmt.Errorf("drop-clock-start: expected a receipt: %w", err)
	}
	pid := int(gabsPID.Load())
	if pid <= 0 {
		return nil, errors.New("drop: GABS PID unknown")
	}
	lost := h.Client.Disconnected()
	select {
	case <-lost:
		return nil, errors.New("drop: client reported disconnected before the drop")
	default:
	}
	gabs, err := os.FindProcess(pid)
	if err != nil {
		return nil, fmt.Errorf("drop: find GABS %d: %w", pid, err)
	}
	dropped := time.Now()
	if err := gabs.Kill(); err != nil {
		return nil, fmt.Errorf("drop: kill GABS %d: %w", pid, err)
	}
	select {
	case <-lost:
	case <-time.After(15 * time.Second):
		return nil, errors.New("drop: bridge client never reported the lost session")
	case <-ctx.Done():
		return nil, ctx.Err()
	}
	noticed := time.Since(dropped)
	if _, err := h.Wire(ctx, "drop-read-while-down", "authority_read_status", map[string]any{"identity": identity}); !errors.Is(err, bridge.ErrDisconnected) {
		return nil, fmt.Errorf("drop-read-while-down: expected bridge.ErrDisconnected, got %v", err)
	}
	// Reattach: the dead GABS's claim on the game may take a moment to be
	// recognised as stale, the same tolerance nativeaccept's game reuse gives
	// a reopened session.
	var attempts int
	var reattachErr error
	reattachDeadline := time.Now().Add(60 * time.Second)
	for {
		attempts++
		attemptCtx, cancel := context.WithTimeout(ctx, 60*time.Second)
		reattachErr = h.Client.Reattach(attemptCtx)
		cancel()
		if reattachErr == nil || time.Now().After(reattachDeadline) {
			break
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(time.Second):
		}
	}
	if reattachErr != nil {
		return nil, fmt.Errorf("drop-reattach: %d attempt(s): %w", attempts, reattachErr)
	}
	reattached := time.Since(dropped)
	if int(gabsPID.Load()) == pid {
		return nil, errors.New("drop-reattach: no fresh GABS process was spawned")
	}
	stopped, err := waitStopped(ctx, h, identity, "drop-clock-poll")
	if err != nil {
		return nil, err
	}
	if na.AsString(stopped["reason"]) != "STOP_REASON_LEASE_EXPIRED" {
		return nil, fmt.Errorf("drop-clock-stopped: expected STOP_REASON_LEASE_EXPIRED, got %v", stopped)
	}
	afterGeneration, state, err := authority(ctx, h, "drop-status-after", identity)
	if err != nil {
		return nil, err
	}
	inactive, ok := na.AsMap(state["inactive"])
	if !ok || na.AsString(inactive["reason"]) != "REVOCATION_REASON_DISCONNECT" {
		return nil, fmt.Errorf("drop-status-after: expected inactive REVOCATION_REASON_DISCONNECT, got %v", state)
	}
	if afterGeneration != generation+1 {
		return nil, fmt.Errorf("drop-status-after: expected generation %d, got %d", generation+1, afterGeneration)
	}
	regranted, err := grant(ctx, h, "drop-regrant", identity, afterGeneration)
	if err != nil {
		return nil, err
	}
	if regranted != afterGeneration+1 {
		return nil, fmt.Errorf("drop-regrant: expected generation %d, got %d", afterGeneration+1, regranted)
	}
	return map[string]any{
		"gabsPidKilled": pid, "gabsPidAfter": gabsPID.Load(),
		"lossNoticedMs": noticed.Milliseconds(), "reattachedMs": reattached.Milliseconds(), "reattachAttempts": attempts,
		"stopped": stopped, "inactive": inactive, "generationAfterDrop": afterGeneration, "generationRegranted": regranted,
	}, nil
}
