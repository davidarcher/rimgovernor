package bridge

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"sync"
	"time"
)

// Replay is a fake game session that serves a recorded transcript:
// its catalog is the transcript's session row and every call answers with
// the next recorded receipt, provided the call is the one recorded. A call the recording never saw, or one
// past its end, is a ReplayMismatch: the reply is an error, the mismatch
// is kept for Err, and every later call fails the same way. Harness code
// under go test runs against it in milliseconds; the game stays the
// oracle for acceptance.
type Replay struct {
	gameID string
	tools  []string
	calls  []TranscriptRow

	mu       sync.Mutex
	next     int
	mismatch *ReplayMismatch
	sessions []*handlerBackend
}

// ReplayMismatch is a call the transcript did not record at that point.
type ReplayMismatch struct {
	// Sequence and Phase are the recorded row's when one was expected;
	// Sequence is 0 once the transcript is exhausted.
	Sequence int
	Phase    string
	Expected TranscriptRow
	Actual   TranscriptRow
}

func (m *ReplayMismatch) Error() string {
	var b strings.Builder
	if m.Sequence == 0 {
		fmt.Fprintf(&b, "replay: transcript exhausted; unrecorded call %s", describeCall(m.Actual))
		return b.String()
	}
	fmt.Fprintf(&b, "replay: call differs from transcript row %d", m.Sequence)
	if m.Phase != "" {
		fmt.Fprintf(&b, " (phase %s)", m.Phase)
	}
	fmt.Fprintf(&b, ": recorded %s, got %s", describeCall(m.Expected), describeCall(m.Actual))
	if diff := lineDiff(prettyCall(m.Expected), prettyCall(m.Actual)); diff != "" {
		b.WriteString("\n")
		b.WriteString(diff)
	}
	return b.String()
}

func describeCall(row TranscriptRow) string {
	if row.NativeTool != "" && row.NativeTool != row.Tool {
		return row.Tool + " " + row.NativeTool
	}
	return row.Tool
}

func prettyCall(row TranscriptRow) string {
	var out bytes.Buffer
	fmt.Fprintf(&out, "tool: %s\n", row.Tool)
	var value any
	if json.Unmarshal(row.Arguments, &value) == nil {
		expanded, _ := json.MarshalIndent(value, "", "  ")
		out.Write(expanded)
	} else {
		out.Write(row.Arguments)
	}
	return out.String()
}

// NewReplay builds a Replay over rows (ReadTranscript's). The first
// session row names the game and the call catalog; a transcript without
// one is refused.
func NewReplay(rows []TranscriptRow) (*Replay, error) {
	r := &Replay{}
	for _, row := range rows {
		switch row.Kind {
		case "session":
			if r.gameID == "" {
				r.gameID, r.tools = row.GameID, row.Tools
			}
		case "call":
			r.calls = append(r.calls, row)
		}
	}
	if r.gameID == "" {
		return nil, errors.New("replay: transcript has no session row")
	}
	return r, nil
}

// Open connects a Client to the replay; timeout is the Client's per-call
// bound, as ProcessConfig.Timeout. Reconnect and Reattach on the Client
// connect to the same replay and continue the transcript.
// Open connects a Client to the replay; timeout is the Client's per-call
// bound, as ProcessConfig.Timeout. Reconnect and Reattach on the Client
// connect to the same replay and continue the transcript.
func (r *Replay) Open(ctx context.Context, timeout time.Duration) (*Client, error) {
	catalog := Discovery{ProtocolVersion: "transcript", ServerName: "replay", ServerVersion: "transcript"}
	for _, name := range r.tools {
		catalog.Tools = append(catalog.Tools, Tool{Name: name, InputSchema: json.RawMessage(`{"type":"object"}`)})
	}
	return open(ctx, r.gameID, timeout, nil, nil, func(context.Context) (backend, error) {
		session := newHandlerBackend(catalog, r.handle)
		r.mu.Lock()
		r.sessions = append(r.sessions, session)
		r.mu.Unlock()
		return session, nil
	})
}

