// Package video holds the rendered-profile cases: video capture needs
// Find.Camera != null, which batch mode never has, so every case declares
// Rendered and the runner opens the windowed profile whatever -headless
// says. A headless run can only exercise LeaseVideo/ReadFrame's
// batch-mode-refusal paths.
//
// stream (the former videostreamaccept) is G01.09's video streaming
// slice: PresentationMedia.LeaseVideo, ReadFrame and AcknowledgeFrame
// against the real native mod. CaptureScreenshot and the whole
// PlayerPresentation service (camera/input ownership) remain out of scope.
// The WebSocket relay itself (go/internal/httpapi/video_stream.go) is
// verified separately with a fake bridge client; this case only proves
// the underlying RPCs work against the real game.
//
// feeds (the former videofeedsaccept) proves the multi-source native
// video driver (#21 stage B): a colonist feed and a whole-map feed leased
// beside the screen, each publishing to its own shared-memory buffer at
// its own cadence, readable by source through ReadFrame, and stoppable
// one at a time. It also saves one decoded frame per feed for inspection.
//
// matrix (the former videofeedsmatrix) is the issue #21 stage D
// acceptance matrix for live feeds with the VideoMatrixFixture build:
// colonists spanning body types, apparel and weapon kinds each render in
// their own feed; several pawn feeds, the whole map and the screen
// publish concurrently; a removed pawn ends its feed and cannot be
// re-leased; a native load ends the map-bound feeds and the same pawn
// re-leases on the new map; and the frame-time and tick cost of each
// configuration is measured against a no-feed baseline.
//
// source-spike (the former videosourcespike) drives the disposable
// test/video_source_spike fixture (build_native_mod.ps1 -Fixture
// VideoSourceFixture) for issue #21 stage B: it saves a colonist feed
// rendered by a second camera at near and far main-camera zoom, a
// whole-map frame, and the per-frame cost of baseline / pawn feeds /
// whole-map culling while paused and at Normal speed. It is evidence
// gathering more than a pass/fail acceptance.
package video

import (
	"bytes"
	"context"
	"encoding/base64"
	"fmt"
	"image"
	"image/color"
	"image/png"
	"math"
	"os"
	"path/filepath"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/videoshm"
)

func init() {
	cases.Register(cases.Case{
		Name:     "video/stream",
		Scope:    "LeaseVideo start against a real rendered game, multiple ReadFrame polls proving strictly increasing sequence and updated frame bytes, the same source read out of shared memory at a materially higher rate with matching geometry, AcknowledgeFrame success, and LeaseVideo stop reflected by a subsequent ReadFrame refusal and a quiet shared buffer. CaptureScreenshot and the whole PlayerPresentation service are out of scope. The WebSocket relay is verified separately with a fake bridge client.",
		Start:    cases.DebugStart{},
		Quiet:    na.QuietIfAvailable,
		Reason:   "also runs against a production build, which carries no quiet op",
		Rendered: true,
		Budget:   6 * time.Minute,
		Run:      runStream,
	})
	cases.Register(cases.Case{
		Name:     "video/feeds",
		Scope:    "LeaseVideo with pawn and map sources beside the screen source: three concurrent buffers, per-source cadence and geometry read from shared memory, ReadFrame by source id, invalid-source typed failures, stopping one source leaves the others publishing, stopping all is quiet, and two viewers of one source hold it independently.",
		Start:    cases.DebugStart{},
		Quiet:    na.QuietIfAvailable,
		Reason:   "also runs against a production build, which carries no quiet op",
		Rendered: true,
		Budget:   6 * time.Minute,
		Run:      runFeeds,
	})
	cases.Register(cases.Case{
		Name:     "video/matrix",
		Scope:    "Live feed matrix: varied colonists each render in their feed, feeds + map + screen publish concurrently, a removed pawn ends its feed and is refused on re-lease, a native load ends map-bound feeds and the pawn re-leases on the new map, and per-configuration frame-time and tick cost is measured.",
		Start:    cases.DebugStart{},
		Rendered: true,
		Budget:   10 * time.Minute,
		Run:      runMatrix,
	})
	cases.Register(cases.Case{
		Name:     "video/source-spike",
		Scope:    "Stage B spike evidence: second-camera colonist feed at near and far main zoom, whole-map frame, and per-frame draw cost for baseline, pawn feeds and whole-map culling while paused and at Normal speed.",
		Start:    cases.DebugStart{},
		Rendered: true,
		Budget:   6 * time.Minute,
		Run:      runSourceSpike,
	})
}

