package bridge

import (
	"bytes"
	"encoding/json"
	"math"
	"strings"
	"testing"
)

// response builds one native_response row carrying the companion's timing
// block, the shape the service copies out of the reply wrapper.
func response(tool string, timing map[string]any) TimelineRecord {
	return TimelineRecord{Kind: "native_response", WallTime: 1,
		Payload: map[string]any{"tool": "games_call_tool", "native_tool": tool, "timing": timing}}
}

// nativeTimed is the pre-#642 timing block: gate/call/decode phases plus the
// companion's queue and execute split, and nothing else.
func nativeTimed(queueMs, executeMs float64) map[string]any {
	return map[string]any{"gate_wait_ms": 0.0, "call_ms": executeMs + queueMs, "decode_ms": 0.0,
		"total_ms": executeMs + queueMs, "response_bytes": 100.0,
		"native_queue_ms": queueMs, "native_execute_ms": executeMs}
}

// observed adds the #642 observation account to a timing block.
func observed(timing map[string]any, account map[string]any) map[string]any {
	timing["native_observation"] = account
	return timing
}

// TestObservationAccountAggregated checks the per-hop capture account
// reduces to exact quantiles, sums, per-section rows and outcomes over a
// known sample set, and that a failed hop's account is counted too.
func TestObservationAccountAggregated(t *testing.T) {
	// Ten hops, captureMs 1..10 so the nearest-rank quantiles are exact:
	// p50 = index ceil(0.5*10) = 5 -> 5, p95 = 10, p99 = 10, max = 10.
	var rows []TimelineRecord
	for i := 1; i <= 10; i++ {
		account := map[string]any{
			"captureMs": float64(i), "formatMs": 0.5, "formatPasses": 3.0,
			"payloadBytes": 1024.0, "outcome": "ok",
			"sections": map[string]any{
				"emergency":   map[string]any{"ms": float64(i) / 2, "rows": 8.0},
				"colonyFacts": map[string]any{"ms": 0.25, "rows": 100.0, "candidates": 250.0},
			},
		}
		if i == 10 {
			account["outcome"] = "failure"
			account["droppedSections"] = 2.0
		}
		rows = append(rows, response("rimgovernor/observations_read_bundle", observed(nativeTimed(float64(i)/10, float64(i)+1), account)))
	}
	summary := SummarizePhases(rows)
	obs := summary.Observation
	if obs.Hops != 10 || obs.FormatPasses != 30 || obs.PayloadBytes != 10240 || obs.Dropped != 2 {
		t.Fatalf("totals: %+v", obs)
	}
	if obs.CaptureMs != 55 || obs.FormatMs != 5 {
		t.Fatalf("sums: capture %v format %v", obs.CaptureMs, obs.FormatMs)
	}
	if obs.Capture.Samples != 10 || obs.Capture.P50 != 5 || obs.Capture.P95 != 10 || obs.Capture.P99 != 10 || obs.Capture.Max != 10 {
		t.Fatalf("capture quantiles: %+v", obs.Capture)
	}
	if obs.Capture.Mean() != 5.5 {
		t.Fatalf("capture mean %v", obs.Capture.Mean())
	}
	// Queue and execute come from the pre-#642 split, so they cover every
	// timed hop: executeMs 2..11.
	if obs.Execute.Samples != 10 || obs.Execute.P50 != 6 || obs.Execute.Max != 11 {
		t.Fatalf("execute quantiles: %+v", obs.Execute)
	}
	if obs.Queue.Samples != 10 || obs.Queue.Max != 1 {
		t.Fatalf("queue quantiles: %+v", obs.Queue)
	}
	if obs.Outcomes["ok"] != 9 || obs.Outcomes["failure"] != 1 {
		t.Fatalf("outcomes: %+v", obs.Outcomes)
	}
	// Sections sort by cost: emergency sums (1..10)/2 = 27.5, colonyFacts 2.5.
	if len(obs.Sections) != 2 || obs.Sections[0].Section != "emergency" {
		t.Fatalf("sections: %+v", obs.Sections)
	}
	if obs.Sections[0].Ms != 27.5 || obs.Sections[0].MaxMs != 5 || obs.Sections[0].Rows != 80 || obs.Sections[0].Candidates != 0 {
		t.Fatalf("emergency section: %+v", obs.Sections[0])
	}
	if obs.Sections[1].Rows != 1000 || obs.Sections[1].Candidates != 2500 || obs.Sections[1].Hops != 10 {
		t.Fatalf("colony facts section: %+v", obs.Sections[1])
	}
	// The per-tool row carries the same split so a report can name the tool.
	if len(summary.Tools) != 1 {
		t.Fatalf("tools: %+v", summary.Tools)
	}
	if tool := summary.Tools[0]; tool.ObservationHops != 10 || tool.CaptureMs != 55 || tool.FormatMs != 5 || tool.FormatPasses != 30 || tool.PayloadBytes != 10240 {
		t.Fatalf("tool split: %+v", tool)
	}
}

