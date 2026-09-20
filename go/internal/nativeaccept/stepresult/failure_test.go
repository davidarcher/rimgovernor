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
