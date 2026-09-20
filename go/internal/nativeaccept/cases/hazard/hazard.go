// The hazard/* cases prove the supervisor's declared detection bounds
// (#626, docs/developers/architecture/hazard-detection-bounds.md): each
// injects one hazard class into a clock window running at Ultrafast, the
// speed that carries the most ticks per frame, and asserts the stop's
// detect_ticks (detected_tick less occurrence_tick, #621) and the gap from
// the injection tick to the detection both lie within the class's bound.
// The letter case delivers through the real LetterStack callback
// (test/deliver_letter); the others use test/hazard_inject.
package hazard

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// probeIntervalTicks and hookedBoundTicks mirror SupervisedPlayHazards.cs;
// the clock status reports the native declaration and the case checks the
// two agree.
const (
	probeIntervalTicks = 30
	hookedBoundTicks   = 1
	// windowTicks is the clock budget: long enough for the injection to
	// land mid-window at Ultrafast, short enough that a missed stop fails
	// fast.
	windowTicks = 6000
	// settleTicks is how far the window runs before the injection, so the
	// detection is measured under a moving clock, never at the start
	// baseline.
	settleTicks = 300
)

// hazardCase is one class: how it is injected and the stop it must raise.
type hazardCase struct {
	name   string
	class  string
	bound  int64
	scope  string
	inject func(ctx context.Context, s cases.Session) (int64, map[string]any, error)
}

func init() {
	for _, hc := range []hazardCase{
		{name: "hazard/letter", class: "notification_batch", bound: probeIntervalTicks,
			scope:  "A letter received mid-window at Ultrafast stops the clock as notification_batch within the 30-tick bound (the letter hook lands it next tick).",
			inject: injectLetter},
		{name: "hazard/hostile", class: "hostile", bound: probeIntervalTicks,
			scope:  "A hostile pawn spawned near a colonist mid-window at Ultrafast stops the clock as hostile within the 30-tick bound (the spawn hook lands it next tick).",
			inject: injectOp("hostile")},
		{name: "hazard/downed", class: "colonist_downed", bound: hookedBoundTicks,
			scope:  "A colonist downed mid-window at Ultrafast stops the clock as colonist_downed within the hooked 1-tick bound.",
			inject: injectOp("downed")},
		{name: "hazard/injury", class: "colonist_injury", bound: probeIntervalTicks,
			scope:  "A colonist wounded mid-window at Ultrafast stops the clock as colonist_injury within the 30-tick probe bound.",
			inject: injectOp("injury")},
		{name: "hazard/predator", class: "predator_hunt", bound: probeIntervalTicks,
			scope:  "A predator hunting a colonist mid-window at Ultrafast stops the clock as predator_hunt within the 30-tick probe bound.",
			inject: injectOp("predator")},
	} {
		hc := hc
		cases.Register(cases.Case{
			Name:        hc.name,
			Scope:       hc.scope,
			Start:       cases.FlatDebugStart(),
			QuietWorld:  true,
			RequiredOps: []string{"test/hazard_inject", "test/deliver_letter", "test/letter_pause_mode"},
			Budget:      4 * time.Minute,
			Run:         func(ctx context.Context, s cases.Session) error { return run(ctx, s, hc) },
		})
	}
}

// injectOp calls test/hazard_inject with the op and reports the tick the
// fixture acted on.
func injectOp(op string) func(ctx context.Context, s cases.Session) (int64, map[string]any, error) {
	return func(ctx context.Context, s cases.Session) (int64, map[string]any, error) {
		reply, err := s.Harness().Call(ctx, "inject-"+op, "test/hazard_inject", map[string]any{"op": op})
		if err != nil {
			return 0, nil, err
		}
		if ok, _ := na.AsBool(reply["success"]); !ok {
			return 0, nil, fmt.Errorf("hazard_inject %s refused: %#v", op, reply)
		}
		return int64(na.AsNumber(reply["tick"])), reply, nil
	}
}

