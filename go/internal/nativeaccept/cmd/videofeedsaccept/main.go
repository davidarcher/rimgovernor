// Command videofeedsaccept proves the multi-source native video driver (#21
// stage B) against a rendered game: a colonist feed and a whole-map feed
// leased beside the screen, each publishing to its own shared-memory buffer
// at its own cadence, readable by source through ReadFrame, and stoppable one
// at a time. It also saves one decoded frame per feed for inspection.
package main

import (
	"context"
	"encoding/base64"
	"flag"
	"fmt"
	"image"
	"image/color"
	"image/png"
	"math"
	"os"
	"path/filepath"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/videoshm"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-video-feeds)")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 10*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-video-feeds"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	report := na.NewReport("LeaseVideo with pawn and map sources beside the screen source: three concurrent buffers, per-source cadence and geometry read from shared memory, ReadFrame by source id, invalid-source typed failures, stopping one source leaves the others publishing, and stopping all is quiet.", false)
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

type feed struct {
	label  string
	source string
	spec   map[string]any
	state  map[string]any
	reader videoshm.Reader
	frames []videoshm.Frame
}

func run(ctx context.Context, root, output, gameID string, report na.Report) error {
	cfg := &na.Config{Root: root, Output: output, Headless: false, GameID: gameID}
	if err := cfg.PrepareConfig(); err != nil {
		return fmt.Errorf("prepare profile: %w", err)
	}
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
	for _, tool := range []string{"rimgovernor/presentation_lease_video", "rimgovernor/presentation_read_frame", "rimgovernor/presentation_colonists"} {
		if !na.Contains(names, tool) {
			return fmt.Errorf("missing %s in discovery", tool)
		}
	}
	if _, err := na.StartDebugGame(ctx, h, names, na.QuietIfAvailable); err != nil {
		return err
	}
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
	feeds := []*feed{
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
	latest := func(f *feed) (uint64, error) {
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
	for f, last := range map[*feed]uint64{feeds[0]: lastScreen, feeds[2]: lastMap} {
		if _, ok, err := f.reader.Read(last); err != nil {
			return err
		} else if ok {
			return fmt.Errorf("stop-all: %s kept publishing", f.label)
		}
	}
	report["case_stop_all_quiet"] = true

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), false)
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
