package testkit

import (
	"bytes"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"os"
	"path/filepath"
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

func fixtureBytes(t *testing.T, name string) []byte {
	t.Helper()
	data, err := os.ReadFile(filepath.Join("..", "..", "..", "contracts", "fixtures", name))
	if err != nil {
		t.Fatal(err)
	}
	return data
}

func TestReplayStateFixtures(t *testing.T) {
	var fixture struct {
		Items []struct {
			ID    string          `json:"id"`
			Value json.RawMessage `json:"value"`
		} `json:"items"`
	}
	if err := json.Unmarshal(fixtureBytes(t, "state-baseline.json"), &fixture); err != nil {
		t.Fatal(err)
	}
	if len(fixture.Items) == 0 {
		t.Fatal("missing state evidence")
	}
	for _, item := range fixture.Items {
		t.Run(item.ID, func(t *testing.T) {
			var pretty bytes.Buffer
			if err := json.Indent(&pretty, item.Value, "", "  "); err != nil {
				t.Fatal(err)
			}
			result, err := CompareJSON(bytes.NewReader(item.Value), &pretty)
			if err != nil || !result.Equal {
				t.Fatalf("fixture whitespace changed replay: %+v, %v", result, err)
			}
		})
	}
}

func TestReplaySignedSerializationFixtures(t *testing.T) {
	var fixture struct {
		Items []struct {
			ID            string `json:"id"`
			Category      string `json:"category"`
			Bytes         string `json:"action_utf8_base64"`
			ProgressBytes string `json:"progress_utf8_base64"`
			Signature     string `json:"signature"`
		} `json:"items"`
		Relations []struct {
			Left  string `json:"left"`
			Right string `json:"right"`
			Equal bool   `json:"equal"`
		} `json:"signature_relations"`
	}
	if err := json.Unmarshal(fixtureBytes(t, "serialization-baseline.json"), &fixture); err != nil {
		t.Fatal(err)
	}
	if len(fixture.Items) == 0 || len(fixture.Relations) == 0 {
		t.Fatal("missing signature evidence")
	}
	values := make(map[string][]byte)
	for _, item := range fixture.Items {
		encoded := item.Bytes
		if item.Category == "uncertain_progress" {
			encoded = item.ProgressBytes
		} else if item.Category != "action_serialization" {
			t.Fatalf("unhandled fixture category %q", item.Category)
		}
		value, err := base64.StdEncoding.DecodeString(encoded)
		if err != nil {
			t.Fatal(err)
		}
		digest := sha256.Sum256(value)
		if item.Category == "action_serialization" && hex.EncodeToString(digest[:]) != item.Signature {
			t.Fatalf("%s exact signed bytes changed", item.ID)
		}
		if item.Category == "uncertain_progress" {
			for _, change := range []struct{ before, after string }{
				{`"confirmed":false`, `"confirmed":true`},
				{`"order_generation":0`, `"order_generation":1`},
				{`"load_token":"load"`, `"load_token":"other"`},
			} {
				altered := bytes.Replace(value, []byte(change.before), []byte(change.after), 1)
				if bytes.Equal(value, altered) {
					t.Fatalf("fixture no longer contains %s", change.before)
				}
				result, err := CompareJSON(bytes.NewReader(value), bytes.NewReader(altered))
				if err != nil || result.Equal {
					t.Fatalf("uncertain evidence change hidden: %+v, %v", result, err)
				}
			}
		}
		values[item.ID] = value
		var pretty bytes.Buffer
		if err := json.Indent(&pretty, value, "", " "); err != nil {
			t.Fatal(err)
		}
		result, err := CompareJSON(bytes.NewReader(value), &pretty)
		if err != nil || !result.Equal {
			t.Fatalf("%s: %+v, %v", item.ID, result, err)
		}
	}
	for _, relation := range fixture.Relations {
		result, err := CompareJSON(bytes.NewReader(values[relation.Left]), bytes.NewReader(values[relation.Right]))
		if err != nil || result.Equal != relation.Equal {
			t.Errorf("signature relation %s / %s: %+v, %v", relation.Left, relation.Right, result, err)
		}
	}
}

var _ io.Reader = failingReplayReader{}
