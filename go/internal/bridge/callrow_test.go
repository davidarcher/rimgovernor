package bridge

import (
	"encoding/json"
	"strings"
	"testing"
	"time"
)

func TestRecordedArgumentsKeepsOnlyTheWrapperRequest(t *testing.T) {
	wrapped := json.RawMessage(`{"gameId":"g","tool":"rimgovernor/x","arguments":{"request":"{\"a\":1}","trace":"t/s","class":"control","encoding":"proto-shm"}}`)
	got, _ := json.Marshal(recordedArguments("games_call_tool", wrapped))
	if string(got) != `{"request":"{\"a\":1}"}` {
		t.Fatalf("wrapper arguments = %s", got)
	}
	plain := json.RawMessage(`{"cursor":"","query":"q"}`)
	if got, _ := json.Marshal(recordedArguments("games_tool_names", plain)); string(got) != string(plain) {
		t.Fatalf("other tools keep their arguments, got %s", got)
	}
	odd := json.RawMessage(`{"tool":"x"}`)
	if got, _ := json.Marshal(recordedArguments("games_call_tool", odd)); string(got) != string(odd) {
		t.Fatalf("a wrapper without a request is kept as sent, got %s", got)
	}
}

func TestRecordedResultDropsTimingOnly(t *testing.T) {
	got := recordedResult(json.RawMessage(`{"proto":"abc","operation":{"id":"o"},"timing":{"frames":{"histogram":[1,2,3]}}}`))
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(got, &fields); err != nil {
		t.Fatal(err)
	}
	if _, ok := fields["timing"]; ok || len(fields) != 2 || string(fields["proto"]) != `"abc"` {
		t.Fatalf("result = %s", got)
	}
	for _, raw := range []string{`{"proto":"abc"}`, `not json`, ``} {
		if string(recordedResult(json.RawMessage(raw))) != raw {
			t.Fatalf("%q should pass through", raw)
		}
	}
}

func TestSampleFramesOncePerInterval(t *testing.T) {
	c := &Client{}
	if !c.sampleFrames() {
		t.Fatal("first call carries the frame account")
	}
	if c.sampleFrames() {
		t.Fatal("a second call inside the interval must not")
	}
	c.framesSampledAt.Store(time.Now().Add(-2 * framesSampleEvery).UnixNano())
	if !c.sampleFrames() {
		t.Fatal("a call after the interval carries it again")
	}
}

func TestTruncatedPayloadPreviewIsBounded(t *testing.T) {
	r, err := NewFlightRecorder(t.TempDir()+"/flight.jsonl", FlightPayloadBytes(DefaultFlightPayloadBytes))
	if err != nil {
		t.Fatal(err)
	}
	defer r.Close()
	if _, err := r.Event("big", nil, false, map[string]any{"blob": strings.Repeat("x", DefaultFlightPayloadBytes*2)}); err != nil {
		t.Fatal(err)
	}
	if stats := r.FlightRecorderStats(); stats.Truncated != 1 {
		t.Fatalf("truncated = %d", stats.Truncated)
	}
	data, err := readFlightLines(r.path)
	if err != nil {
		t.Fatal(err)
	}
	last := data[len(data)-1]
	if len(last) > 2*flightPreviewBytes {
		t.Fatalf("truncated row is %d bytes, preview should cap near %d", len(last), flightPreviewBytes)
	}
}
