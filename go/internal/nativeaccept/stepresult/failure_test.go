package stepresult

import (
	"encoding/json"
	"strings"
	"testing"
)

func TestParseFailureFallbacks(t *testing.T) {
	for _, raw := range []string{`{`, `null`, `{"isError":false,"content":[{"text":"blocking attention"}]}`, `{"structuredContent":{"exception":"not a failed result"}}`} {
		if failure, ok := Parse(json.RawMessage(raw)); ok {
			t.Fatalf("classified %s as %+v", raw, failure)
		}
	}
	for _, tc := range []struct{ raw, kind, summary, detail string }{
		{`{"isError":true}`, "refusal", "tool returned isError without a message", ""},
		{`{"isError":true,"content":[{"type":"image"},{"text":"Missing prerequisite.\r\nMore detail."}]}`, "refusal", "Missing prerequisite.", "More detail."},
		{`{"isError":true,"content":[{"text":"Call blocked.\nAttention ID: att-1\nSummary: Game error\nSamples: mote exception"}]}`, "blocking attention", "Game error", "Samples: mote exception"},
		{`{"isError":true,"structuredContent":{"attentionId":"att-2","summary":"Game error","sampleMessages":["mote exception"]}}`, "blocking attention", "Game error", `"sampleMessages":["mote exception"]`},
	} {
		f, ok := Parse(json.RawMessage(tc.raw))
		if !ok || f.Kind != tc.kind || f.Summary != tc.summary || !strings.Contains(f.Detail, tc.detail) {
			t.Fatalf("Parse(%s) = %+v, %v", tc.raw, f, ok)
		}
	}
}

// A fixture op that declines, and one whose game threw, both reply with a
// successful MCP receipt carrying "success": false. Requiring isError left
// nine nightly cases reporting a bare "bridge read refused: games_call_tool"
// (#663).
func TestParseClassifiesARefusalWithoutIsError(t *testing.T) {
	for _, tc := range []struct{ raw, kind, summary string }{
		{`{"content":[{"text":"{\"reason\":\"No open reachable area for the fixture hut.\",\"success\":false}"}],"structuredContent":{"reason":"No open reachable area for the fixture hut.","success":false}}`,
			"refusal", "No open reachable area for the fixture hut."},
		{`{"structuredContent":{"exception":"System.InvalidOperationException: Pause before drain\r\n[Ref 386DAC38]\n  at HomeBridge.BridgeTools.FoodChannelFixture","success":false}}`,
			"native exception", "System.InvalidOperationException: Pause before drain"},
		{`{"structuredContent":{"exception":"System.InvalidOperationException: Pause before drain/r/n[Ref 145AC928]/n  at HomeBridge","success":false}}`,
			"native exception", "System.InvalidOperationException: Pause before drain"},
		{`{"structuredContent":{"refused":true,"reason":"stale token"}}`, "refusal", "stale token"},
	} {
		f, ok := Parse(json.RawMessage(tc.raw))
		if !ok || f.Kind != tc.kind || f.Summary != tc.summary {
			t.Fatalf("Parse(%s) = %+v, %v", tc.raw, f, ok)
		}
	}
	// A successful reply that reports a handled exception is still not a
	// failure, and neither is one that says success.
	for _, raw := range []string{`{"structuredContent":{"exception":"handled"}}`, `{"structuredContent":{"success":true,"reason":"fine"}}`} {
		if failure, ok := Parse(json.RawMessage(raw)); ok {
			t.Fatalf("classified %s as %+v", raw, failure)
		}
	}
}
