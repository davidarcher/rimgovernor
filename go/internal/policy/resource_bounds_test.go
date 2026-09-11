package policy

import (
	"strings"
	"testing"
)

func TestNativeResourceByteBounds(t *testing.T) {
	for _, tc := range []struct {
		name  string
		valid bool
	}{
		{strings.Repeat("x", 256), true}, {strings.Repeat("x", 257), false},
		{strings.Repeat("🧱", 64), true}, {strings.Repeat("🧱", 65), false},
		{"wood\x00", false}, {string([]byte{0xff}), false},
	} {
		r := request(candidate(t, "a", 1, 1))
		r.Stock.Values[0].Resource = Resource(tc.name)
		_, err := NewInput(r)
		if (err == nil) != tc.valid {
			t.Fatalf("resource bytes=%d error=%v", len(tc.name), err)
		}
	}
}
