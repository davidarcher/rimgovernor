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
	report := na.NewReport("LeaseVideo start against a real rendered game, multiple ReadFrame polls proving strictly increasing sequence and updated frame bytes, AcknowledgeFrame success, and LeaseVideo stop reflected by a subsequent ReadFrame refusal. CaptureScreenshot and the whole PlayerPresentation service are out of scope. The WebSocket relay is verified separately with a fake bridge client.", false)
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
	if err := cfg.PrepareConfig(); err != nil {
		return fmt.Errorf("prepare profile: %w", err)
	}
	game, err := cfg.GameSection()
	if err != nil {
		return err
	}
	files, err := na.PackageFiles(fmt.Sprint(game["workingDir"]))
	if err != nil {
		return err
	}
	report["package_files"] = files
	gabsExecutable, err := na.GABSExecutable(root, cfg.Configuration)
	if err != nil {
		return err
	}
	client, err := na.OpenSession(ctx, gabsExecutable, cfg.Configuration, gameID, 90*time.Second)
	if err != nil {
		return err
	}
	defer func() {
		stopCtx, stopCancel := context.WithTimeout(context.Background(), 60*time.Second)
		defer stopCancel()
		if stopped, err := client.GamesStop(stopCtx); err == nil {
			report["stop"] = string(stopped.Envelope)
		} else {
			report["stop_error"] = err.Error()
		}
		_ = client.Close()
	}()
	h := na.NewHarness(client, output)

	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	report["discovery"] = names
	for _, tool := range []string{"rimgovernor/presentation_lease_video", "rimgovernor/presentation_read_frame", "rimgovernor/presentation_acknowledge_frame"} {
		if !na.Contains(names, tool) {
			return fmt.Errorf("missing %s in discovery", tool)
		}
	}
	if _, err := h.Call(ctx, "new-game", "rimworld/start_debug_game_ready", map[string]any{
		"readiness": "visual", "pauseIfNeeded": true, "timeoutMs": 120000,
	}); err != nil {
		return err
	}
	// Unpaused so successive captured frames are actually likely to differ
	// (colonist and camera-adjacent animation), not just carry a new sequence.
	if _, err := h.Call(ctx, "unpause", "rimworld/set_time_speed", map[string]any{"speed": "Normal", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	identityReply, err := h.Wire(ctx, "identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, loaded, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return err
	}
	loadedContext, _ := na.AsMap(loaded["context"])
	identity, _ := na.AsMap(loadedContext["identity"])
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
		sequence float64
		data     []byte
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
			polls = append(polls, polledFrame{sequence, data})
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

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	if err := na.CheckStartupLog(string(logData), false); err != nil {
		return err
	}
	return nil
}
