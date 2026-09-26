package bridge

import "testing"

// envelope is a raw native reply envelope with the companion's own timing
// key names, as a harness call returns it (no flight recording involved).
func envelope(queueMs, executeMs float64, observation, frames map[string]any) map[string]any {
	timing := map[string]any{"queueMs": queueMs, "executeMs": executeMs, "trace": "t/1", "class": "observation"}
	if observation != nil {
		timing["observation"] = observation
	}
	if frames != nil {
		timing["frames"] = frames
	}
	return map[string]any{"payload": "{}", "timing": timing}
}

// TestNativeAccountReadsReplyEnvelopes checks the accounts come out of raw
// reply envelopes the same way they come out of a flight recording: the
// per-hop capture split aggregates, the cumulative frame counters are
// differenced first to last, and an envelope from a companion that carries
// neither block leaves the accounts unknown rather than zeroed.
func TestNativeAccountReadsReplyEnvelopes(t *testing.T) {
	frames := func(updates uint64, elapsed float64) map[string]any {
		return map[string]any{"hooked": true, "updates": float64(updates), "elapsedMs": elapsed,
			"maxUpdateMs": 40.0, "observationMs": 12.0, "observations": 3.0, "cancelled": 1.0, "recorderMs": 0.4,
			"slow": []any{map[string]any{"thresholdMs": 16.7, "count": 5.0}}}
	}
	var account NativeAccount
	if account.Reply(envelope(0.5, 4, map[string]any{"captureMs": 3.0, "formatMs": 1.0, "formatPasses": 1.0,
		"payloadBytes": 900.0, "outcome": "ok",
		"sections": map[string]any{"planningWindow": map[string]any{"ms": 2.0, "rows": 120.0, "candidates": 400.0}},
	}, frames(1000, 16000))) != true {
		t.Fatal("the first envelope carried both accounts")
	}
	if !account.Reply(envelope(0.5, 6, map[string]any{"captureMs": 5.0, "formatMs": 2.0, "formatPasses": 2.0,
		"payloadBytes": 1100.0, "outcome": "ok"}, frames(1300, 21000))) {
		t.Fatal("the second envelope carried both accounts")
	}
	obs := account.Observation()
	if obs.Hops != 2 || obs.CaptureMs != 8 || obs.FormatMs != 3 || obs.FormatPasses != 3 || obs.PayloadBytes != 2000 {
		t.Fatalf("observation aggregate: %+v", obs)
	}
	if obs.Capture.Samples != 2 || obs.Capture.Max != 5 || obs.Execute.Samples != 2 || obs.Execute.Max != 6 {
		t.Fatalf("quantiles over the envelopes: capture=%+v execute=%+v", obs.Capture, obs.Execute)
	}
	if len(obs.Sections) != 1 || obs.Sections[0].Section != "planningWindow" || obs.Sections[0].Candidates != 400 {
		t.Fatalf("per-section split: %+v", obs.Sections)
	}
	frame := account.Frames()
	if !frame.Hooked || frame.Samples != 2 || frame.Updates != 300 || frame.ElapsedMs != 5000 {
		t.Fatalf("frame account: %+v", frame)
	}
	if frame.MaxIntervalMs != 40 || frame.UpdatesPerSecond() != 60 {
		t.Fatalf("frame rate: %+v", frame)
	}

	// A companion that predates the account, and a reply with no timing at
	// all, leave both accounts unknown.
	var older NativeAccount
	if older.Reply(map[string]any{"payload": "{}"}) {
		t.Fatal("an envelope without a timing block carries nothing")
	}
	if !older.Reply(envelope(0.5, 4, nil, nil)) {
		t.Fatal("a pre-#642 envelope still carries the queue and execute split")
	}
	if got := older.Observation(); got.Hops != 0 || got.Capture.Samples != 0 || got.Queue.Samples != 1 {
		t.Fatalf("capture stays unknown while the queue split is known: %+v", got)
	}
	if got := older.Frames(); got.Samples != 0 || got.Hooked {
		t.Fatalf("frames stay unknown: %+v", got)
	}
}
