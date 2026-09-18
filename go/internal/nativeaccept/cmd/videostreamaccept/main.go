// Command videostreamaccept is the native-acceptance run for G01.09's video
// streaming slice: PresentationMedia.LeaseVideo, ReadFrame and
// AcknowledgeFrame. It owns the full disposable-worker lifecycle (prepare a
// windowed/rendered profile, launch GABS, start a fresh debug game, exercise
// the three new proto tools against the real native mod, stop) and requires
// an actual graphical (non-headless) run: video capture needs Find.Camera !=
// null, which batch mode never has.
//
// CaptureScreenshot and the whole PlayerPresentation service (camera/input
// ownership) remain out of scope and are not exercised here. The WebSocket
// relay itself (go/internal/httpapi/video_stream.go) is verified separately
// with a fake bridge client; this run only proves the underlying RPCs work
// against the real game.
package main

import (
	"bytes"
	"context"
	"encoding/base64"
	"flag"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/videoshm"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-video-stream-acceptance)")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 600*time.Second, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-video-stream-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	report := na.NewReport("LeaseVideo start against a real rendered game, multiple ReadFrame polls proving strictly increasing sequence and updated frame bytes, the same source read out of shared memory at a materially higher rate with matching geometry, AcknowledgeFrame success, and LeaseVideo stop reflected by a subsequent ReadFrame refusal and a quiet shared buffer. CaptureScreenshot and the whole PlayerPresentation service are out of scope. The WebSocket relay is verified separately with a fake bridge client.", false)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID string, report na.Report) error {
	// Video capture requires actual rendering (Find.Camera != null); a
	// headless batch-mode run can never exercise LeaseVideo/ReadFrame's
	// success paths, only their batch-mode-refusal paths.
	cfg := &na.Config{Root: root, Output: output, Headless: false, GameID: gameID}
	s, err := na.OpenSession(ctx, cfg, report, na.DebugStart{}, na.QuietIfAvailable)
	if err != nil {
		return err
	}
	defer s.Close()
	h, identity := s.Harness, s.Identity
	for _, tool := range []string{"rimgovernor/presentation_lease_video", "rimgovernor/presentation_read_frame", "rimgovernor/presentation_acknowledge_frame"} {
		if !na.Contains(s.Names, tool) {
			return fmt.Errorf("missing %s in discovery", tool)
		}
	}
	// Unpaused so successive captured frames are actually likely to differ
	// (colonist and camera-adjacent animation), not just carry a new sequence.
	if _, err := h.Call(ctx, "unpause", "rimworld/set_time_speed", map[string]any{"speed": "Normal", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	viewer := map[string]any{"identity": identity, "playerDirection": 1, "viewerId": "videostreamaccept"}

	// Case 1: LeaseVideo start with a five-second lease.
	startReply, err := h.Wire(ctx, "video-lease-start", "presentation_lease_video", map[string]any{
		"start": map[string]any{"viewer": viewer, "leaseSeconds": 5},
	})
	if err != nil {
		return fmt.Errorf("video-lease-start: %w", err)
	}
	_, startState, err := na.Outcome(startReply, "state")
	if err != nil {
		return fmt.Errorf("video-lease-start: expected a state outcome: %w", err)
	}
	if supported, _ := na.AsBool(startState["supported"]); !supported {
		return fmt.Errorf("video-lease-start: rendered game reported supported=false")
	}
	if active, _ := na.AsBool(startState["active"]); !active {
		return fmt.Errorf("video-lease-start: reported active=false")
	}
	sourceID := na.AsString(startState["sourceId"])
	if sourceID == "" {
		return fmt.Errorf("video-lease-start: missing sourceId")
	}
	remaining := na.AsNumber(startState["remainingLeaseMs"])
	if remaining <= 0 || remaining > 5000 {
		return fmt.Errorf("video-lease-start: unexpected remainingLeaseMs %v", startState["remainingLeaseMs"])
	}
	report["case_video_lease_start"] = startState

	// Case 2: multiple ReadFrame polls prove a strictly increasing sequence
	// and, given unpaused animation, updated pixel bytes.
	type polledFrame struct {
		sequence      float64
		width, height any
		data          []byte
	}
	var polls []polledFrame
	deadline := time.Now().Add(3 * time.Second)
	for len(polls) < 4 && time.Now().Before(deadline) {
		frameReply, err := h.Wire(ctx, "read-frame", "presentation_read_frame", map[string]any{
			"viewer": viewer, "sourceId": sourceID,
		})
		if err != nil {
			return fmt.Errorf("read-frame: %w", err)
		}
		_, frame, err := na.Outcome(frameReply, "frame")
		if err != nil {
			return fmt.Errorf("read-frame: expected a frame outcome: %w", err)
		}
		frameRef, ok := na.AsMap(frame["frame"])
		if !ok {
			return fmt.Errorf("read-frame: frame reply missing frame reference: %v", frame)
		}
		sequence := na.AsNumber(frameRef["sequence"])
		// MediaFrame.data is a proto bytes field: ProtoJSON carries it base64-encoded.
		data, err := base64.StdEncoding.DecodeString(na.AsString(frame["data"]))
		if err != nil {
			return fmt.Errorf("read-frame: invalid base64 frame data: %w", err)
		}
		if len(polls) == 0 || polls[len(polls)-1].sequence != sequence {
			polls = append(polls, polledFrame{sequence, frame["width"], frame["height"], data})
		}
		time.Sleep(150 * time.Millisecond)
	}
	if len(polls) < 2 {
		return fmt.Errorf("read-frame: expected at least two distinct sequences, got %d", len(polls))
	}
	for i := 1; i < len(polls); i++ {
		if polls[i].sequence <= polls[i-1].sequence {
			return fmt.Errorf("read-frame: sequence did not strictly increase: %v then %v", polls[i-1].sequence, polls[i].sequence)
		}
	}
	if len(polls[0].data) == 0 {
		return fmt.Errorf("read-frame: empty frame data")
	}
	differed := false
	for i := 1; i < len(polls); i++ {
		if len(polls[i].data) != len(polls[0].data) {
			return fmt.Errorf("read-frame: frame byte length changed across polls (%d vs %d)", len(polls[i].data), len(polls[0].data))
		}
		if !bytes.Equal(polls[i].data, polls[0].data) {
			differed = true
		}
	}
	report["case_read_frame_sequences"] = len(polls)
	report["case_read_frame_bytes_updated"] = differed
	lastPoll := polls[len(polls)-1]

	// Case 2b: the same source read straight out of shared memory, as the
	// controller's video relay does on the game's host. Frames must keep the
	// RPC frame's geometry and byte length, advance strictly, and arrive at a
	// materially higher rate than the base64 ReadFrame polls above.
	shared, err := videoshm.Open(sourceID)
	if err != nil {
		return fmt.Errorf("shared-memory-open %q: %w", sourceID, err)
	}
	defer shared.Close()
	rpcWidth, rpcHeight := int(na.AsNumber(polls[0].width)), int(na.AsNumber(polls[0].height))
	var sharedSequences []uint64
	sharedStarted := time.Now()
	sharedDeadline := sharedStarted.Add(2 * time.Second)
	var previous uint64
	for time.Now().Before(sharedDeadline) {
		frame, ok, err := shared.Read(previous)
		if err != nil {
			return fmt.Errorf("shared-memory-read: %w", err)
		}
		if ok {
			if frame.Sequence <= previous {
				return fmt.Errorf("shared-memory-read: sequence did not strictly increase: %d then %d", previous, frame.Sequence)
			}
			if frame.Width != rpcWidth || frame.Height != rpcHeight || len(frame.Data) != len(polls[0].data) {
				return fmt.Errorf("shared-memory-read: %dx%d/%d bytes does not match the ReadFrame frame %dx%d/%d bytes",
					frame.Width, frame.Height, len(frame.Data), rpcWidth, rpcHeight, len(polls[0].data))
			}
			if frame.CapturedUnixMs <= 0 {
				return fmt.Errorf("shared-memory-read: invalid capturedUnixMs %d", frame.CapturedUnixMs)
			}
			previous = frame.Sequence
			sharedSequences = append(sharedSequences, frame.Sequence)
		}
		time.Sleep(5 * time.Millisecond)
	}
	sharedWindow := time.Since(sharedStarted)
	if len(sharedSequences) < 2 {
		return fmt.Errorf("shared-memory-read: expected at least two frames in %s, got %d", sharedWindow, len(sharedSequences))
	}
	if sharedSequences[0] < uint64(lastPoll.sequence) {
		return fmt.Errorf("shared-memory-read: first shared sequence %d is behind the last ReadFrame sequence %v", sharedSequences[0], lastPoll.sequence)
	}
	sharedRate := float64(len(sharedSequences)) / sharedWindow.Seconds()
	report["case_shared_memory_frames"] = len(sharedSequences)
	report["case_shared_memory_frames_per_second"] = sharedRate
	report["case_shared_memory_first_sequence"] = sharedSequences[0]
	report["case_shared_memory_last_sequence"] = sharedSequences[len(sharedSequences)-1]
	// ReadFrame polls at 150 ms plus a base64 round trip cannot exceed
	// ~6 fps; shared memory must clear that comfortably to be worth having.
	if sharedRate < 10 {
		return fmt.Errorf("shared-memory-read: %.1f frames/s is no better than the ReadFrame poll", sharedRate)
	}
	// Case 3: AcknowledgeFrame for the most recently observed frame.
	ackReply, err := h.Wire(ctx, "acknowledge-frame", "presentation_acknowledge_frame", map[string]any{
		"viewer":          viewer,
		"frame":           map[string]any{"sourceId": sourceID, "sequence": fmt.Sprintf("%d", int64(lastPoll.sequence))},
		"displayedUnixMs": time.Now().UnixMilli(),
	})
	if err != nil {
		return fmt.Errorf("acknowledge-frame: %w", err)
	}
	_, acknowledged, err := na.Outcome(ackReply, "acknowledged")
	if err != nil {
		return fmt.Errorf("acknowledge-frame: expected an acknowledged outcome: %w", err)
	}
	ackFrame, _ := na.AsMap(acknowledged["frame"])
	if na.AsString(ackFrame["sourceId"]) != sourceID || na.AsNumber(ackFrame["sequence"]) != lastPoll.sequence {
		return fmt.Errorf("acknowledge-frame: reference mismatch %v", ackFrame)
	}
	report["case_acknowledge_frame"] = ackFrame

	// Case 4: LeaseVideo stop, reflected immediately in its own reply state.
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	stopReply, err := h.Wire(ctx, "video-lease-stop", "presentation_lease_video", map[string]any{
		"stop": map[string]any{"viewer": viewer},
	})
	if err != nil {
		return fmt.Errorf("video-lease-stop: %w", err)
	}
	_, stopState, err := na.Outcome(stopReply, "state")
	if err != nil {
		return fmt.Errorf("video-lease-stop: expected a state outcome: %w", err)
	}
	if active, _ := na.AsBool(stopState["active"]); active {
		return fmt.Errorf("video-lease-stop: still reported active=true")
	}
	report["case_video_lease_stop"] = stopState

	// Case 5: a subsequent ReadFrame against the now-stopped source must be a
	// typed failure, never a crash or a stale successful frame.
	afterStopReply, err := h.Wire(ctx, "read-frame-after-stop", "presentation_read_frame", map[string]any{
		"viewer": viewer, "sourceId": sourceID,
	})
	if err != nil {
		return fmt.Errorf("read-frame-after-stop: %w", err)
	}
	if code, ok := na.FailureCode(afterStopReply); !ok || code == "" {
		return fmt.Errorf("read-frame-after-stop: expected a typed failure, got %v", afterStopReply)
	} else {
		report["case_read_frame_after_stop_failure_code"] = code
	}

	// Case 5b: the released buffer publishes nothing further; a reader that
	// still holds the mapping sees no new sequence past the one current once
	// the stop returned (frames kept landing between the window and the stop).
	current, ok, err := shared.Read(0)
	if err != nil || !ok {
		return fmt.Errorf("shared-memory-read-after-stop: no current frame (%v)", err)
	}
	lastShared := current.Sequence
	time.Sleep(300 * time.Millisecond)
	if _, ok, err := shared.Read(lastShared); err != nil {
		return fmt.Errorf("shared-memory-read-after-stop: %w", err)
	} else if ok {
		return fmt.Errorf("shared-memory-read-after-stop: a frame was published after the lease stopped")
	}
	report["case_shared_memory_quiet_after_stop"] = true

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	if err := na.CheckStartupLog(string(logData), false); err != nil {
		return err
	}
	return nil
}
