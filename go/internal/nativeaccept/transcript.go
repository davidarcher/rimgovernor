package nativeaccept

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// RecordEnv names a directory; when set, every bridge call of the run is
// recorded to <dir>/transcript.jsonl (bridge.Transcript) so harness code
// can be exercised against the recording under go test (#282):
// ReplayHarness serves it back. The evidence rows (evidence.go) stay the
// human-readable record; the transcript is the machine-replayable one.
const RecordEnv = "RIMGOVERNOR_ACCEPT_RECORD"

// TranscriptName is the transcript's file name under RecordEnv's directory.
const TranscriptName = "transcript.jsonl"

// recording is the process's one transcript: the sessions a run opens (a
// reattach after a service, a reuse) all append to it, so the rows are one
// sequence.
var recording struct {
	once sync.Once
	t    *bridge.Transcript
	err  error
}

// RecordingTranscript is the run's transcript when RecordEnv is set, nil
// otherwise; the file is opened once per process.
func RecordingTranscript() (*bridge.Transcript, error) {
	dir := os.Getenv(RecordEnv)
	if dir == "" {
		return nil, nil
	}
	recording.once.Do(func() {
		recording.t, recording.err = bridge.OpenTranscript(filepath.Join(dir, TranscriptName))
		if recording.err != nil {
			recording.err = fmt.Errorf("%s: %w", RecordEnv, recording.err)
		}
	})
	return recording.t, recording.err
}

// WithRecording attaches the run's transcript (RecordingTranscript) to a
// bridge.ProcessConfig; every bridge.Open in this package goes through it.
func WithRecording(config bridge.ProcessConfig) (bridge.ProcessConfig, error) {
	if config.Transcript != nil {
		return config, nil
	}
	transcript, err := RecordingTranscript()
	if err != nil {
		return config, err
	}
	config.Transcript = transcript
	return config, nil
}

// ReplayHarness opens a Harness over the transcript at path (a
// bridge.Replay): its calls answer with the recorded receipts in
// milliseconds, and a call the recording never saw fails, with
// bridge.Replay.Err naming the row and the diff. Evidence rows go under
// output as they do live. The caller closes both.
func ReplayHarness(ctx context.Context, path, output string) (*Harness, *bridge.Replay, error) {
	rows, err := bridge.ReadTranscript(path)
	if err != nil {
		return nil, nil, err
	}
	replay, err := bridge.NewReplay(rows)
	if err != nil {
		return nil, nil, err
	}
	client, err := replay.Open(ctx, 10*time.Second)
	if err != nil {
		replay.Close()
		return nil, nil, err
	}
	return NewHarness(client, output), replay, nil
}
