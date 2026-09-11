package contractgen

import (
	"strings"
	"testing"
)

func TestRawJSONContainerDepthMatchesLanguageBoundaries(t *testing.T) {
	for _, depth := range []int{63, 64, 65} {
		raw := strings.Repeat("[", depth) + strings.Repeat("]", depth)
		err := validJSONText([]byte(raw))
		if (err == nil) != (depth <= 64) {
			t.Fatalf("depth %d: %v", depth, err)
		}
	}
	if err := validJSONText([]byte(`"` + strings.Repeat("[", 65) + `"`)); err != nil {
		t.Fatalf("quoted brackets count as containers: %v", err)
	}
}
