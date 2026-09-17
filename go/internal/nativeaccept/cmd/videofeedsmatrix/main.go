// Command videofeedsmatrix is the issue #21 stage D acceptance matrix for
// live feeds against a rendered game with the VideoMatrixFixture build:
// colonists spanning body types, apparel and weapon kinds each render in
// their own feed; several pawn feeds, the whole map and the screen publish
// concurrently; a removed pawn ends its feed and cannot be re-leased; a
// native load ends the map-bound feeds and the same pawn re-leases on the
// new map; and the frame-time and tick cost of each configuration is
// measured against a no-feed baseline.
package main

import (
	"context"
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
	output := flag.String("output", "", "fresh output directory (default <root>/native-video-matrix)")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 15*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-video-matrix"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	report := na.NewReport("Live feed matrix: varied colonists each render in their feed, feeds + map + screen publish concurrently, a removed pawn ends its feed and is refused on re-lease, a native load ends map-bound feeds and the pawn re-leases on the new map, and per-configuration frame-time and tick cost is measured.", false)
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
	for _, tool := range []string{"rimgovernor/presentation_lease_video", "rimgovernor/presentation_read_frame", "test/video_matrix_fixture"} {
		if !na.Contains(names, tool) {
			return fmt.Errorf("missing %s in discovery (build with -Fixture VideoMatrixFixture)", tool)
		}
	}
	if _, err := h.Call(ctx, "new-game", "rimworld/start_debug_game_ready", map[string]any{
		"readiness": "visual", "pauseIfNeeded": true, "timeoutMs": 120000,
	}); err != nil {
		return err
	}
	identity, err := readIdentity(ctx, h, "identity")
	if err != nil {
		return err
	}
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
	afterIdentity, err := readIdentity(ctx, h, "identity-after-load")
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

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), false)
}

func current(f *feed) (uint64, error) {
	frame, ok, err := f.reader.Read(0)
	if err != nil || !ok {
		return 0, fmt.Errorf("%s: no current frame (%v)", f.label, err)
	}
	return frame.Sequence, nil
}

func readIdentity(ctx context.Context, h *na.Harness, label string) (map[string]any, error) {
	reply, err := h.Wire(ctx, label, "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return nil, err
	}
	_, loaded, err := na.Outcome(reply, "loaded")
	if err != nil {
		return nil, fmt.Errorf("%s: %w", label, err)
	}
	loadedContext, _ := na.AsMap(loaded["context"])
	identity, _ := na.AsMap(loadedContext["identity"])
	if identity == nil {
		return nil, fmt.Errorf("%s: no identity in %v", label, loaded)
	}
	return identity, nil
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
