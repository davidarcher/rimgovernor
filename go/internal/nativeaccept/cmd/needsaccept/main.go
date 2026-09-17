// Command needsaccept proves test/freeze_needs (issue #92): with every need
// but Rest frozen, a tenth of a day at Superfast leaves each free colonist's
// frozen needs at maximum while Rest keeps moving; release lets them fall
// again.
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
	output := flag.String("output", "", "fresh output directory (default <root>/native-letter-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 15*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-letter-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	entries, _ := os.ReadDir(*output)
	if len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport("test/freeze_needs pins every free colonist need but the kept ones at maximum across "+
		"an advance window and releases them on request.", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, !*rendered, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID string, headless bool, report na.Report) error {
	cfg := &na.Config{Root: root, Output: output, Headless: headless, GameID: gameID}
	if err := cfg.PrepareConfig(); err != nil {
		return fmt.Errorf("prepare profile: %w", err)
	}
	held, err := na.OpenGame(ctx, cfg)
	if err != nil {
		return err
	}
	defer held.Close(report)
	client := held.Client
	h := na.NewHarness(client, output)
	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	if _, err := na.StartDebugGame(ctx, h, names, na.QuietRequired); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
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
	supervisor := &na.ScenarioClock{Wire: h.WireFunc(), Identity: identity, Owner: na.Controller, Report: report}
	if _, err := supervisor.Acquire(ctx, "acquire"); err != nil {
		return err
	}
	rt := &na.ScenarioRuntime{Query: h.Call, Clock: supervisor, Report: report, Tools: names}

	const kept = "Rest"
	frozen, err := na.FreezeNeeds(ctx, h, names, kept)
	if err != nil {
		return err
	}
	report["frozen"] = frozen
	levels := func(label string) (map[string]map[string]float64, error) {
		reply, err := h.Call(ctx, label, na.FreezeNeedsTool, map[string]any{"action": "inspect"})
		if err != nil {
			return nil, err
		}
		out := map[string]map[string]float64{}
		for _, raw := range na.AsSlice(reply["needs"]) {
			row, _ := na.AsMap(raw)
			pawn := na.AsString(row["pawn"])
			out[pawn] = map[string]float64{}
			for _, n := range na.AsSlice(row["needs"]) {
				need, _ := na.AsMap(n)
				out[pawn][na.AsString(need["def"])] = na.AsNumber(need["level"])
			}
		}
		if len(out) == 0 {
			return nil, fmt.Errorf("%s: no free colonists reported: %#v", label, reply)
		}
		return out, nil
	}
	before, err := levels("needs-before")
	if err != nil {
		return err
	}
	// A tenth of a day: Rest falls by a few hundredths awake, more than
	// enough to show it is live, while a frozen need would fall the same.
	if _, err := na.AdvanceGame(ctx, rt, 6000, na.WithTimeout(180*time.Second)); err != nil {
		return fmt.Errorf("frozen window: %w", err)
	}
	after, err := levels("needs-after")
	if err != nil {
		return err
	}
	summary := map[string]any{"kept": kept}
	report["needs"] = summary
	moved := 0
	for pawn, needs := range after {
		for def, level := range needs {
			if def == kept {
				if level != before[pawn][def] {
					moved++
				}
				continue
			}
			if level < 0.95 {
				return fmt.Errorf("frozen need %s on %s fell to %.3f", def, pawn, level)
			}
		}
	}
	if moved == 0 {
		return fmt.Errorf("kept need %s did not move on any colonist: %#v", kept, after)
	}
	summary["kept_moved_on"] = moved
	summary["after"] = after

	released, err := h.Call(ctx, "release", na.FreezeNeedsTool, map[string]any{"action": "release"})
	if err != nil {
		return err
	}
	if frozen, _ := na.AsBool(released["frozen"]); frozen {
		return fmt.Errorf("release left needs frozen: %#v", released)
	}
	if _, err := na.AdvanceGame(ctx, rt, 6000, na.WithTimeout(180*time.Second)); err != nil {
		return fmt.Errorf("released window: %w", err)
	}
	final, err := levels("needs-released")
	if err != nil {
		return err
	}
	fell := 0
	for pawn, needs := range final {
		for def, level := range needs {
			if def != kept && level < after[pawn][def] {
				fell++
			}
		}
	}
	if fell == 0 {
		return fmt.Errorf("no need fell after release: %#v", final)
	}
	summary["fell_after_release"] = fell
	return nil
}