// TestObservationAccountAbsentStaysUnknown checks a recording from a
// companion without the account, and one whose account is malformed, leave
// the capture phases unknown rather than zero, while the queue and execute
// split it does carry is still summarized.
func TestObservationAccountAbsentStaysUnknown(t *testing.T) {
	rows := []TimelineRecord{
		// Pre-#642: queue/execute only.
		response("rimgovernor/observations_read_bundle", nativeTimed(1, 4)),
		// An untimed call (a failure before timing existed).
		{Kind: "native_response", WallTime: 1, Payload: map[string]any{"tool": "games_call_tool", "native_tool": "rimgovernor/clock_read_status"}},
		// A malformed account: a negative duration is not a measurement.
		response("rimgovernor/observations_read_bundle", observed(nativeTimed(1, 4), map[string]any{"captureMs": -1.0, "formatMs": 1.0})),
		// An empty account carries no measurement either.
		response("rimgovernor/observations_read_bundle", observed(nativeTimed(1, 4), map[string]any{})),
		// An error row keeps its call in the tool totals with no account.
		{Kind: "native_error", WallTime: 1, Payload: map[string]any{"tool": "games_call_tool",
			"native_tool": "rimgovernor/observations_read_bundle", "error": "transport", "timing": nativeTimed(0, 0)}},
	}
	summary := SummarizePhases(rows)
	obs := summary.Observation
	if obs.Hops != 0 || obs.Capture.Samples != 0 || obs.Format.Samples != 0 || obs.CaptureMs != 0 {
		t.Fatalf("absent account reported: %+v", obs)
	}
	if obs.Queue.Samples != 4 || obs.Execute.Samples != 4 || obs.Execute.Max != 4 {
		t.Fatalf("queue/execute split: %+v %+v", obs.Queue, obs.Execute)
	}
	if summary.Untimed != 1 {
		t.Fatalf("untimed %d", summary.Untimed)
	}
	if summary.Frames.Samples != 0 || summary.Frames.Hooked {
		t.Fatalf("frames reported without samples: %+v", summary.Frames)
	}
	var text bytes.Buffer
	WritePhaseReport(&text, summary)
	if !strings.Contains(text.String(), "capture unknown") || !strings.Contains(text.String(), "format unknown") {
		t.Fatalf("absent phases not marked unknown:\n%s", text.String())
	}
	if strings.Contains(text.String(), "frames:") {
		t.Fatalf("frame section printed without samples:\n%s", text.String())
	}
}

// frameSampleRow builds a native_response row carrying a frame-recorder
// sample with the given cumulative counters.
func frameSampleRow(updates, observations, cancelled uint64, elapsed, maxUpdate, observationMs, recorder float64, slow map[float64]uint64, worst []any) TimelineRecord {
	buckets := make([]any, 0, len(slow))
	for _, threshold := range SlowFrameThresholdsMs {
		count, ok := slow[threshold]
		if !ok {
			continue
		}
		buckets = append(buckets, map[string]any{"thresholdMs": threshold, "count": float64(count)})
	}
	timing := nativeTimed(0.1, 1)
	timing["native_frames"] = map[string]any{
		"hooked": true, "updates": float64(updates), "elapsedMs": elapsed, "maxUpdateMs": maxUpdate,
		"observationMs": observationMs, "observations": float64(observations), "cancelled": float64(cancelled),
		"recorderMs": recorder, "slow": buckets, "worst": worst,
	}
	return response("rimgovernor/observations_read_bundle", timing)
}

