package bridge

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"time"
)

// A transcript is the call sequence of one bridge session, recorded so a
// Replay can serve it back without a game (#282): one JSON line per GABS
// tools/call with the raw MCP receipt the client decoded, plus a session
// row per connect carrying the GABS catalog the client discovered. It is
// the recording the acceptance harnesses make under
// RIMGOVERNOR_ACCEPT_RECORD; the flight recorder (flightrecorder.go) is
// the production timeline and keeps only the decoded structured content.

// TranscriptRow is one line of a transcript.
type TranscriptRow struct {
	Sequence int `json:"sequence"`
	// Kind is "session" (a connect: GameID and Tools) or "call".
	Kind string `json:"kind"`
	// Phase is the case phase the caller attached with WithTranscriptPhase
	// (an acceptance harness's evidence label); empty for calls made
	// outside one.
	Phase  string   `json:"phase,omitempty"`
	GameID string   `json:"game_id,omitempty"`
	Tools  []string `json:"tools,omitempty"`
	// Tool is the GABS tool (games_call_tool, games_tool_names, ...) and
	// Arguments its wire arguments; NativeTool is the native tool a
	// games_call_tool named, for reading the transcript.
	Tool       string          `json:"tool,omitempty"`
	NativeTool string          `json:"native_tool,omitempty"`
	Arguments  json.RawMessage `json:"arguments,omitempty"`
	// Result is the raw tools/call receipt (content, structuredContent,
	// isError) when one arrived; a refusal is a receipt like any other.
	// Error is the transport failure when none did.
	Result    json.RawMessage `json:"result,omitempty"`
	Error     string          `json:"error,omitempty"`
	ElapsedMS int64           `json:"elapsed_ms"`
}

// Transcript appends rows to a JSONL file; it is safe for concurrent use
// and one may serve several Clients (a run that reattaches), whose rows
// then share one sequence.
type Transcript struct {
	mu       sync.Mutex
	file     *os.File
	writer   *bufio.Writer
	sequence int
}

// OpenTranscript opens (creating or appending to) the transcript at path.
func OpenTranscript(path string) (*Transcript, error) {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return nil, fmt.Errorf("transcript: %w", err)
	}
	file, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
	if err != nil {
		return nil, fmt.Errorf("transcript: %w", err)
	}
	return &Transcript{file: file, writer: bufio.NewWriter(file)}, nil
}

// Close flushes and closes the file.
func (t *Transcript) Close() error {
	if t == nil {
		return nil
	}
	t.mu.Lock()
	defer t.mu.Unlock()
	if t.file == nil {
		return nil
	}
	flushErr := t.writer.Flush()
	closeErr := t.file.Close()
	t.file = nil
	if flushErr != nil {
		return flushErr
	}
	return closeErr
}

// session records a connect and the catalog it discovered.
func (t *Transcript) session(gameID string, discovery Discovery) {
	if t == nil {
		return
	}
	names := make([]string, len(discovery.Tools))
	for i, tool := range discovery.Tools {
		names[i] = tool.Name
	}
	t.write(TranscriptRow{Kind: "session", GameID: gameID, Tools: names})
}

// call records one tools/call and its outcome.
func (t *Transcript) call(ctx context.Context, tool string, arguments, receipt json.RawMessage, err error, elapsed time.Duration) {
	if t == nil {
		return
	}
	row := TranscriptRow{Kind: "call", Phase: transcriptPhaseFrom(ctx), Tool: tool, NativeTool: nativeToolOf(tool, arguments), Arguments: append(json.RawMessage(nil), arguments...), ElapsedMS: elapsed.Milliseconds()}
	if len(receipt) > 0 {
		row.Result = append(json.RawMessage(nil), receipt...)
	} else if err != nil {
		row.Error = err.Error()
	}
	t.write(row)
}

// write appends one row; a transcript that cannot be written is a broken
// recording, so the failure is loud rather than silent.
func (t *Transcript) write(row TranscriptRow) {
	t.mu.Lock()
	defer t.mu.Unlock()
	if t.file == nil {
		return
	}
	t.sequence++
	row.Sequence = t.sequence
	line, err := json.Marshal(row)
	if err != nil {
		panic(fmt.Sprintf("transcript: encode row %d: %v", row.Sequence, err))
	}
	if _, err := t.writer.Write(append(line, '\n')); err != nil {
		panic(fmt.Sprintf("transcript: write row %d: %v", row.Sequence, err))
	}
	if err := t.writer.Flush(); err != nil {
		panic(fmt.Sprintf("transcript: flush row %d: %v", row.Sequence, err))
	}
}

type transcriptPhaseKey struct{}

// WithTranscriptPhase names the case phase of every call made with ctx in
// the transcript (an acceptance harness attaches its evidence label).
func WithTranscriptPhase(ctx context.Context, phase string) context.Context {
	if phase == "" {
		return ctx
	}
	return context.WithValue(ctx, transcriptPhaseKey{}, phase)
}

func transcriptPhaseFrom(ctx context.Context) string {
	phase, _ := ctx.Value(transcriptPhaseKey{}).(string)
	return phase
}

// ReadTranscript reads every row of the transcript at path.
func ReadTranscript(path string) ([]TranscriptRow, error) {
	file, err := os.Open(path)
	if err != nil {
		return nil, fmt.Errorf("transcript: %w", err)
	}
	defer file.Close()
	scanner := bufio.NewScanner(file)
	scanner.Buffer(make([]byte, 0, 64*1024), maxResponseBytes+64*1024)
	var rows []TranscriptRow
	line := 0
	for scanner.Scan() {
		line++
		text := scanner.Bytes()
		if len(text) == 0 {
			continue
		}
		var row TranscriptRow
		if err := json.Unmarshal(text, &row); err != nil {
			return nil, fmt.Errorf("transcript %s line %d: %w", path, line, err)
		}
		switch row.Kind {
		case "session", "call":
		default:
			return nil, fmt.Errorf("transcript %s line %d: unknown row kind %q", path, line, row.Kind)
		}
		rows = append(rows, row)
	}
	if err := scanner.Err(); err != nil {
		return nil, fmt.Errorf("transcript %s: %w", path, err)
	}
	return rows, nil
}