func runStream(ctx context.Context, s cases.Session) error {
	report, h, identity := s.Report(), s.Harness(), s.Identity()
	for _, tool := range []string{"rimgovernor/presentation_lease_video", "rimgovernor/presentation_read_frame", "rimgovernor/presentation_acknowledge_frame"} {
		if !na.Contains(s.Names(), tool) {
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
	pollStarted := time.Now()
	deadline := pollStarted.Add(3 * time.Second)
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
	// Distinct frames the poll delivered per second: the rate shared memory
	// has to beat on this machine, under whatever peer load it carries.
	pollRate := float64(len(polls)) / time.Since(pollStarted).Seconds()
	report["case_read_frame_sequences"] = len(polls)
	report["case_read_frame_bytes_updated"] = differed
	report["case_read_frame_frames_per_second"] = pollRate
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
	firstShared, lastShared := sharedSequences[0], sharedSequences[len(sharedSequences)-1]
	published := lastShared - firstShared + 1
	report["case_shared_memory_frames"] = len(sharedSequences)
	report["case_shared_memory_frames_per_second"] = sharedRate
	report["case_shared_memory_first_sequence"] = firstShared
	report["case_shared_memory_last_sequence"] = lastShared
	report["case_shared_memory_published_sequences"] = published
	// The game's capture cadence caps both readers, and it drops with peer
	// load on the box, so the floor is relative to this run rather than an
	// absolute fps: shared memory must deliver materially more frames per
	// second than the ReadFrame poll did, and must see nearly every sequence
	// the source published in its window (a reader that drops frames is no
	// faster than the source in any useful sense).
	if sharedRate < 1.2*pollRate {
		return fmt.Errorf("shared-memory-read: %.1f frames/s is no better than the ReadFrame poll's %.1f frames/s", sharedRate, pollRate)
	}
	if seen := float64(len(sharedSequences)) / float64(published); seen < 0.9 {
		return fmt.Errorf("shared-memory-read: saw %d of %d published sequences (%.0f%%)", len(sharedSequences), published, 100*seen)
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
	atStop := current.Sequence
	time.Sleep(300 * time.Millisecond)
	if _, ok, err := shared.Read(atStop); err != nil {
		return fmt.Errorf("shared-memory-read-after-stop: %w", err)
	} else if ok {
		return fmt.Errorf("shared-memory-read-after-stop: a frame was published after the lease stopped")
	}
	report["case_shared_memory_quiet_after_stop"] = true

	return cases.CheckStartupLog(s)
}

// leasedFeed is one leased source of the feeds case.
type leasedFeed struct {
	label  string
	source string
	spec   map[string]any
	state  map[string]any
	reader videoshm.Reader
	frames []videoshm.Frame
}

func runFeeds(ctx context.Context, s cases.Session) error {
	report, h, identity, output := s.Report(), s.Harness(), s.Identity(), s.Config().Output
	for _, tool := range []string{"rimgovernor/presentation_lease_video", "rimgovernor/presentation_read_frame", "rimgovernor/presentation_colonists"} {
		if !na.Contains(s.Names(), tool) {
			return fmt.Errorf("missing %s in discovery", tool)
		}
	}
	if _, err := h.Call(ctx, "unpause", "rimworld/set_time_speed", map[string]any{"speed": "Normal", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	viewer := map[string]any{"identity": identity, "playerDirection": 1, "viewerId": "videofeedsaccept"}

	colonistsReply, err := h.Wire(ctx, "colonists", "presentation_colonists", map[string]any{"identity": identity})
	if err != nil {
		return err
	}
	_, roster, err := na.Outcome(colonistsReply, "roster")
	if err != nil {
		return err
	}
	colonists := na.AsSlice(roster["colonists"])
	if len(colonists) == 0 {
		return fmt.Errorf("colonists: fresh debug game has no spawned colonists")
	}
	first, _ := na.AsMap(colonists[0])
	pawnID := na.AsString(first["pawnId"])
	if pawnID == "" {
		return fmt.Errorf("colonists: first colonist has no pawnId")
	}
	report["pawn_id"] = pawnID

	lease := func(label string, source map[string]any, seconds int) (map[string]any, error) {
		start := map[string]any{"viewer": viewer, "leaseSeconds": seconds}
		if source != nil {
			start["source"] = source
		}
		reply, err := h.Wire(ctx, label, "presentation_lease_video", map[string]any{"start": start})
		if err != nil {
			return nil, fmt.Errorf("%s: %w", label, err)
		}
		_, state, err := na.Outcome(reply, "state")
		if err != nil {
			return nil, fmt.Errorf("%s: expected a state outcome: %w", label, err)
		}
		if active, _ := na.AsBool(state["active"]); !active || na.AsString(state["sourceId"]) == "" {
			return nil, fmt.Errorf("%s: not active: %v", label, state)
		}
		return state, nil
	}

	// Case 1: three concurrent leases, each with its own source id.
	feeds := []*leasedFeed{
		{label: "screen", spec: nil},
		{label: "pawn", spec: map[string]any{"kind": "VIDEO_SOURCE_KIND_PAWN", "pawnId": pawnID, "width": 320, "height": 200, "framesPerSecond": 15}},
		{label: "map", spec: map[string]any{"kind": "VIDEO_SOURCE_KIND_MAP", "height": 400, "framesPerSecond": 4}},
	}
	seen := map[string]bool{}
	for _, f := range feeds {
		state, err := lease("lease-"+f.label, f.spec, 8)
		if err != nil {
			return err
		}
		f.state, f.source = state, na.AsString(state["sourceId"])
		if seen[f.source] {
			return fmt.Errorf("lease-%s: source id %q reused across sources", f.label, f.source)
		}
		seen[f.source] = true
		echoed, _ := na.AsMap(state["source"])
		wantKind := "VIDEO_SOURCE_KIND_SCREEN"
		if f.spec != nil {
			wantKind = na.AsString(f.spec["kind"])
		}
		if na.AsString(echoed["kind"]) != wantKind {
			return fmt.Errorf("lease-%s: source echo %v does not name %s", f.label, echoed, wantKind)
		}
		report["case_lease_"+f.label] = state
	}
	// Re-leasing an identical spec extends the same source rather than
	// opening a second buffer.
	again, err := lease("lease-pawn-again", feeds[1].spec, 8)
	if err != nil {
		return err
	}
	if na.AsString(again["sourceId"]) != feeds[1].source {
		return fmt.Errorf("lease-pawn-again: identical spec opened a new source %q", again["sourceId"])
	}

	// Case 2: invalid sources are typed failures.
	for label, source := range map[string]map[string]any{
		"pawn-without-id": {"kind": "VIDEO_SOURCE_KIND_PAWN"},
		"map-too-fast":    {"kind": "VIDEO_SOURCE_KIND_MAP", "framesPerSecond": 60},
		"pawn-too-small":  {"kind": "VIDEO_SOURCE_KIND_PAWN", "pawnId": pawnID, "width": 4, "height": 4},
	} {
		reply, err := h.Wire(ctx, "lease-"+label, "presentation_lease_video", map[string]any{
			"start": map[string]any{"viewer": viewer, "leaseSeconds": 5, "source": source},
		})
		if err != nil {
			return err
		}
		if code, ok := na.FailureCode(reply); !ok || code != "FAILURE_CODE_INVALID_REQUEST" {
			return fmt.Errorf("lease-%s: expected INVALID_REQUEST, got %v", label, reply)
		}
	}
	report["case_invalid_sources"] = true

	// Case 3: every source publishes to its own buffer at its own cadence.
	for _, f := range feeds {
		f.reader, err = videoshm.Open(f.source)
		if err != nil {
			return fmt.Errorf("open %s buffer %q: %w", f.label, f.source, err)
		}
		defer f.reader.Close()
	}
	window := 3 * time.Second
	started := time.Now()
	for time.Since(started) < window {
		for _, f := range feeds {
			var previous uint64
			if n := len(f.frames); n > 0 {
				previous = f.frames[n-1].Sequence
			}
			frame, ok, err := f.reader.Read(previous)
			if err != nil {
				return fmt.Errorf("%s read: %w", f.label, err)
			}
			if ok {
				f.frames = append(f.frames, frame)
			}
		}
		time.Sleep(5 * time.Millisecond)
	}
	elapsed := time.Since(started).Seconds()
	rates := map[string]float64{}
	for _, f := range feeds {
		rates[f.label] = float64(len(f.frames)) / elapsed
	}
	report["case_frames_per_second"] = rates
	// The producer's own count, for comparison with what the readers saw.
	published := map[string]any{}
	for _, f := range feeds {
		state, err := lease("release-"+f.label, f.spec, 8)
		if err != nil {
			return err
		}
		published[f.label] = map[string]any{"capturedFrames": state["capturedFrames"], "frameSeconds": state["frameSeconds"], "read": len(f.frames)}
	}
	report["case_published_frames"] = published
	if rates["screen"] < 10 {
		return fmt.Errorf("screen: %.1f fps with feeds leased", rates["screen"])
	}
	if rates["pawn"] < 8 || rates["pawn"] > 31 {
		return fmt.Errorf("pawn: %.1f fps, expected about 15", rates["pawn"])
	}
	if rates["map"] < 2 || rates["map"] > 11 {
		return fmt.Errorf("map: %.1f fps, expected about 4", rates["map"])
	}
	pawnFrame := feeds[1].frames[len(feeds[1].frames)-1]
	if pawnFrame.Width != 320 || pawnFrame.Height != 200 || len(pawnFrame.Data) != 320*200*4 {
		return fmt.Errorf("pawn: frame is %dx%d/%d bytes", pawnFrame.Width, pawnFrame.Height, len(pawnFrame.Data))
	}
	mapFrame := feeds[2].frames[len(feeds[2].frames)-1]
	if mapFrame.Height != 400 || mapFrame.Width < 100 || mapFrame.Width > 1600 {
		return fmt.Errorf("map: frame is %dx%d", mapFrame.Width, mapFrame.Height)
	}
	for _, f := range feeds[1:] {
		frame := f.frames[len(f.frames)-1]
		spread, err := savePNG(filepath.Join(output, f.label+".png"), frame)
		if err != nil {
			return err
		}
		report["case_"+f.label+"_pixel_spread"] = spread
		// A blank (unrendered) target has no variation at all.
		if spread < 1.5 {
			return fmt.Errorf("%s: frame looks blank (pixel spread %.1f)", f.label, spread)
		}
		report["case_"+f.label+"_readback_ms"] = frame.ReadbackMs
	}

	// Case 4: ReadFrame selects a source by id and reports its geometry.
	reply, err := h.Wire(ctx, "read-frame-pawn", "presentation_read_frame", map[string]any{"viewer": viewer, "sourceId": feeds[1].source})
	if err != nil {
		return err
	}
	_, rpcFrame, err := na.Outcome(reply, "frame")
	if err != nil {
		return fmt.Errorf("read-frame-pawn: %w", err)
	}
	ref, _ := na.AsMap(rpcFrame["frame"])
	if na.AsString(ref["sourceId"]) != feeds[1].source || na.AsNumber(rpcFrame["width"]) != 320 || na.AsNumber(rpcFrame["height"]) != 200 {
		return fmt.Errorf("read-frame-pawn: wrong source or geometry: %v %v %v", ref, rpcFrame["width"], rpcFrame["height"])
	}
	data, err := base64.StdEncoding.DecodeString(na.AsString(rpcFrame["data"]))
	if err != nil || len(data) != 320*200*4 {
		return fmt.Errorf("read-frame-pawn: %d bytes (%v)", len(data), err)
	}
	report["case_read_frame_pawn_sequence"] = ref["sequence"]

	// Case 5: stopping one source leaves the others publishing. The buffers
	// keep their last frame after a stop, so "quiet" means no sequence past
	// the one current at the moment of stopping.
	latest := func(f *leasedFeed) (uint64, error) {
		frame, ok, err := f.reader.Read(0)
		if err != nil || !ok {
			return 0, fmt.Errorf("%s: no current frame (%v)", f.label, err)
		}
		return frame.Sequence, nil
	}
	stopReply, err := h.Wire(ctx, "stop-pawn", "presentation_lease_video", map[string]any{
		"stop": map[string]any{"viewer": viewer, "sourceId": feeds[1].source},
	})
	if err != nil {
		return err
	}
	if _, _, err := na.Outcome(stopReply, "state"); err != nil {
		return fmt.Errorf("stop-pawn: %w", err)
	}
	reply, err = h.Wire(ctx, "read-frame-pawn-after-stop", "presentation_read_frame", map[string]any{"viewer": viewer, "sourceId": feeds[1].source})
	if err != nil {
		return err
	}
	if code, ok := na.FailureCode(reply); !ok || code == "" {
		return fmt.Errorf("read-frame-pawn-after-stop: expected a typed failure, got %v", reply)
	}
	// A frame in flight at the moment of the stop may still land; the
	// sequence current after the RPC returns is the bar.
	lastPawn, err := latest(feeds[1])
	if err != nil {
		return err
	}
	lastMap, err := latest(feeds[2])
	if err != nil {
		return err
	}
	lastScreen, err := latest(feeds[0])
	if err != nil {
		return err
	}
	time.Sleep(600 * time.Millisecond)
	if _, ok, err := feeds[1].reader.Read(lastPawn); err != nil {
		return err
	} else if ok {
		return fmt.Errorf("stop-pawn: the pawn buffer kept publishing")
	}
	mapAfter, ok, err := feeds[2].reader.Read(lastMap)
	if err != nil || !ok {
		return fmt.Errorf("stop-pawn: the map feed stopped too (%v)", err)
	}
	screenAfter, ok, err := feeds[0].reader.Read(lastScreen)
	if err != nil || !ok {
		return fmt.Errorf("stop-pawn: the screen feed stopped too (%v)", err)
	}
	report["case_stop_one"] = map[string]any{"map_sequence_after": mapAfter.Sequence, "screen_sequence_after": screenAfter.Sequence}

	// Case 6: stop without a source id ends everything.
	if _, err := h.Wire(ctx, "stop-all", "presentation_lease_video", map[string]any{"stop": map[string]any{"viewer": viewer}}); err != nil {
		return err
	}
	lastMap, err = latest(feeds[2])
	if err != nil {
		return err
	}
	lastScreen, err = latest(feeds[0])
	if err != nil {
		return err
	}
	time.Sleep(600 * time.Millisecond)
	for f, last := range map[*leasedFeed]uint64{feeds[0]: lastScreen, feeds[2]: lastMap} {
		if _, ok, err := f.reader.Read(last); err != nil {
			return err
		} else if ok {
			return fmt.Errorf("stop-all: %s kept publishing", f.label)
		}
	}
	report["case_stop_all_quiet"] = true

	// Case 7: two viewers of one spec share a source and hold it
	// independently: the first viewer's stop leaves the second's feed
	// running; the second's stop ends it.
	viewerB := map[string]any{"identity": identity, "playerDirection": 1, "viewerId": "videofeedsaccept-b"}
	mapSource := map[string]any{"kind": "VIDEO_SOURCE_KIND_MAP", "height": 400, "framesPerSecond": 4}
	stateA, err := lease("lease-map-viewer-a", mapSource, 8)
	if err != nil {
		return err
	}
	replyB, err := h.Wire(ctx, "lease-map-viewer-b", "presentation_lease_video", map[string]any{
		"start": map[string]any{"viewer": viewerB, "leaseSeconds": 8, "source": mapSource},
	})
	if err != nil {
		return err
	}
	_, stateB, err := na.Outcome(replyB, "state")
	if err != nil {
		return fmt.Errorf("lease-map-viewer-b: %w", err)
	}
	shared := na.AsString(stateA["sourceId"])
	if shared == "" || na.AsString(stateB["sourceId"]) != shared {
		return fmt.Errorf("competing viewers: expected one shared source, got %q and %q", shared, na.AsString(stateB["sourceId"]))
	}
	sharedReader, err := videoshm.Open(shared)
	if err != nil {
		return fmt.Errorf("open shared map buffer %q: %w", shared, err)
	}
	defer sharedReader.Close()
	if _, err := h.Wire(ctx, "stop-map-viewer-a", "presentation_lease_video", map[string]any{
		"stop": map[string]any{"viewer": viewer, "sourceId": shared},
	}); err != nil {
		return err
	}
	time.Sleep(300 * time.Millisecond)
	before, ok, err := sharedReader.Read(0)
	if err != nil || !ok {
		return fmt.Errorf("competing viewers: no frame after viewer A stopped (%v)", err)
	}
	time.Sleep(800 * time.Millisecond)
	if _, ok, err := sharedReader.Read(before.Sequence); err != nil {
		return err
	} else if !ok {
		return fmt.Errorf("competing viewers: viewer A's stop ended viewer B's map feed")
	}
	if _, err := h.Wire(ctx, "stop-map-viewer-b", "presentation_lease_video", map[string]any{
		"stop": map[string]any{"viewer": viewerB, "sourceId": shared},
	}); err != nil {
		return err
	}
	last, ok, err := sharedReader.Read(0)
	if err != nil || !ok {
		return fmt.Errorf("competing viewers: no frame current at viewer B's stop (%v)", err)
	}
	time.Sleep(600 * time.Millisecond)
	if _, ok, err := sharedReader.Read(last.Sequence); err != nil {
		return err
	} else if ok {
		return fmt.Errorf("competing viewers: the map feed kept publishing after its last viewer stopped")
	}
	report["case_competing_viewers"] = map[string]any{"shared_source": shared, "sequence_after_a_stopped": before.Sequence}

	return cases.CheckStartupLog(s)
}

// feed is one leased source of the matrix case.
type feed struct {
	label  string
	spec   map[string]any
	source string
	reader videoshm.Reader
	frames []videoshm.Frame
}

func (f *feed) last() uint64 {
	if n := len(f.frames); n > 0 {
		return f.frames[n-1].Sequence
	}
	return 0
}

func runMatrix(ctx context.Context, s cases.Session) error {
	report, h, identity, output := s.Report(), s.Harness(), s.Identity(), s.Config().Output
	for _, tool := range []string{"rimgovernor/presentation_lease_video", "rimgovernor/presentation_read_frame", "test/video_matrix_fixture"} {
		if !na.Contains(s.Names(), tool) {
			return fmt.Errorf("missing %s in discovery (build with -Fixture VideoMatrixFixture)", tool)
		}
	}
	var err error
	viewer := map[string]any{"identity": identity, "playerDirection": 1, "viewerId": "videofeedsmatrix"}

	// The matrix colonists: one per body type, each with its own apparel and
	// weapon kind (ranged, melee, none).
	spawnReply, err := h.Call(ctx, "spawn", "test/video_matrix_fixture", map[string]any{"mode": "spawn"})
	if err != nil {
		return err
	}
	var pawns []map[string]any
	for _, p := range na.AsSlice(spawnReply["pawns"]) {
		pawn, _ := na.AsMap(p)
		if na.AsString(pawn["pawnId"]) == "" {
			return fmt.Errorf("spawn: pawn without id: %v", pawn)
		}
		pawns = append(pawns, pawn)
	}
	if len(pawns) < 5 {
		return fmt.Errorf("spawn: expected five matrix colonists, got %d", len(pawns))
	}
	report["matrix_pawns"] = pawns
	if _, err := h.Call(ctx, "unpause", "rimworld/set_time_speed", map[string]any{"speed": "Normal", "ultraSpeedBoost": false}); err != nil {
		return err
	}

	lease := func(label string, spec map[string]any, seconds int) (map[string]any, error) {
		start := map[string]any{"viewer": viewer, "leaseSeconds": seconds}
		if spec != nil {
			start["source"] = spec
		}
		reply, err := h.Wire(ctx, label, "presentation_lease_video", map[string]any{"start": start})
		if err != nil {
			return nil, fmt.Errorf("%s: %w", label, err)
		}
		_, state, err := na.Outcome(reply, "state")
		if err != nil {
			return nil, fmt.Errorf("%s: expected a state outcome: %w", label, err)
		}
		return state, nil
	}
	leaseActive := func(label string, spec map[string]any, seconds int) (string, error) {
		state, err := lease(label, spec, seconds)
		if err != nil {
			return "", err
		}
		if active, _ := na.AsBool(state["active"]); !active || na.AsString(state["sourceId"]) == "" {
			return "", fmt.Errorf("%s: not active: %v", label, state)
		}
		return na.AsString(state["sourceId"]), nil
	}
	stopAll := func(label string) error {
		_, err := h.Wire(ctx, label, "presentation_lease_video", map[string]any{"stop": map[string]any{"viewer": viewer}})
		return err
	}
	pawnSpec := func(pawn map[string]any) map[string]any {
		return map[string]any{"kind": "VIDEO_SOURCE_KIND_PAWN", "pawnId": na.AsString(pawn["pawnId"]), "width": 320, "height": 200, "framesPerSecond": 15}
	}
	mapSpec := map[string]any{"kind": "VIDEO_SOURCE_KIND_MAP", "height": 400, "framesPerSecond": 4}

	// Reads every feed's buffer for the window and returns per-feed rates.
	sample := func(feeds []*feed, window time.Duration) (map[string]float64, error) {
		for _, f := range feeds {
			f.frames = nil
		}
		started := time.Now()
		for time.Since(started) < window {
			for _, f := range feeds {
				frame, ok, err := f.reader.Read(f.last())
				if err != nil {
					return nil, fmt.Errorf("%s read: %w", f.label, err)
				}
				if ok {
					f.frames = append(f.frames, frame)
				}
			}
			time.Sleep(5 * time.Millisecond)
		}
		elapsed := time.Since(started).Seconds()
		rates := map[string]float64{}
		for _, f := range feeds {
			rates[f.label] = float64(len(f.frames)) / elapsed
		}
		return rates, nil
	}
	measure := func(label string, feeds []*feed, window time.Duration) (map[string]any, map[string]float64, error) {
		if _, err := h.Call(ctx, label+"-sample-start", "test/video_matrix_fixture", map[string]any{"mode": "sample_start"}); err != nil {
			return nil, nil, err
		}
		rates, err := sample(feeds, window)
		if err != nil {
			return nil, nil, err
		}
		stats, err := h.Call(ctx, label+"-sample-stop", "test/video_matrix_fixture", map[string]any{"mode": "sample_stop"})
		if err != nil {
			return nil, nil, err
		}
		if na.AsNumber(stats["ticks"]) <= 0 {
			return nil, nil, fmt.Errorf("%s: the game did not tick during the sample: %v", label, stats)
		}
		return stats, rates, nil
	}
	open := func(feeds []*feed) error {
		for _, f := range feeds {
			reader, err := videoshm.Open(f.source)
			if err != nil {
				return fmt.Errorf("open %s buffer %q: %w", f.label, f.source, err)
			}
			f.reader = reader
		}
		return nil
	}
	closeAll := func(feeds []*feed) {
		for _, f := range feeds {
			if f.reader != nil {
				_ = f.reader.Close()
				f.reader = nil
			}
		}
	}
	const window = 4 * time.Second
	cost := map[string]any{}

	// Phase 0: no feeds.
	baseline, _, err := measure("baseline", nil, window)
	if err != nil {
		return err
	}
	cost["baseline"] = baseline

	// Phase 1: five pawn feeds, one per matrix colonist.
	var pawnFeeds []*feed
	for i, pawn := range pawns[:5] {
		f := &feed{label: fmt.Sprintf("pawn%d", i), spec: pawnSpec(pawn)}
		if f.source, err = leaseActive("lease-"+f.label, f.spec, 15); err != nil {
			return err
		}
		pawnFeeds = append(pawnFeeds, f)
	}
	if err := open(pawnFeeds); err != nil {
		return err
	}
	feedsOnly, feedRates, err := measure("pawn-feeds", pawnFeeds, window)
	if err != nil {
		return err
	}
	cost["pawn_feeds"] = feedsOnly
	report["case_pawn_feed_rates"] = feedRates

	// Case: every colonist variant renders, with the pawn at the centre of
	// its own feed and no two feeds showing the same picture.
	var frames []videoshm.Frame
	for i, f := range pawnFeeds {
		if len(f.frames) == 0 {
			return fmt.Errorf("%s: no frames in %s", f.label, window)
		}
		frame := f.frames[len(f.frames)-1]
		if frame.Width != 320 || frame.Height != 200 || len(frame.Data) != 320*200*4 {
			return fmt.Errorf("%s: frame is %dx%d/%d bytes", f.label, frame.Width, frame.Height, len(frame.Data))
		}
		spread, err := savePNG(filepath.Join(output, fmt.Sprintf("pawn%d-%s.png", i, na.AsString(pawns[i]["bodyType"]))), frame)
		if err != nil {
			return err
		}
		centre := patchContrast(frame, 40)
		report[fmt.Sprintf("case_render_%s", f.label)] = map[string]any{
			"bodyType": pawns[i]["bodyType"], "apparel": pawns[i]["apparel"], "weapon": pawns[i]["weapon"],
			"pixelSpread": spread, "centreContrast": centre, "readbackMs": frame.ReadbackMs,
		}
		if spread < 1.5 {
			return fmt.Errorf("%s: frame looks blank (pixel spread %.1f)", f.label, spread)
		}
		// The pawn stands in the centre of its feed; a centre patch of bare
		// terrain would be as flat as the frame's border.
		if centre < 4 {
			return fmt.Errorf("%s: nothing drawn at the centre of the feed (contrast %.1f)", f.label, centre)
		}
		frames = append(frames, frame)
	}
	for i := range frames {
		for j := i + 1; j < len(frames); j++ {
			if d := meanAbsDiff(frames[i], frames[j]); d < 2 {
				return fmt.Errorf("pawn%d and pawn%d feeds show the same picture (mean diff %.2f)", i, j, d)
			}
		}
	}
	for _, f := range pawnFeeds {
		if feedRates[f.label] < 6 {
			return fmt.Errorf("%s: %.1f fps with five feeds leased, expected about 15", f.label, feedRates[f.label])
		}
	}

	// Phase 2: the five feeds plus the whole map and the screen.
	mapFeed := &feed{label: "map", spec: mapSpec}
	if mapFeed.source, err = leaseActive("lease-map", mapSpec, 15); err != nil {
		return err
	}
	screenFeed := &feed{label: "screen"}
	if screenFeed.source, err = leaseActive("lease-screen", nil, 15); err != nil {
		return err
	}
	all := append(append([]*feed{}, pawnFeeds...), mapFeed, screenFeed)
	if err := open([]*feed{mapFeed, screenFeed}); err != nil {
		return err
	}
	// Renew the pawn feeds so none lapses mid-sample.
	for _, f := range pawnFeeds {
		if _, err := leaseActive("renew-"+f.label, f.spec, 15); err != nil {
			return err
		}
	}
	everything, allRates, err := measure("all-sources", all, window)
	if err != nil {
		return err
	}
	cost["pawn_feeds_map_screen"] = everything
	report["case_concurrent_rates"] = allRates
	report["case_cost"] = cost
	if allRates["map"] < 2 || allRates["screen"] < 8 {
		return fmt.Errorf("concurrent: map %.1f fps, screen %.1f fps", allRates["map"], allRates["screen"])
	}
	for _, f := range pawnFeeds {
		if allRates[f.label] < 6 {
			return fmt.Errorf("concurrent: %s fell to %.1f fps", f.label, allRates[f.label])
		}
	}
	if mapFrame := mapFeed.frames[len(mapFeed.frames)-1]; mapFrame.Height != 400 {
		return fmt.Errorf("map: frame is %dx%d", mapFrame.Width, mapFrame.Height)
	} else if _, err := savePNG(filepath.Join(output, "map.png"), mapFrame); err != nil {
		return err
	}
	baseTPS, allTPS := na.AsNumber(baseline["ticksPerSecond"]), na.AsNumber(everything["ticksPerSecond"])
	report["case_cost_summary"] = map[string]any{
		"baseline_tps": baseTPS, "pawn_feeds_tps": feedsOnly["ticksPerSecond"], "all_sources_tps": allTPS,
		"baseline_frame_ms_p50": baseline["frameMsP50"], "pawn_feeds_frame_ms_p50": feedsOnly["frameMsP50"], "all_sources_frame_ms_p50": everything["frameMsP50"],
		"baseline_frame_ms_p95": baseline["frameMsP95"], "pawn_feeds_frame_ms_p95": feedsOnly["frameMsP95"], "all_sources_frame_ms_p95": everything["frameMsP95"],
	}
	// Feeds must not stall the simulation: Normal speed is 60 ticks/s and a
	// rendered game on a loaded box already runs below that.
	if allTPS < 0.5*baseTPS {
		return fmt.Errorf("cost: ticks fell from %.1f/s to %.1f/s with every source leased", baseTPS, allTPS)
	}

	// Case: a removed pawn ends its feed and cannot be re-leased.
	removed := pawnFeeds[0]
	if _, err := h.Call(ctx, "remove", "test/video_matrix_fixture", map[string]any{"mode": "remove", "pawnId": na.AsString(pawns[0]["pawnId"])}); err != nil {
		return err
	}
	// The feed ends on its next due frame; a frame already in flight may
	// still land, so the bar is the sequence current shortly after.
	time.Sleep(300 * time.Millisecond)
	quietFrom, err := current(removed)
	if err != nil {
		return err
	}
	time.Sleep(700 * time.Millisecond)
	if _, ok, err := removed.reader.Read(quietFrom); err != nil {
		return err
	} else if ok {
		return fmt.Errorf("remove: the removed pawn's feed kept publishing")
	}
	reply, err := h.Wire(ctx, "read-frame-removed", "presentation_read_frame", map[string]any{"viewer": viewer, "sourceId": removed.source})
	if err != nil {
		return err
	}
	if code, ok := na.FailureCode(reply); !ok || code == "" {
		return fmt.Errorf("read-frame-removed: expected a typed failure, got %v", reply)
	}
	state, err := lease("release-removed", removed.spec, 15)
	if err != nil {
		return err
	}
	if active, _ := na.AsBool(state["active"]); active {
		return fmt.Errorf("release-removed: a feed of a removed pawn was leased: %v", state)
	}
	if supported, _ := na.AsBool(state["supported"]); !supported {
		return fmt.Errorf("release-removed: capture reported unsupported instead of the pawn unavailable: %v", state)
	}
	unavailable, _ := na.AsMap(state["unavailable"])
	if na.AsString(unavailable["detail"]) == "" {
		return fmt.Errorf("release-removed: no unavailable detail: %v", state)
	}
	report["case_removed_pawn"] = map[string]any{"unavailable": unavailable, "others_publishing": true}
	// The other feeds are unaffected.
	for _, f := range pawnFeeds[1:] {
		from, err := current(f)
		if err != nil {
			return err
		}
		time.Sleep(400 * time.Millisecond)
		if _, ok, err := f.reader.Read(from); err != nil || !ok {
			return fmt.Errorf("remove: %s stopped too (%v)", f.label, err)
		}
	}

	// Case: a native load replaces the map. Map-bound feeds end; the screen
	// continues; the same pawn re-leases on the new map under a new id.
	closeAll(all)
	if _, err := h.Call(ctx, "pause-for-save", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	saveName := fmt.Sprintf("videofeedsmatrix-%d", time.Now().UnixNano())
	saveReply, err := h.Wire(ctx, "save", "lifecycle_save", map[string]any{
		"player":   map[string]any{"identity": identity, "playerDirection": 1, "requestId": saveName + "-save"},
		"saveName": saveName,
	})
	if err != nil {
		return fmt.Errorf("save: %w", err)
	}
	if _, _, err := na.Outcome(saveReply, "completed"); err != nil {
		return fmt.Errorf("save: expected a completed save: %w", err)
	}
	for _, f := range append(pawnFeeds[1:], mapFeed, screenFeed) {
		if _, err := leaseActive("pre-load-renew-"+f.label, f.spec, 15); err != nil {
			return err
		}
	}
	loadRequestID := saveName + "-load"
	loadReply, err := h.Wire(ctx, "load-start", "lifecycle_load", map[string]any{
		"requestId": loadRequestID, "saveName": saveName, "readiness": "READINESS_VISUAL",
		"expectedPlayer":  map[string]any{"identity": identity, "playerDirection": 1, "requestId": loadRequestID},
		"playerDirection": 1,
	})
	if err != nil {
		return fmt.Errorf("load-start: %w", err)
	}
	if _, err := pollLoad(ctx, h, loadReply, loadRequestID, "load-poll"); err != nil {
		return err
	}
	afterIdentity, err := na.ReadIdentity(ctx, h, "identity-after-load")
	if err != nil {
		return err
	}
	viewer["identity"] = afterIdentity
	if _, err := h.Call(ctx, "unpause-after-load", "rimworld/set_time_speed", map[string]any{"speed": "Normal", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	time.Sleep(500 * time.Millisecond)
	ended := map[string]any{}
	for _, f := range append(pawnFeeds[1:], mapFeed) {
		reply, err := h.Wire(ctx, "read-frame-after-load-"+f.label, "presentation_read_frame", map[string]any{"viewer": viewer, "sourceId": f.source})
		if err != nil {
			return err
		}
		code, ok := na.FailureCode(reply)
		if !ok || code == "" {
			return fmt.Errorf("after load: %s (map-bound) still answers ReadFrame: %v", f.label, reply)
		}
		ended[f.label] = code
	}
	report["case_load_ended_sources"] = ended
	// The pawn keeps its load id across save/load; the new lease is a new
	// source on the new map.
	reborn := &feed{label: "pawn1-after-load", spec: pawnFeeds[1].spec}
	if reborn.source, err = leaseActive("lease-after-load", reborn.spec, 15); err != nil {
		return err
	}
	if reborn.source == pawnFeeds[1].source {
		return fmt.Errorf("after load: the pawn feed re-used the old map's source id")
	}
	mapAfter := &feed{label: "map-after-load", spec: mapSpec}
	if mapAfter.source, err = leaseActive("lease-map-after-load", mapSpec, 15); err != nil {
		return err
	}
	if _, err := leaseActive("lease-screen-after-load", nil, 15); err != nil {
		return err
	}
	afterFeeds := []*feed{reborn, mapAfter}
	if err := open(afterFeeds); err != nil {
		return err
	}
	afterRates, err := sample(afterFeeds, 3*time.Second)
	if err != nil {
		return err
	}
	closeAll(afterFeeds)
	report["case_after_load_rates"] = afterRates
	if afterRates["pawn1-after-load"] < 6 || afterRates["map-after-load"] < 2 {
		return fmt.Errorf("after load: pawn %.1f fps, map %.1f fps", afterRates["pawn1-after-load"], afterRates["map-after-load"])
	}
	if _, err := savePNG(filepath.Join(output, "pawn1-after-load.png"), reborn.frames[len(reborn.frames)-1]); err != nil {
		return err
	}
	if err := stopAll("stop-all"); err != nil {
		return err
	}

	return cases.CheckStartupLog(s)
}

func current(f *feed) (uint64, error) {
	frame, ok, err := f.reader.Read(0)
	if err != nil || !ok {
		return 0, fmt.Errorf("%s: no current frame (%v)", f.label, err)
	}
	return frame.Sequence, nil
}

func pollLoad(ctx context.Context, h *na.Harness, first map[string]any, requestID, label string) (map[string]any, error) {
	reply := first
	for attempt := 0; attempt < 600; attempt++ {
		if _, completed, err := na.Outcome(reply, "completed"); err == nil {
			return completed, nil
		}
		if _, superseded, err := na.Outcome(reply, "superseded"); err == nil {
			return nil, fmt.Errorf("load was superseded: %v", superseded["detail"])
		}
		if code, ok := na.FailureCode(reply); ok {
			return nil, fmt.Errorf("load failed: %s", code)
		}
		if _, _, err := na.Outcome(reply, "pending"); err != nil {
			return nil, fmt.Errorf("unexpected load reply shape: %v", reply)
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(200 * time.Millisecond):
		}
		next, err := h.Wire(ctx, fmt.Sprintf("%s-%d", label, attempt), "lifecycle_read_load", map[string]any{"requestId": requestID})
		if err != nil {
			return nil, err
		}
		reply = next
	}
	return nil, fmt.Errorf("load did not complete within the polling budget")
}

// luminance of the bottom-up RGBA32 pixel at (x, y) with y counted from the top.
func luminance(frame videoshm.Frame, x, y int) float64 {
	i := ((frame.Height-1-y)*frame.Width + x) * 4
	return 0.299*float64(frame.Data[i]) + 0.587*float64(frame.Data[i+1]) + 0.114*float64(frame.Data[i+2])
}

// patchContrast is the mean absolute luminance deviation inside the centred
// size×size patch, where the followed pawn is drawn.
func patchContrast(frame videoshm.Frame, size int) float64 {
	x0, y0 := frame.Width/2-size/2, frame.Height/2-size/2
	var sum float64
	values := make([]float64, 0, size*size)
	for y := y0; y < y0+size; y++ {
		for x := x0; x < x0+size; x++ {
			l := luminance(frame, x, y)
			values = append(values, l)
			sum += l
		}
	}
	mean := sum / float64(len(values))
	var dev float64
	for _, l := range values {
		dev += math.Abs(l - mean)
	}
	return dev / float64(len(values))
}

func meanAbsDiff(a, b videoshm.Frame) float64 {
	var sum float64
	for y := 0; y < a.Height; y++ {
		for x := 0; x < a.Width; x++ {
			sum += math.Abs(luminance(a, x, y) - luminance(b, x, y))
		}
	}
	return sum / float64(a.Width*a.Height)
}

// savePNG writes a bottom-up RGBA32 frame as a PNG and returns the mean
// absolute deviation of its luminance, a cheap blank-frame detector.
func savePNG(path string, frame videoshm.Frame) (float64, error) {
	img := image.NewRGBA(image.Rect(0, 0, frame.Width, frame.Height))
	var sum float64
	lum := make([]float64, 0, frame.Width*frame.Height)
	for y := 0; y < frame.Height; y++ {
		row := frame.Data[(frame.Height-1-y)*frame.Width*4:]
		for x := 0; x < frame.Width; x++ {
			r, g, b := row[x*4], row[x*4+1], row[x*4+2]
			img.SetRGBA(x, y, color.RGBA{r, g, b, 255})
			l := 0.299*float64(r) + 0.587*float64(g) + 0.114*float64(b)
			lum = append(lum, l)
			sum += l
		}
	}
	mean := sum / float64(len(lum))
	var dev float64
	for _, l := range lum {
		dev += math.Abs(l - mean)
	}
	file, err := os.Create(path)
	if err != nil {
		return 0, err
	}
	defer file.Close()
	if err := png.Encode(file, img); err != nil {
		return 0, err
	}
	return dev / float64(len(lum)), nil
}

func runSourceSpike(ctx context.Context, s cases.Session) error {
	const frames, feeds = 90, 4 // frames measured per cost configuration; pawn feeds rendered per frame
	report, h, output := s.Report(), s.Harness(), s.Config().Output
	if !na.Contains(s.Names(), "test/video_source_spike") {
		return fmt.Errorf("missing test/video_source_spike in discovery; rebuild the native mod with -Fixture VideoSourceFixture")
	}
	speed := func(label, speed string) error {
		_, err := h.Call(ctx, label, "rimworld/set_time_speed", map[string]any{"speed": speed, "ultraSpeedBoost": false})
		return err
	}

	// Single frames: the same colonist with the main camera near and far, so
	// zoom-coupled LOD in the submitted meshes shows up as a difference.
	for _, zoom := range []string{"near", "far"} {
		result, err := h.Call(ctx, "pawn-"+zoom, "test/video_source_spike", map[string]any{"mode": "pawn", "mainZoom": zoom, "width": 320, "height": 200})
		if err != nil {
			return fmt.Errorf("pawn-%s: %w", zoom, err)
		}
		if err := saveFixturePNG(output, "pawn-"+zoom+".png", result); err != nil {
			return err
		}
		report["pawn_"+zoom] = withoutPNG(result)
	}
	result, err := h.Call(ctx, "map", "test/video_source_spike", map[string]any{"mode": "map", "mainZoom": "near", "height": 1000})
	if err != nil {
		return fmt.Errorf("map: %w", err)
	}
	if err := saveFixturePNG(output, "map.png", result); err != nil {
		return err
	}
	report["map"] = withoutPNG(result)

	// Cost while paused isolates draw submission and rendering; at Normal
	// speed the same work competes with ticking.
	costPaused, err := h.Call(ctx, "cost-paused", "test/video_source_spike", map[string]any{"mode": "cost", "mainZoom": "near", "frames": frames, "feeds": feeds, "width": 320, "height": 200})
	if err != nil {
		return fmt.Errorf("cost-paused: %w", err)
	}
	report["cost_paused"] = costPaused
	if err := speed("normal", "Normal"); err != nil {
		return err
	}
	costNormal, err := h.Call(ctx, "cost-normal", "test/video_source_spike", map[string]any{"mode": "cost", "mainZoom": "near", "frames": frames, "feeds": feeds, "width": 320, "height": 200})
	if err != nil {
		return fmt.Errorf("cost-normal: %w", err)
	}
	report["cost_normal"] = costNormal
	if err := speed("pause-after", "Paused"); err != nil {
		return err
	}
	return cases.CheckStartupLog(s)
}

func saveFixturePNG(output, name string, result map[string]any) error {
	data, err := base64.StdEncoding.DecodeString(na.AsString(result["pngBase64"]))
	if err != nil || len(data) == 0 {
		return fmt.Errorf("%s: missing or invalid pngBase64: %v", name, err)
	}
	return os.WriteFile(filepath.Join(output, name), data, 0644)
}

func withoutPNG(result map[string]any) map[string]any {
	trimmed := make(map[string]any, len(result))
	for k, v := range result {
		if k != "pngBase64" {
			trimmed[k] = v
		}
	}
	return trimmed
}
