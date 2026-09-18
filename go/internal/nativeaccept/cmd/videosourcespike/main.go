// Command videosourcespike drives the disposable test/video_source_spike
// fixture (build_native_mod.ps1 -Fixture VideoSourceFixture) against a
// rendered game for issue #21 stage B: it saves a colonist feed rendered by a
// second camera at near and far main-camera zoom, a whole-map frame, and the
// per-frame cost of baseline / pawn feeds / whole-map culling while paused and
// at Normal speed. It is evidence gathering, not a pass/fail acceptance.
package main

import (
	"context"
	"encoding/base64"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-video-source-spike)")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	frames := flag.Int("frames", 90, "frames measured per cost configuration")
	feeds := flag.Int("feeds", 4, "pawn feeds rendered per frame in the cost run")
	timeout := flag.Duration("timeout", 10*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-video-source-spike"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	report := na.NewReport("Stage B spike evidence: second-camera colonist feed at near and far main zoom, whole-map frame, and per-frame draw cost for baseline, pawn feeds and whole-map culling while paused and at Normal speed.", false)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, *frames, *feeds, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID string, frames, feeds int, report na.Report) error {
	cfg := &na.Config{Root: root, Output: output, Headless: false, GameID: gameID}
	s, err := na.OpenSession(ctx, cfg, report, na.DebugStart{}, na.QuietRequired)
	if err != nil {
		return err
	}
	defer s.Close()
	h := s.Harness
	if !na.Contains(s.Names, "test/video_source_spike") {
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
		if err := savePNG(output, "pawn-"+zoom+".png", result); err != nil {
			return err
		}
		report["pawn_"+zoom] = withoutPNG(result)
	}
	result, err := h.Call(ctx, "map", "test/video_source_spike", map[string]any{"mode": "map", "mainZoom": "near", "height": 1000})
	if err != nil {
		return fmt.Errorf("map: %w", err)
	}
	if err := savePNG(output, "map.png", result); err != nil {
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
	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), false)
}

func savePNG(output, name string, result map[string]any) error {
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