// TestFrameAccountDifferencedOverRecording checks the cumulative frame
// counters are differenced between the recording's first and last sample,
// the widest interval is the widest any sample saw, the slow-frame counts
// come out at the declared thresholds and the worst ring is the last
// sample's.
func TestFrameAccountDifferencedOverRecording(t *testing.T) {
	worst := []any{map[string]any{"update": 900.0, "intervalMs": 120.5, "observationMs": 95.25, "tick": "12000", "trace": "abc/def"}}
	rows := []TimelineRecord{
		frameSampleRow(1000, 10, 0, 16000, 40, 100, 1, map[float64]uint64{16.7: 20, 33.3: 4, 100: 0, 250: 0}, nil),
		frameSampleRow(2000, 30, 1, 48000, 120.5, 700, 3, map[float64]uint64{16.7: 90, 33.3: 14, 100: 1, 250: 0}, worst),
	}
	summary := SummarizePhases(rows)
	frames := summary.Frames
	if !frames.Hooked || frames.Samples != 2 || frames.Updates != 1000 || frames.Observations != 20 || frames.Cancelled != 1 {
		t.Fatalf("frame counters: %+v", frames)
	}
	if frames.ElapsedMs != 32000 || frames.ObservationMs != 600 || frames.RecorderMs != 2 || frames.MaxIntervalMs != 120.5 {
		t.Fatalf("frame spans: %+v", frames)
	}
	if got := frames.UpdatesPerSecond(); math.Abs(got-31.25) > 1e-9 {
		t.Fatalf("updates per second %v", got)
	}
	if got := frames.ObservationShare(); math.Abs(got-0.01875) > 1e-9 {
		t.Fatalf("observation share %v", got)
	}
	if len(frames.Slow) != 4 || frames.Slow[0] != (SlowFrameBucket{ThresholdMs: 16.7, Count: 70}) || frames.Slow[2].Count != 1 {
		t.Fatalf("slow buckets: %+v", frames.Slow)
	}
	if len(frames.Worst) != 1 || frames.Worst[0].Frame != 900 || frames.Worst[0].Tick != 12000 || frames.Worst[0].Trace != "abc/def" {
		t.Fatalf("worst ring: %+v", frames.Worst)
	}
	var text bytes.Buffer
	WritePhaseReport(&text, summary)
	for _, want := range []string{"frames: 1000 updates over 32.0s", "max update 120.5ms", ">16.7ms 70", "worst update 900"} {
		if !strings.Contains(text.String(), want) {
			t.Fatalf("report missing %q:\n%s", want, text.String())
		}
	}
	// The JSON form is the same fields in snake case.
	encoded, err := json.Marshal(summary.Frames)
	if err != nil || !strings.Contains(string(encoded), `"max_interval_ms":120.5`) {
		t.Fatalf("json form %s (%v)", encoded, err)
	}
}

// TestFrameAccountResetStartsNewBaseline checks a reloaded game, whose
// session counters restart, is read as a new baseline instead of a negative
// span.
func TestFrameAccountResetStartsNewBaseline(t *testing.T) {
	rows := []TimelineRecord{
		frameSampleRow(5000, 100, 0, 80000, 50, 900, 5, nil, nil),
		frameSampleRow(40, 2, 0, 700, 30, 20, 1, nil, nil),
		frameSampleRow(240, 7, 0, 4200, 33, 90, 2, nil, nil),
	}
	frames := SummarizePhases(rows).Frames
	if frames.Updates != 200 || frames.Observations != 5 || frames.ElapsedMs != 3500 || frames.ObservationMs != 70 {
		t.Fatalf("reset baseline: %+v", frames)
	}
	if frames.MaxIntervalMs != 50 {
		t.Fatalf("widest interval is session-wide: %+v", frames)
	}
}

// TestQuantilesNearestRank pins the quantile definition the report
// documents, including the single-sample and two-sample cases a sparse
// recording produces.
func TestQuantilesNearestRank(t *testing.T) {
	if got := quantiles(nil); got.Samples != 0 || got.Max != 0 {
		t.Fatalf("empty: %+v", got)
	}
	one := quantiles([]float64{7})
	if one.Samples != 1 || one.P50 != 7 || one.P95 != 7 || one.P99 != 7 || one.Max != 7 {
		t.Fatalf("single sample: %+v", one)
	}
	two := quantiles([]float64{9, 1})
	if two.P50 != 1 || two.P95 != 9 || two.Max != 9 || two.Sum != 10 {
		t.Fatalf("two samples: %+v", two)
	}
	hundred := make([]float64, 100)
	for i := range hundred {
		hundred[i] = float64(100 - i)
	}
	full := quantiles(hundred)
	if full.P50 != 50 || full.P95 != 95 || full.P99 != 99 || full.Max != 100 {
		t.Fatalf("hundred samples: %+v", full)
	}
}

// TestNativeTimingCarriesObservationBlocks checks the reply wrapper's #642
// blocks reach the flight row verbatim, and that a wrapper without them
// reports none.
func TestNativeTimingCarriesObservationBlocks(t *testing.T) {
	raw := `{"payload":"{}","timing":{"queueMs":1,"executeMs":2,` +
		`"observation":{"captureMs":1.5,"sections":{"emergency":{"ms":0.5,"rows":8}}},` +
		`"frames":{"hooked":true,"updates":12}}}`
	report, ok := nativeTiming(json.RawMessage(raw))
	if !ok || report.observation == nil || report.frames == nil {
		t.Fatalf("blocks not carried: %+v %v", report, ok)
	}
	if report.observation["captureMs"] != 1.5 || report.frames["hooked"] != true {
		t.Fatalf("blocks altered: %+v", report)
	}
	plain, ok := nativeTiming(json.RawMessage(`{"payload":"{}","timing":{"queueMs":1,"executeMs":2}}`))
	if !ok || plain.observation != nil || plain.frames != nil {
		t.Fatalf("absent blocks invented: %+v", plain)
	}
}
