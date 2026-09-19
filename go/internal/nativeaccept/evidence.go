package nativeaccept

import (
	"encoding/json"
	"fmt"
	"path/filepath"
	"sync"
	"time"
)

// Every native call a Harness makes lands as one evidence row,
// <output>/NNNN-<label>.json, holding the request and the reply plus when
// it was observed: sequence (NNNN, allocated per output directory for the
// whole process so the harnesses a case opens over one game -- a
// reattach after a service, a service's own preamble -- number one
// stream), observed_at (the RFC3339 millisecond wall time the call was
// sent), elapsed_ms (until the reply) and tick (the game tick the reply
// carries, when it does; replyTick).

// evidenceSequences allocates the next NNNN per output directory.
var evidenceSequences struct {
	mu   sync.Mutex
	next map[string]int
}

func nextEvidenceSequence(output string) int {
	key := filepath.Clean(output)
	evidenceSequences.mu.Lock()
	defer evidenceSequences.mu.Unlock()
	if evidenceSequences.next == nil {
		evidenceSequences.next = map[string]int{}
	}
	evidenceSequences.next[key]++
	return evidenceSequences.next[key]
}

// evidencePath is the row's file for a sequence and label under output.
func evidencePath(output string, sequence int, label string) string {
	return filepath.Join(output, fmt.Sprintf("%04d-%s.json", sequence, label))
}

// evidenceRow builds the row for one call: the request, the reply envelope
// (or the error and whatever envelope came with it) and the observation
// stamps.
func evidenceRow(sequence int, tool string, args json.RawMessage, sent time.Time, elapsed time.Duration, envelope json.RawMessage, callErr error, tick *uint64) map[string]any {
	row := map[string]any{
		"sequence":    sequence,
		"observed_at": sent.UTC().Format("2006-01-02T15:04:05.000Z07:00"),
		"elapsed_ms":  elapsed.Milliseconds(),
		"request":     map[string]any{"tool": tool, "arguments": args},
	}
	if callErr != nil {
		row["error"] = callErr.Error()
	}
	if len(envelope) > 0 {
		row["result"] = envelope
	}
	if tick != nil {
		row["tick"] = *tick
	}
	return row
}