// Close ends every session the replay served.
func (r *Replay) Close() {
	r.mu.Lock()
	sessions := r.sessions
	r.sessions = nil
	r.mu.Unlock()
	for _, session := range sessions {
		_ = session.close()
	}
}

// Err is the first mismatch, or nil while every call matched.
func (r *Replay) Err() error {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.mismatch == nil {
		return nil
	}
	return r.mismatch
}

// Consumed is how many recorded calls have been served; Remaining how
// many the transcript still holds.
func (r *Replay) Consumed() int { r.mu.Lock(); defer r.mu.Unlock(); return r.next }
func (r *Replay) Remaining() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.calls) - r.next
}

// handle answers one call with the next recorded receipt, byte for byte, so
// int64 observations survive the round trip the way they do on the wire.
func (r *Replay) handle(_ context.Context, name string, arguments json.RawMessage) (json.RawMessage, error) {
	actual := TranscriptRow{Kind: "call", Tool: name, Arguments: append(json.RawMessage(nil), arguments...)}
	actual.NativeTool = nativeToolOf(actual.Tool, actual.Arguments)
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.mismatch != nil {
		return nil, r.mismatch
	}
	if r.next >= len(r.calls) {
		r.mismatch = &ReplayMismatch{Actual: actual}
		return nil, r.mismatch
	}
	expected := r.calls[r.next]
	if expected.Tool != actual.Tool || canonicalJSON(expected.Arguments) != canonicalJSON(actual.Arguments) {
		r.mismatch = &ReplayMismatch{Sequence: expected.Sequence, Phase: expected.Phase, Expected: expected, Actual: actual}
		return nil, r.mismatch
	}
	r.next++
	if len(expected.Result) == 0 {
		if expected.Error != "" {
			return nil, errors.New(expected.Error)
		}
		return nil, fmt.Errorf("replay: transcript row %d has neither a receipt nor an error", expected.Sequence)
	}
	return append(json.RawMessage(nil), expected.Result...), nil
}

// canonicalJSON renders raw with sorted keys and no whitespace so two
// encodings of one argument object compare equal; invalid JSON compares
// as its own text.
func canonicalJSON(raw json.RawMessage) string {
	var value any
	if len(raw) == 0 {
		return "{}"
	}
	if err := json.Unmarshal(raw, &value); err != nil {
		return string(raw)
	}
	out, err := json.Marshal(value)
	if err != nil {
		return string(raw)
	}
	return string(out)
}

// lineDiff is a unified-style line diff of a and b ("-" recorded, "+"
// actual), empty when they are equal.
func lineDiff(a, b string) string {
	if a == b {
		return ""
	}
	as, bs := strings.Split(a, "\n"), strings.Split(b, "\n")
	// Longest common subsequence over lines.
	lcs := make([][]int, len(as)+1)
	for i := range lcs {
		lcs[i] = make([]int, len(bs)+1)
	}
	for i := len(as) - 1; i >= 0; i-- {
		for j := len(bs) - 1; j >= 0; j-- {
			if as[i] == bs[j] {
				lcs[i][j] = lcs[i+1][j+1] + 1
			} else if lcs[i+1][j] >= lcs[i][j+1] {
				lcs[i][j] = lcs[i+1][j]
			} else {
				lcs[i][j] = lcs[i][j+1]
			}
		}
	}
	var out strings.Builder
	i, j := 0, 0
	for i < len(as) || j < len(bs) {
		switch {
		case i < len(as) && j < len(bs) && as[i] == bs[j]:
			out.WriteString("  " + as[i] + "\n")
			i++
			j++
		case i < len(as) && (j >= len(bs) || lcs[i+1][j] >= lcs[i][j+1]):
			out.WriteString("- " + as[i] + "\n")
			i++
		default:
			out.WriteString("+ " + bs[j] + "\n")
			j++
		}
	}
	return strings.TrimRight(out.String(), "\n")
}
