// The presentation/media case is the native-acceptance run for G01.09's
// PresentationReads.RenderState, PresentationMedia.DemandRendering and
// PresentationMedia.CapturePawn slice against the real native mod. It
// requires an actual graphical run (`acceptance run presentation/media
// -headless=false`): CapturePawn and DemandRendering both need
// Find.Camera != null, which batch mode never has.
//
// LeaseVideo/ReadFrame/AcknowledgeFrame, CaptureScreenshot and the whole
// PlayerPresentation service (camera/input ownership) are out of scope for
// this slice and are not exercised here.
package presentation

import (
	"context"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name:   "presentation/media",
		Scope:  "RenderState zero-side-effect read, DemandRendering lease reflected by a subsequent RenderState, CapturePawn portrait and follow against a real spawned colonist, an unknown-pawn-id typed failure, and presentation state across a native load: a long-hold Research watch and a capture in flight when the colony is replaced must not strand the new game (the watch closes as nothing, the capture fails with a typed reason) and both work again against the loaded colony. LeaseVideo/ReadFrame/AcknowledgeFrame, CaptureScreenshot and the whole PlayerPresentation service are out of scope and not exercised.",
		Start:  cases.DebugStart{},
		Quiet:  na.QuietIfAvailable,
		Reason: "a rendered presentation read that also runs on a production build, which carries no quiet fixture",
		Budget: 5 * time.Minute,
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	// This slice requires actual rendering (Find.Camera != null); a headless
	// batch-mode run can never exercise DemandRendering/CapturePawn's success
	// paths, only their batch-mode-refusal paths.
	if s.Config().Headless {
		return fmt.Errorf("presentation/media needs a rendered game: run with -headless=false")
	}
	h, identity := s.Harness(), s.Identity()
	for _, tool := range []string{"rimgovernor/presentation_render_state", "rimgovernor/presentation_render_demand", "rimgovernor/presentation_capture_pawn"} {
		if !na.Contains(s.Names(), tool) {
			return fmt.Errorf("missing %s in discovery", tool)
		}
	}

	// Case 1: RenderState is a zero-side-effect observation before any demand
	// has been made. It must succeed (not fail) against a rendered game.
	stateBefore, err := h.Wire(ctx, "render-state-before", "presentation_render_state", map[string]any{"identity": identity})
	if err != nil {
		return fmt.Errorf("render-state-before: %w", err)
	}
	_, statusBefore, err := na.Outcome(stateBefore, "status")
	if err != nil {
		return fmt.Errorf("render-state-before: expected a status outcome: %w", err)
	}
	if supported, _ := na.AsBool(statusBefore["supported"]); !supported {
		return fmt.Errorf("render-state-before: rendered game reported supported=false")
	}
	report["case_render_state_before"] = statusBefore

	// Case 2: DemandRendering with a five-second lease. The returned status
	// must be supported with a positive remaining lease.
	demandReply, err := h.Wire(ctx, "render-demand", "presentation_render_demand", map[string]any{
		"viewer":       map[string]any{"identity": identity, "playerDirection": 1, "viewerId": "presentationmediaaccept"},
		"leaseSeconds": 5,
	})
	if err != nil {
		return fmt.Errorf("render-demand: %w", err)
	}
	_, demanded, err := na.Outcome(demandReply, "status")
	if err != nil {
		return fmt.Errorf("render-demand: expected a status outcome: %w", err)
	}
	if supported, _ := na.AsBool(demanded["supported"]); !supported {
		return fmt.Errorf("render-demand: rendered game reported supported=false")
	}
	remaining := na.AsNumber(demanded["remainingLeaseMs"])
	if remaining <= 0 || remaining > 5000 {
		return fmt.Errorf("render-demand: unexpected remainingLeaseMs %v", demanded["remainingLeaseMs"])
	}
	report["case_render_demand"] = demanded

	// Case 3: a subsequent RenderState observes the lease DemandRendering just
	// installed, without extending or shortening it itself.
	stateAfter, err := h.Wire(ctx, "render-state-after", "presentation_render_state", map[string]any{"identity": identity})
	if err != nil {
		return fmt.Errorf("render-state-after: %w", err)
	}
	_, statusAfter, err := na.Outcome(stateAfter, "status")
	if err != nil {
		return fmt.Errorf("render-state-after: expected a status outcome: %w", err)
	}
	if supported, _ := na.AsBool(statusAfter["supported"]); !supported {
		return fmt.Errorf("render-state-after: rendered game reported supported=false")
	}
	afterRemaining := na.AsNumber(statusAfter["remainingLeaseMs"])
	if afterRemaining <= 0 || afterRemaining > remaining {
		return fmt.Errorf("render-state-after: remainingLeaseMs %v did not reflect the render-demand lease %v", statusAfter["remainingLeaseMs"], remaining)
	}
	report["case_render_state_after"] = statusAfter

	// A real spawned colonist is required for CapturePawn; the debug game
	// starts with a colony, so the roster must be non-empty.
	colonistsReply, err := h.Wire(ctx, "colonists", "presentation_colonists", map[string]any{"identity": identity})
	if err != nil {
		return fmt.Errorf("colonists: %w", err)
	}
	_, roster, err := na.Outcome(colonistsReply, "roster")
	if err != nil {
		return fmt.Errorf("colonists: expected a roster outcome: %w", err)
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

	// Case 4: CapturePawn portrait against the real spawned colonist.
	portraitReply, err := h.Wire(ctx, "capture-portrait", "presentation_capture_pawn", map[string]any{
		"identity": identity, "pawnId": pawnID, "view": "PAWN_VIEW_PORTRAIT",
	})
	if err != nil {
		return fmt.Errorf("capture-portrait: %w", err)
	}
	_, portraitImage, err := na.Outcome(portraitReply, "image")
	if err != nil {
		return fmt.Errorf("capture-portrait: expected an image outcome: %w", err)
	}
	if err := checkFrame(portraitImage, pawnID, "PAWN_VIEW_PORTRAIT", 192, 192, "CAPTURE_METHOD_PORTRAIT"); err != nil {
		return fmt.Errorf("capture-portrait: %w", err)
	}
	report["case_capture_portrait"] = map[string]any{"pawnId": pawnID, "width": 192, "height": 192}

	// Case 5: CapturePawn follow against the same colonist.
	followReply, err := h.Wire(ctx, "capture-follow", "presentation_capture_pawn", map[string]any{
		"identity": identity, "pawnId": pawnID, "view": "PAWN_VIEW_FOLLOW",
	})
	if err != nil {
		return fmt.Errorf("capture-follow: %w", err)
	}
	_, followImage, err := na.Outcome(followReply, "image")
	if err != nil {
		return fmt.Errorf("capture-follow: expected an image outcome: %w", err)
	}
	if err := checkFrame(followImage, pawnID, "PAWN_VIEW_FOLLOW", 640, 400, "CAPTURE_METHOD_OFFSCREEN_FOLLOW"); err != nil {
		return fmt.Errorf("capture-follow: %w", err)
	}
	report["case_capture_follow"] = map[string]any{"pawnId": pawnID, "width": 640, "height": 400}

	// Case 6: an unknown pawn id must return a typed Failure, never a crash or
	// hang. Begin() succeeds synchronously (session/camera checks pass); the
	// async draw-cycle lookup then finds no such colonist and fails cleanly.
	unknownReply, err := h.Wire(ctx, "capture-unknown", "presentation_capture_pawn", map[string]any{
		"identity": identity, "pawnId": "presentationmediaaccept-unknown-pawn", "view": "PAWN_VIEW_PORTRAIT",
	})
	if err != nil {
		return fmt.Errorf("capture-unknown: %w", err)
	}
	if code, ok := na.FailureCode(unknownReply); !ok || code == "" {
		return fmt.Errorf("capture-unknown: expected a typed failure, got %v", unknownReply)
	} else {
		report["case_capture_unknown_failure_code"] = code
	}

	// Case 7: presentation state across a native load. A Research watch with a
	// 60-second hold is open, and a capture is started while the load is in
	// flight, when the colony is replaced. Neither may act on or block the new
	// game: the watch is abandoned (its scheduled close undoes nothing there),
	// the capture fails with a typed reason instead of holding the one capture
	// slot to its deadline, and a fresh watch and capture then succeed against
	// the loaded colony.
	if err := acrossLoad(ctx, h, identity, report); err != nil {
		return fmt.Errorf("across-load: %w", err)
	}

	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	if err := na.CheckStartupLog(string(logData), false); err != nil {
		return err
	}
	return nil
}

func acrossLoad(ctx context.Context, h *na.Harness, identity map[string]any, report na.Report) error {
	projects, err := h.Call(ctx, "research-read", "home/research", map[string]any{})
	if err != nil {
		return fmt.Errorf("research-read: %w", err)
	}
	available := na.AsSlice(projects["available"])
	if len(available) == 0 {
		return fmt.Errorf("research-read: no available project to select")
	}
	firstProject, _ := na.AsMap(available[0])
	project := na.AsString(firstProject["defName"])
	watchBefore, err := h.Call(ctx, "research-watch-before", "home/research", map[string]any{
		"set": project, "dryRun": false, "watch": true, "watchSeconds": 60,
	})
	if err != nil {
		return fmt.Errorf("research-watch-before: %w", err)
	}
	if applied, _ := na.AsBool(watchBefore["applied"]); !applied {
		return fmt.Errorf("research-watch-before: project %q was not applied: %v", project, watchBefore)
	}
	shownBefore, _ := na.AsMap(watchBefore["watch"])
	if shown, _ := na.AsBool(shownBefore["shown"]); !shown {
		return fmt.Errorf("research-watch-before: the Research watch showed nothing: %v", shownBefore)
	}
	report["case_across_load_watch_before"] = shownBefore

	saveName := fmt.Sprintf("presentationmediaaccept-%d", time.Now().UnixNano())
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

	pawnID := na.AsString(report["pawn_id"])
	loadRequestID := saveName + "-load"
	loadReply, err := h.Wire(ctx, "load-start", "lifecycle_load", map[string]any{
		"requestId":       loadRequestID,
		"saveName":        saveName,
		"readiness":       "READINESS_VISUAL",
		"expectedPlayer":  map[string]any{"identity": identity, "playerDirection": 1, "requestId": loadRequestID},
		"playerDirection": 1,
	})
	if err != nil {
		return fmt.Errorf("load-start: %w", err)
	}
	// The capture races the load on purpose. Whichever side of the colony
	// swap Begin lands on, the reply must be a typed failure, never an image
	// of the old colony and never a hang past the capture deadline.
	type captured struct {
		reply   map[string]any
		elapsed time.Duration
		err     error
	}
	inFlight := make(chan captured, 1)
	go func() {
		started := time.Now()
		reply, err := h.Wire(ctx, "capture-during-load", "presentation_capture_pawn", map[string]any{
			"identity": identity, "pawnId": pawnID, "view": "PAWN_VIEW_PORTRAIT",
		})
		inFlight <- captured{reply: reply, elapsed: time.Since(started), err: err}
	}()
	completed, err := pollLoad(ctx, h, loadReply, loadRequestID, "load-poll")
	if err != nil {
		return err
	}
	loadedIdentity, _ := na.AsMap(completed["loaded"])
	loadedContext, _ := na.AsMap(loadedIdentity["context"])
	afterIdentity, _ := na.AsMap(loadedContext["identity"])
	if na.AsString(afterIdentity["loadToken"]) == "" || na.AsString(afterIdentity["loadToken"]) == na.AsString(identity["loadToken"]) {
		return fmt.Errorf("load: loaded map did not receive a fresh load token")
	}
	during := <-inFlight
	if during.err != nil {
		return fmt.Errorf("capture-during-load: %w", during.err)
	}
	code, failed := na.FailureCode(during.reply)
	if !failed {
		return fmt.Errorf("capture-during-load: expected a typed failure, got %v", during.reply)
	}
	if during.elapsed >= 6*time.Second {
		return fmt.Errorf("capture-during-load: took %s, past the capture deadline", during.elapsed)
	}
	failure, _ := na.AsMap(during.reply["failure"])
	report["case_across_load_capture_during"] = map[string]any{"code": code, "detail": failure["detail"], "elapsedMs": during.elapsed.Milliseconds()}

	// The new colony: a capture must not find the slot busy, and a fresh
	// watch must open on the new game's own UI.
	afterReply, err := h.Wire(ctx, "capture-after-load", "presentation_capture_pawn", map[string]any{
		"identity": afterIdentity, "pawnId": pawnID, "view": "PAWN_VIEW_PORTRAIT",
	})
	if err != nil {
		return fmt.Errorf("capture-after-load: %w", err)
	}
	_, afterImage, err := na.Outcome(afterReply, "image")
	if err != nil {
		return fmt.Errorf("capture-after-load: expected an image outcome: %w", err)
	}
	if err := checkFrame(afterImage, pawnID, "PAWN_VIEW_PORTRAIT", 192, 192, "CAPTURE_METHOD_PORTRAIT"); err != nil {
		return fmt.Errorf("capture-after-load: %w", err)
	}
	watchAfter, err := h.Call(ctx, "research-watch-after", "home/research", map[string]any{
		"set": project, "dryRun": false, "watch": true, "watchSeconds": 1,
	})
	if err != nil {
		return fmt.Errorf("research-watch-after: %w", err)
	}
	shownAfter, _ := na.AsMap(watchAfter["watch"])
	if shown, _ := na.AsBool(shownAfter["shown"]); !shown {
		return fmt.Errorf("research-watch-after: the Research watch showed nothing on the loaded colony: %v", shownAfter)
	}
	report["case_across_load_watch_after"] = shownAfter
	return nil
}

func pollLoad(ctx context.Context, h *na.Harness, first map[string]any, requestID, label string) (map[string]any, error) {
	reply := first
	for attempt := 0; attempt < 300; attempt++ {
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

func checkFrame(image map[string]any, wantPawnID, wantView string, wantWidth, wantHeight float64, wantMethod string) error {
	if na.AsString(image["pawnId"]) != wantPawnID {
		return fmt.Errorf("pawn id mismatch: %v", image["pawnId"])
	}
	if na.AsString(image["view"]) != wantView {
		return fmt.Errorf("view mismatch: %v", image["view"])
	}
	frame, ok := na.AsMap(image["frame"])
	if !ok {
		return fmt.Errorf("missing frame")
	}
	if na.AsNumber(frame["width"]) != wantWidth || na.AsNumber(frame["height"]) != wantHeight {
		return fmt.Errorf("unexpected dimensions %vx%v", frame["width"], frame["height"])
	}
	if na.AsString(frame["encoding"]) != "MEDIA_ENCODING_PNG" {
		return fmt.Errorf("unexpected encoding %v", frame["encoding"])
	}
	if na.AsString(frame["captureMethod"]) != wantMethod {
		return fmt.Errorf("unexpected capture method %v", frame["captureMethod"])
	}
	if na.AsNumber(frame["capturedUnixMs"]) <= 0 {
		return fmt.Errorf("invalid capturedUnixMs %v", frame["capturedUnixMs"])
	}
	data := na.AsString(frame["data"])
	if data == "" {
		return fmt.Errorf("empty frame data")
	}
	return nil
}
