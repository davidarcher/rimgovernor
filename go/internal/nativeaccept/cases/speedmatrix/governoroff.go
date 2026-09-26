package speedmatrix

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// runGovernorOff is the governor-off row (#621): the same stage reloaded
// and frozen exactly as a governed row, then played natively at the row's
// speed (the test acceleration where the row asks for it) with no
// controller attached, until the tick has advanced by the budget. The
// harness's own tick reads pace the wait; there is nothing else to wait
// on. Its metrics row carries wall TPS and the tick budget under the same
// keys as a governed row, zeros for the governor's counters, and
// governor_off so a reader tells the rows apart. Nothing submits plans,
// so the row records its native counters but takes no part in the outcome
// comparison; it still requires healthy colonists and a fresh stage.
func (m *matrix) runGovernorOff(ctx context.Context, c na.SpeedCase) (err error) {
	output := filepath.Join(m.s.Config().Output, c.Name)
	if err := os.MkdirAll(output, 0755); err != nil {
		return err
	}
	report := na.NewReport("speed case "+c.Name, m.s.Config().Headless)
	report["case"] = c
	defer func() {
		if err != nil {
			report["error"] = err.Error()
		} else {
			report["passed"] = true
		}
		report.Finalize(output)
	}()
	h, err := m.s.Reattach(ctx)
	if err != nil {
		return err
	}
	h.Output = output
	if _, err := h.Call(ctx, "load-stage", "rimworld/load_game_ready", map[string]any{
		"saveName": m.p.save, "readiness": "visual", "timeoutMs": 90000, "ignoreModCompatibility": false,
	}); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	identity, err := readIdentity(ctx, h)
	if err != nil {
		return err
	}
	frozen, err := na.FreezeNeeds(ctx, h, nil)
	if err != nil {
		return err
	}
	report["frozen_needs"] = frozen
	before, err := m.control(ctx, h, identity, "control-before")
	if err != nil {
		return err
	}
	report["control_before"] = before
	if na.AsNumber(before["storedUnits"]) != 0 || na.AsNumber(before["wallsBuilt"]) != 0 || na.AsNumber(before["wallBlueprints"]) != 0 {
		return fmt.Errorf("stage is not fresh after reload: %#v", before)
	}
	startTick := uint64(na.AsNumber(before["tick"]))
	if _, err := h.Call(ctx, "play", "rimworld/set_time_speed", map[string]any{"speed": c.Speed, "ultraSpeedBoost": c.TestAcceleration}); err != nil {
		return err
	}
	resumedAt := time.Now()
	lastTick := startTick
	reads := 0
	// Nothing records a flight timeline here, so the row's observation and
	// frame accounts come off its own tick reads' reply envelopes (#642):
	// the ungoverned update intervals on the same save and renderer.
	account := &bridge.NativeAccount{}
	waitErr := na.WaitProgress(ctx, na.Wait{Stall: na.StallBudget(), Interval: 2 * time.Second},
		func(ctx context.Context) (string, bool, error) {
			tick, err := readTickAccounted(ctx, h, fmt.Sprintf("tick-%d", reads), account)
			reads++
			if err != nil {
				return "", false, err
			}
			lastTick = tick
			return na.Signature(lastTick), lastTick >= startTick+m.p.ticks, nil
		})
	wallSeconds := time.Since(resumedAt).Seconds()
	if _, pauseErr := h.Call(ctx, "pause-after", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); pauseErr != nil && waitErr == nil {
		waitErr = pauseErr
	}
	report["wait"] = map[string]any{"start_tick": startTick, "last_tick": lastTick, "wall_seconds": wallSeconds, "tick_reads": reads}
	if waitErr != nil {
		return fmt.Errorf("tick budget wait (start %d, last %d, want +%d): %w", startTick, lastTick, m.p.ticks, waitErr)
	}
	after, err := m.control(ctx, h, identity, "control-after")
	if err != nil {
		return err
	}
	report["control_after"] = after
	pawnsReply, err := h.Wire(ctx, "pawns-after", "observations_list_pawns", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "filter": map[string]any{"colonist": true},
	})
	if err != nil {
		return err
	}
	if err := na.RequireHealthyColonists(pawnsReply); err != nil {
		return err
	}
	_, observed, _ := na.Outcome(pawnsReply, "observed")
	outcome := na.OutcomeFromControl(c.Name, after)
	outcome.HealthyColonists = len(na.AsSlice(observed["pawns"]))
	report["outcome"] = outcome
	wallTPS := 0.0
	if wallSeconds > 0 && lastTick > startTick {
		wallTPS = float64(lastTick-startTick) / wallSeconds
	}
	metrics := map[string]any{
		"case": c.Name, "speed": c.Speed, "test_acceleration": c.TestAcceleration, "blind_ticks": c.BlindTicks, "governor_off": true,
		"ticks_advanced": lastTick - startTick, "wall_seconds": wallSeconds, "budget_wall_tps": wallTPS, "wall_tps": wallTPS,
		"paused_ms": 0, "running_ms": uint64(wallSeconds * 1000), "paused_fraction_native": 0.0, "native_pause_samples": 0,
		"paused_fraction": 0.0, "paused_samples": 0, "clock_samples": 0, "paused_sampled_seconds": 0.0,
		"steps": 0, "reads_per_step": 0.0, "stops": 0, "budget_stops": 0, "reactive_stops": 0, "tick_reads": reads,
	}
	for key, value := range observationRow(account.Observation(), account.Frames()) {
		metrics[key] = value
	}
	report["metrics"] = metrics
	appendMetrics(m.report, metrics)
	return nil
}

// readIdentity reads the loaded identity the stage reload produced.
func readIdentity(ctx context.Context, h *na.Harness) (map[string]any, error) {
	reply, err := h.Wire(ctx, "identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return nil, err
	}
	_, loaded, err := na.Outcome(reply, "loaded")
	if err != nil {
		return nil, err
	}
	loadedContext, _ := na.AsMap(loaded["context"])
	identity, _ := na.AsMap(loadedContext["identity"])
	return identity, nil
}

// readTickAccounted is readTick over the raw reply envelope, so the reply's
// companion timing block feeds the row's observation and frame accounts
// (#642). Harness.Wire unwraps the ProtoJSON payload and drops the block.
func readTickAccounted(ctx context.Context, h *na.Harness, label string, account *bridge.NativeAccount) (uint64, error) {
	envelope, err := h.Call(ctx, label, "rimgovernor/lifecycle_read_tick", map[string]any{"request": "{}"})
	if err != nil {
		return 0, err
	}
	account.Reply(envelope)
	payload, ok := envelope["payload"].(string)
	if !ok {
		return 0, fmt.Errorf("lifecycle_read_tick: reply did not preserve the ProtoJSON string envelope")
	}
	var message map[string]any
	if err := json.Unmarshal([]byte(payload), &message); err != nil {
		return 0, fmt.Errorf("lifecycle_read_tick: invalid ProtoJSON reply: %w", err)
	}
	_, loaded, err := na.Outcome(message, "loaded")
	if err != nil {
		return 0, err
	}
	loadedContext, _ := na.AsMap(loaded["context"])
	return uint64(na.AsNumber(loadedContext["tick"])), nil
}
