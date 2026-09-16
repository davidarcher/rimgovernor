package testkit

import (
	"errors"
	"strings"
	"testing"
)

func TestReplayPreservesEvidence(t *testing.T) {
	baseline := `{"actions":[{"id":"a","generation":7},{"id":"b","generation":8}],"receipt":{"outcome":"uncertain"}}`
	cases := []struct{ name, candidate string }{
		{"action order", `{"actions":[{"id":"b","generation":8},{"id":"a","generation":7}],"receipt":{"outcome":"uncertain"}}`},
		{"ID", strings.Replace(baseline, `"id":"a"`, `"id":"different"`, 1)},
		{"generation", strings.Replace(baseline, `"generation":7`, `"generation":9`, 1)},
		{"receipt", strings.Replace(baseline, `"uncertain"`, `"completed"`, 1)},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			result, err := CompareJSON(strings.NewReader(baseline), strings.NewReader(tc.candidate))
			if err != nil || result.Equal || result.FirstDifference < 0 {
				t.Fatalf("changed evidence was not detected: %+v, %v", result, err)
			}
		})
	}
}

func TestReplayWhitespaceOnly(t *testing.T) {
	cases := []struct {
		left, right string
		equal       bool
	}{
		{` { "a" : [ 1, null, " a b " ] } `, `{"a":[1,null," a b "]}`, true},
		{`{"a":1,"b":2}`, `{"b":2,"a":1}`, false},
		{`1`, `1.0`, false},
		{`1e0`, `1`, false},
		{`0`, `-0`, false},
		{`9007199254740992`, `9007199254740993`, false},
		{`{}`, `{"unknown":null}`, false},
		{`null`, `false`, false},
		{`"α"`, `"\u03b1"`, false},
		{`"a b"`, `"ab"`, false},
		{`123`, `1234`, false},
	}
	for _, tc := range cases {
		result, err := CompareJSON(strings.NewReader(tc.left), strings.NewReader(tc.right))
		if err != nil || result.Equal != tc.equal || (result.FirstDifference == -1) != tc.equal {
			t.Errorf("%s versus %s: %+v, %v", tc.left, tc.right, result, err)
		}
	}
	result, err := CompareJSON(strings.NewReader(" [1,2] "), strings.NewReader("[1,3]"))
	if err != nil || result.FirstDifference != 3 {
		t.Fatalf("compacted offset: %+v, %v", result, err)
	}
}

func TestReplayRejectsAmbiguousOrMalformedEvidence(t *testing.T) {
	cases := []struct{ name, value, reason string }{
		{"duplicate", `{"a":1,"a":2}`, "duplicate"},
		{"escaped duplicate", `{"a":1,"\u0061":2}`, "duplicate"},
		{"nested duplicate", `[{"a":{"x":1,"x":2}}]`, "duplicate"},
		{"extra value", `{} []`, "trailing"},
		{"extra junk", `{} x`, "trailing"},
		{"empty", ``, "invalid JSON"},
		{"truncated", `{"x":`, "invalid JSON"},
		{"bad number", `01`, "trailing JSON"},
		{"mismatched closer", `{"x":1]`, "invalid JSON"},
		{"nonstring key", `{1:2}`, "invalid JSON"},
		{"trailing comma", `[1,]`, "invalid JSON"},
		{"invalid UTF8", string([]byte{'"', 0xff, '"'}), "invalid UTF-8"},
		{"too deep", strings.Repeat("[", MaxReplayDepth+1) + "0" + strings.Repeat("]", MaxReplayDepth+1), "nesting"},
		{"too large", strings.Repeat(" ", MaxReplayBytes) + "0", "exceeds"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			for _, side := range []string{"expected", "candidate"} {
				left, right := "null", tc.value
				if side == "expected" {
					left, right = right, left
				}
				_, err := CompareJSON(strings.NewReader(left), strings.NewReader(right))
				if err == nil || !strings.Contains(err.Error(), side+" evidence") || !strings.Contains(err.Error(), tc.reason) {
					t.Fatalf("%s: expected %q error, got %v", side, tc.reason, err)
				}
			}
		})
	}
	for _, valid := range []string{
		strings.Repeat("[", MaxReplayDepth) + "0" + strings.Repeat("]", MaxReplayDepth),
		strings.Repeat(" ", MaxReplayBytes-1) + "0",
		`[{"a":1},{"a":2}]`,
	} {
		result, err := CompareJSON(strings.NewReader(valid), strings.NewReader(valid))
		if err != nil || !result.Equal {
			t.Fatalf("valid boundary rejected: %+v, %v", result, err)
		}
	}
}

type failingReplayReader struct{ err error }

func (reader failingReplayReader) Read([]byte) (int, error) { return 0, reader.err }

func TestReplayReadFailures(t *testing.T) {
	want := errors.New("read interrupted")
	_, err := CompareJSON(strings.NewReader("{}"), failingReplayReader{want})
	if !errors.Is(err, want) || !strings.Contains(err.Error(), "candidate evidence") {
		t.Fatalf("reader error lost: %v", err)
	}
	if _, err := CompareJSON(nil, strings.NewReader("{}")); err == nil {
		t.Fatal("nil reader accepted")
	}
	// A bounded reader must not drain oversized streams.
	stream := strings.NewReader(strings.Repeat(" ", MaxReplayBytes+100))
	_, err = CompareJSON(stream, strings.NewReader("{}"))
	if err == nil || stream.Len() != 99 {
		t.Fatalf("size limit did not bound reads: remaining=%d error=%v", stream.Len(), err)
	}
}
