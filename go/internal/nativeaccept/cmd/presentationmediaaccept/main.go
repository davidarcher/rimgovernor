// Command presentationmediaaccept is the native-acceptance run for G01.09's
// PresentationReads.RenderState, PresentationMedia.DemandRendering and
// PresentationMedia.CapturePawn slice. It owns the full disposable-worker
// lifecycle (prepare a windowed/rendered profile, launch GABS, start a fresh
// debug game, exercise the three new proto tools against the real native mod,
// stop) and requires an actual graphical (non-headless) run: CapturePawn and
// DemandRendering both need Find.Camera != null, which batch mode never has.
//
// LeaseVideo/ReadFrame/AcknowledgeFrame, CaptureScreenshot and the whole
// PlayerPresentation service (camera/input ownership) are out of scope for
// this slice and are not exercised here.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-presentation-media-acceptance)")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 600*time.Second, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-presentation-media-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	report := na.NewReport("RenderState zero-side-effect read, DemandRendering lease reflected by a subsequent RenderState, CapturePawn portrait and follow against a real spawned colonist, and an unknown-pawn-id typed failure. LeaseVideo/ReadFrame/AcknowledgeFrame, CaptureScreenshot and the whole PlayerPresentation service are out of scope and not exercised.", false)
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
	// This slice requires actual rendering (Find.Camera != null); a headless
	// batch-mode run can never exercise DemandRendering/CapturePawn's success
	// paths, only their batch-mode-refusal paths.
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
	for _, tool := range []string{"rimgovernor/presentation_render_state", "rimgovernor/presentation_render_demand", "rimgovernor/presentation_capture_pawn"} {
		if !na.Contains(names, tool) {
			return fmt.Errorf("missing %s in discovery", tool)
		}
	}
	if _, err := h.Call(ctx, "new-game", "rimworld/start_debug_game_ready", map[string]any{
		"readiness": "visual", "pauseIfNeeded": true, "timeoutMs": 120000,
	}); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	identityBefore, err := h.Wire(ctx, "identity-before", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, loaded, err := na.Outcome(identityBefore, "loaded")
	if err != nil {
		return err
	}
	loadedContext, _ := na.AsMap(loaded["context"])
	identity, _ := na.AsMap(loadedContext["identity"])

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

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	if err := na.CheckStartupLog(string(logData), false); err != nil {
		return err
	}
	return nil
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