// injectLetter delivers a ThreatSmall letter now through LetterStack.
// ReceiveLetter, the hook the notification_batch bound rests on.
func injectLetter(ctx context.Context, s cases.Session) (int64, map[string]any, error) {
	reply, err := s.Harness().Call(ctx, "inject-letter", "test/deliver_letter",
		map[string]any{"definition": "ThreatSmall", "label": "hazard letter", "delayTicks": 0})
	if err != nil {
		return 0, nil, err
	}
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return 0, nil, fmt.Errorf("deliver_letter refused: %#v", reply)
	}
	return int64(na.AsNumber(reply["tick"])), reply, nil
}

func run(ctx context.Context, s cases.Session, hc hazardCase) error {
	report := s.Report()
	h := s.Harness()
	for _, tool := range hc.requiredTools() {
		if !na.Contains(s.Names(), tool) {
			return fmt.Errorf("%s is not installed: build the mod with -Fixture HazardFixture,LetterFixture", tool)
		}
	}
	// No letter pauses the clock on its own: the stop under test is the
	// supervisor's, not LetterPauseHook's. Prefs are process-wide; put the
	// mode back for the next case.
	initial, err := h.Call(ctx, "pause-mode-initial", "test/letter_pause_mode", map[string]any{})
	if err != nil {
		return err
	}
	setPauseMode := func(mode string) error {
		reply, err := h.Call(ctx, "pause-mode-"+mode, "test/letter_pause_mode", map[string]any{"mode": mode})
		if err != nil {
			return err
		}
		if na.AsString(reply["mode"]) != mode {
			return fmt.Errorf("letter_pause_mode did not take: %#v", reply)
		}
		return nil
	}
	if err := setPauseMode("Never"); err != nil {
		return err
	}
	defer func() { _ = setPauseMode(na.AsString(initial["mode"])) }()
	defer func() { _, _ = h.Call(ctx, "cleanup", "test/hazard_inject", map[string]any{"op": "cleanup"}) }()

	clock := &na.ScenarioClock{Wire: h.WireFunc(), Identity: s.Identity(), Owner: na.Controller, Report: report}
	if _, err := clock.Acquire(ctx, "acquire"); err != nil {
		return err
	}
	started, err := clock.Change(ctx, "Ultrafast", windowTicks)
	if err != nil {
		return fmt.Errorf("ultrafast window: %w", err)
	}
	startTick, _ := started["startTick"].(uint64)
	clock.SeekEvents(started)
	// Let the window run before injecting; a stop before then is a
	// hazard the colony raised on its own and fails the case.
	deadline := time.Now().Add(60 * time.Second)
	for {
		status, err := clock.Call(ctx, "status", nil)
		if err != nil {
			return err
		}
		if active, _ := status["active"].(bool); !active {
			return fmt.Errorf("window stopped before the injection: %s", na.AsString(status["stopReason"]))
		}
		if last, _ := status["lastTick"].(uint64); last >= startTick+settleTicks {
			break
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("window did not reach %d ticks in 60 s: %#v", settleTicks, status)
		}
		time.Sleep(200 * time.Millisecond)
	}
	injectTick, injected, err := hc.inject(ctx, s)
	if err != nil {
		return err
	}
	// Poll the events until the stop; the window's tick budget bounds the
	// wait at Ultrafast.
	var stopped map[string]any
	var stopTick int64
	deadline = time.Now().Add(90 * time.Second)
	for stopped == nil {
		events, err := clock.Poll(ctx)
		if err != nil {
			return err
		}
		for _, raw := range events {
			row, _ := na.AsMap(raw)
			native, _ := na.AsMap(row["native"])
			if body, ok := na.AsMap(native["stopped"]); ok {
				stopped = body
				context, _ := na.AsMap(native["context"])
				stopTick = int64(na.AsNumber(context["tick"]))
				stopped["kind"] = row["kind"]
			}
		}
		if stopped == nil {
			if time.Now().After(deadline) {
				return fmt.Errorf("no stop within 90 s of injecting %s at tick %d", hc.class, injectTick)
			}
			time.Sleep(100 * time.Millisecond)
		}
	}
	kind := na.AsString(stopped["kind"])
	detected, hasDetected := int64Of(stopped["detectedTick"])
	occurrence, hasOccurrence := int64Of(stopped["occurrenceTick"])
	summary := map[string]any{"class": hc.class, "bound_ticks": hc.bound, "stop_kind": kind, "inject_tick": injectTick,
		"stop_tick": stopTick, "detected_tick": stopped["detectedTick"], "occurrence_tick": stopped["occurrenceTick"], "injected": injected}
	report["hazard"] = summary
	if kind != hc.class {
		return fmt.Errorf("stop kind %s, expected %s: %#v", kind, hc.class, stopped)
	}
	if !hasDetected {
		return fmt.Errorf("%s stop carries no detectedTick: %#v", kind, stopped)
	}
	injectGap := detected - injectTick
	summary["inject_gap_ticks"] = injectGap
	if injectGap < 0 || injectGap > hc.bound {
		return fmt.Errorf("%s detected %d ticks after the injection, bound %d", kind, injectGap, hc.bound)
	}
	if !hasOccurrence {
		return fmt.Errorf("%s stop carries no occurrenceTick: %#v", kind, stopped)
	}
	detectTicks := detected - occurrence
	summary["detect_ticks"] = detectTicks
	if detectTicks < 0 || detectTicks > hc.bound {
		return fmt.Errorf("%s detect_ticks %d (detected %d, occurred %d), bound %d", kind, detectTicks, detected, occurrence, hc.bound)
	}
	// The native declaration on the clock status agrees with the bound
	// the case asserts, and the class's own recorded gap is within it.
	status, err := clock.Call(ctx, "status", nil)
	if err != nil {
		return err
	}
	native, _ := na.AsMap(status["native"])
	var declared map[string]any
	for _, raw := range na.AsSlice(native["hazardGaps"]) {
		if gap, ok := na.AsMap(raw); ok && na.AsString(gap["hazardClass"]) == hc.class {
			declared = gap
		}
	}
	if declared == nil {
		return fmt.Errorf("clock status reports no hazard gap for %s: %#v", hc.class, native["hazardGaps"])
	}
	summary["declared"] = declared
	if bound := int64(na.AsNumber(declared["boundTicks"])); bound != hc.bound {
		return fmt.Errorf("native declares a %d-tick bound for %s, the case asserts %d", bound, hc.class, hc.bound)
	}
	if maxGap := int64(na.AsNumber(declared["maxTickGap"])); maxGap > hc.bound {
		return fmt.Errorf("native recorded a %d-tick gap for %s, bound %d", maxGap, hc.class, hc.bound)
	}
	// A hooked class (the letter, spawn and downed hooks) lands at the next
	// tick boundary whatever its declared bound: the hook must be installed
	// and the measured gap within the hooked bound.
	if hooked, _ := na.AsBool(declared["hooked"]); hooked {
		if injectGap > hookedBoundTicks {
			return fmt.Errorf("%s is hooked but was detected %d ticks after the injection", kind, injectGap)
		}
	} else if hc.class == "notification_batch" || hc.class == "hostile" || hc.class == "colonist_downed" {
		return fmt.Errorf("%s hook is not installed: %#v", hc.class, declared)
	}
	return nil
}

func (hc hazardCase) requiredTools() []string {
	return []string{"test/hazard_inject", "test/deliver_letter", "test/letter_pause_mode"}
}

// int64Of reads a JSON integer the wire carries as a number or a string.
func int64Of(v any) (int64, bool) {
	switch t := v.(type) {
	case nil:
		return 0, false
	case string:
		var n int64
		if _, err := fmt.Sscan(t, &n); err != nil {
			return 0, false
		}
		return n, true
	default:
		return int64(na.AsNumber(v)), true
	}
}
