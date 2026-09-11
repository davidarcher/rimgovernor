package bridge

import (
	"encoding/json"
	"testing"
)

func TestOwnedDescriptorIsExact(t *testing.T) {
	for _, kind := range []string{"object", "string"} {
		raw := json.RawMessage(`{"inputSchema":{"type":"object","additionalProperties":false,"properties":{"request":{"type":"` + kind + `"}}}}`)
		if err := validateOwnedStringInput(raw, "request"); err != nil {
			t.Fatal(err)
		}
	}
	for _, raw := range []string{`{"inputSchema":{"type":"object","properties":{"request":{"type":"object"}}}}`, `{"inputSchema":{"type":"object","additionalProperties":false,"properties":{"placements":{"type":"object"}}}}`, `{"inputSchema":{"type":"object","additionalProperties":false,"properties":{"request":{"type":"array"}}}}`, `{"inputSchema":{"type":"object","additionalProperties":false,"properties":{"request":{"type":"object"},"apply":{"type":"boolean"}}}}`} {
		if err := validateOwnedStringInput(json.RawMessage(raw), "request"); err == nil {
			t.Fatalf("accepted %s", raw)
		}
	}
}
