// Command sustainedmatrixaccept is issue #1's "larger sustained matrix":
// sustainedfoodaccept's single-save EnsureFoodSupply diagnostic, repeated
// across a variant list of saves (more seeds, eight-colonist starts, scarce
// wood, low fertility/temperature, longer survival, changing seasons) so a
// fix can be checked against more than one sampled stable window before
// being trusted. It is still a diagnostic, not a pass/fail acceptance gate:
// per the issue's own acceptance bar, "sampled stable gates over a bounded
// window do not establish arbitrary long-term colony survival" -- this tool
// widens the sample, it does not turn it into a proof.
//
// Variants run sequentially, one at a time: only one native session can be
// connected through the sole GABP slot at once (see routinehaulaccept's
// comment on that constraint), so a variant's service is fully stopped and
// its save's native session closed before the next variant's load begins.
// Each variant gets its own fresh output subdirectory
// (<output>/<variant-name>/) so its full report.json/timeline survives
// alongside the combined matrix report.
//
// Two ways to name the variant list:
//
//   - -saves takes a comma-separated list of save names that must already
//     exist (e.g. hand-prepared fixtures).
//   - -manifest takes a JSON array of variantgen.Variant specs (scenario,
//     seed, biome, colonist count, temperature range); any variant whose
//     save isn't already sitting at profile/Saves/<save>.rws is generated
//     on the spot via go/internal/nativeaccept/variantgen (the same
//     mechanics as cmd/variantsavegen) before its watch window runs. This
//     is the intended path for the matrix: the checked-in artifact is the
//     JSON spec, not a multi-megabyte .rws binary, and running the matrix
//     is what reproduces the fixture, and a save already there is the
//     pre-generated world (issue #91): generation is a one-off,
//     variantsavegen -manifest does it offline. -manifest mode needs a
//     ScenarioStartFixture-built mod installed (see variantgen's doc
//     comment); -saves mode does not. Missing saves are generated before
//     any variant runs, so -reuse-game works in both modes.
//
// manifests/issue-1-matrix.json is the checked-in variant list for issue
// #1's own acceptance-bar dimensions. Baseline is Crashlanded/3 pawns --
// RimWorld's own default new-game scenario and pawn count, not the
// hand-prepared tribal8 fixture -- with two more Crashlanded seeds for seed
// sensitivity, a scarce-wood Desert start, a cold Tundra start, a hot
// ExtremeDesert start, and a Hard-difficulty start. Colony size varies via
// its own natural scenario rather than an edited pawnCount on Crashlanded:
// TheRichExplorer for a one-pawn solo start, and LostTribe at its default
// 5 pawns and at the tribal8 fixture's 8 pawns. manifest_test.go guards the
// file against typos (unknown fields, out-of-range counts, empty seeds,
// duplicate save names) so a bad edit fails `go test`, not a spent native
// session.
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/variantgen"
)

const baselineSave = "RimGovernor-tribal8-baseline"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-sustained-matrix-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	rimgovernorBinary := flag.String("rimgovernor", "", "absolute path to a prebuilt rimgovernor binary (go build ./go/cmd/rimgovernor)")
	saves := flag.String("saves", "", "comma-separated save names that already exist, one variant per save (mutually exclusive with -manifest)")
	manifest := flag.String("manifest", "", "path to a JSON array of variantgen.Variant specs; missing saves are generated before their watch window runs (mutually exclusive with -saves)")
	regenerate := flag.Bool("regenerate", false, "in -manifest mode, regenerate every variant's save even if it already exists at profile/Saves/<save>.rws")
	watch := flag.Duration("watch", 20*time.Minute, "per-variant wall-clock ceiling on observing EnsureFoodSupply after authority is acquired; the whole window when -window is 0")
	window := flag.Uint64("window", 2500, "per-variant sample window in game ticks (2500 = one in-game hour, 60000 = one day): the watch ends once the live tick has advanced this far, or at -watch if the game stops advancing; 0 watches the full -watch wall-clock length (the diagnostic timeline); each variant's result.json records the window it observed under \"window\"")
	poll := flag.Duration("poll", 5*time.Second, "sampling interval during each variant's watch window")
	perVariantTimeout := flag.Duration("variant-timeout", 30*time.Minute, "per-variant run timeout (must exceed -watch plus startup/shutdown)")
	nativeTimeout := flag.Duration("native-timeout", 15*time.Second, "serve subprocess's own --timeout (native call budget per ClockScheduler.Step, shared across every chained routine planner in that step)")
	startTimeout := flag.Duration("start-timeout", 180*time.Second, "in -manifest mode, rimworld/start_debug_game_ready timeout per generated variant")
	stopOnError := flag.Bool("stop-on-error", false, "abort the remaining variants after the first harness error instead of continuing the matrix")
	families := flag.String("families", "", "serve's RIMGOVERNOR_ROUTINE_FAMILIES for every variant; empty composes EnsureFoodSupply's full pipeline, \"all\" serve's autonomous default; narrow it on a machine running peer headless games (issue #103)")
	stepStall := flag.Duration("step-stall", 90*time.Second, "per variant, fail fast unless a scheduler step has admitted a clock window this long after the watch starts (0 disables)")
	reuseGame := flag.Bool("reuse-game", false, "launch RimWorld once and reload each variant's save into the same process (issue #22); a variant failure retires the game and ends the matrix")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *rimgovernorBinary == "" {
		fmt.Fprintln(os.Stderr, "-rimgovernor is required (absolute path to a prebuilt rimgovernor binary)")
		os.Exit(2)
	}
	if !filepath.IsAbs(*rimgovernorBinary) {
		fmt.Fprintln(os.Stderr, "-rimgovernor must be an absolute path")
		os.Exit(2)
	}
	if *saves != "" && *manifest != "" {
		fmt.Fprintln(os.Stderr, "-saves and -manifest are mutually exclusive")
		os.Exit(2)
	}

	var variants []variantgen.Variant
	switch {
	case *manifest != "":
		data, err := os.ReadFile(*manifest)
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(2)
		}
		if err := json.Unmarshal(data, &variants); err != nil {
			fmt.Fprintf(os.Stderr, "-manifest must be a JSON array of variant specs: %v\n", err)
			os.Exit(2)
		}
		for i := range variants {
			variants[i] = variants[i].WithDefaults()
			if err := variants[i].Validate(); err != nil {
				fmt.Fprintln(os.Stderr, err)
				os.Exit(2)
			}
		}
	case *saves != "":
		for _, s := range strings.Split(*saves, ",") {
			if s = strings.TrimSpace(s); s != "" {
				variants = append(variants, variantgen.Variant{Save: s})
			}
		}
	default:
		variants = []variantgen.Variant{{Save: baselineSave}}
	}
	if len(variants) == 0 {
		fmt.Fprintln(os.Stderr, "-saves/-manifest must name at least one variant")
		os.Exit(2)
	}

	if *output == "" {
		*output = *root + "/native-sustained-matrix-acceptance"
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

	saveNames := make([]string, len(variants))
	for i, v := range variants {
		saveNames[i] = v.Save
	}
	report := na.NewReport(fmt.Sprintf("Diagnostic: EnsureFoodSupply sustained matrix across %d save variant(s) "+
		"(%s), evidence for issue #1's acceptance bar that a fix must hold across seeds/colony sizes/resource/"+
		"season variants, not just one sampled stable window. Not a pass/fail acceptance gate.",
		len(variants), strings.Join(saveNames, ", ")), !*rendered)
	report["variant_saves"] = saveNames
	report["manifest_mode"] = *manifest != ""
	report["window_ticks"] = *window
	report["watch"] = watch.String()

	// -manifest: every save the manifest names exists before any variant
	// runs. A save already at profile/Saves is the pre-generated world and is
	// loaded as-is; a missing one (or all of them, -regenerate) is generated
	// now, each in its own main-menu session (variantgen.Generate), before
	// the game the matrix runs in is opened.
	rowFor := map[string]map[string]any{}
	var rows []map[string]any
	allHarnessOK := true
	stoppedEarly := false
	for i, v := range variants {
		variantDir := filepath.Join(*output, sanitize(v.Save))
		if err := os.MkdirAll(variantDir, 0755); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(2)
		}
		row := map[string]any{"save": v.Save, "output": variantDir}
		rowFor[v.Save] = row
		if *manifest == "" {
			continue
		}
		row["variant_spec"] = v
		if !*regenerate && variantgen.Exists(*root, v.Save) {
			row["generated"] = "already existed at " + variantgen.SavePath(*root, v.Save)
			continue
		}
		fmt.Fprintf(os.Stderr, "[%d/%d] generating variant save %q (scenario=%s seed=%s)\n", i+1, len(variants), v.Save, v.Scenario, v.Seed)
		genDir := filepath.Join(variantDir, "generate")
		if err := os.MkdirAll(genDir, 0755); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(2)
		}
		genCtx, genCancel := context.WithTimeout(context.Background(), *startTimeout+5*time.Minute)
		genRow := map[string]any{}
		genErr := variantgen.Generate(genCtx, *root, genDir, *game, !*rendered, *startTimeout, v, genRow)
		genCancel()
		row["generated"] = genRow
		if genErr != nil {
			row["generate_error"] = genErr.Error()
			row["harness_ok"] = false
			allHarnessOK = false
			rows = append(rows, row)
			if *stopOnError {
				fmt.Fprintf(os.Stderr, "variant %q generation failed, stopping matrix early (-stop-on-error): %v\n", v.Save, genErr)
				stoppedEarly = true
				break
			}
		}
	}

	// -reuse-game: one launch for the whole matrix. Root/GameID/Headless are
	// the same for every variant, so one prepared Config serves them all;
	// each Run then loads its save as a case of this lifecycle.
	var reuse *na.GameReuse
	if *reuseGame && !stoppedEarly {
		reuseCfg := &na.Config{Root: *root, Output: *output, Headless: !*rendered, GameID: *game}
		if err := reuseCfg.PrepareConfig(); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(2)
		}
		gabsExecutable, err := na.GABSExecutable(*root, reuseCfg.Configuration)
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(2)
		}
		openCtx, openCancel := context.WithTimeout(context.Background(), 5*time.Minute)
		reuse, err = na.OpenReusableGame(openCtx, reuseCfg, gabsExecutable)
		openCancel()
		if err != nil {
			report["error"] = "open reusable game: " + err.Error()
			os.Exit(report.Finalize(*output))
		}
	}

	for i, v := range variants {
		if stoppedEarly {
			break
		}
		row := rowFor[v.Save]
		if _, failed := row["generate_error"]; failed {
			continue
		}
		variantDir := row["output"].(string)

		fmt.Fprintf(os.Stderr, "[%d/%d] running sustained-food variant %q -> %s\n", i+1, len(variants), v.Save, variantDir)
		variantReport := na.NewReport("variant: "+v.Save, !*rendered)
		ctx, cancel := context.WithTimeout(context.Background(), *perVariantTimeout)
		cfg := sustainedfood.RunConfig{
			Root: *root, Output: variantDir, GameID: *game, Headless: !*rendered,
			RimgovernorBinary: *rimgovernorBinary, Save: v.Save,
			Watch: *watch, Window: *window, Poll: *poll, NativeTimeout: *nativeTimeout,
			RequestPrefix: "sustained-matrix-" + sanitize(v.Save),
			Families:      *families, StepStall: *stepStall,
			Reuse: reuse,
		}
		timeline, err := sustainedfood.Run(ctx, cfg, variantReport)
		cancel()
		if retired, reason := reuseRetired(reuse); retired {
			// The shared game is gone; every remaining variant would fail
			// at load, so end the matrix here and say why.
			row["reuse_retired"] = reason
			if err == nil {
				err = fmt.Errorf("reusable game retired: %s", reason)
			}
		}

		row["harness_ok"] = err == nil
		if err != nil {
			row["error"] = err.Error()
			allHarnessOK = false
		}
		metrics := sustainedfood.DeriveMetrics(timeline)
		row["metrics"] = metrics
		row["window"] = variantReport["window"]
		variantReport["metrics"] = metrics
		if err == nil {
			variantReport["passed"] = true
		} else {
			variantReport["error"] = err.Error()
		}
		variantReport.Finalize(variantDir)
		rows = append(rows, row)

		if err != nil && *stopOnError {
			fmt.Fprintf(os.Stderr, "variant %q failed, stopping matrix early (-stop-on-error): %v\n", v.Save, err)
			break
		}
	}

	report["variants"] = rows
	report["variants_run"] = len(rows)
	report["variants_total"] = len(variants)
	// This tool never fails the whole matrix over a poor food outcome in a
	// given variant (that's exactly the diagnostic data it exists to
	// surface); it only fails when the harness itself couldn't complete a
	// run (generation, load/service/authority/log-check errors), the same
	// bar sustainedfoodaccept applies to a single run.
	report["passed"] = allHarnessOK && len(rows) == len(variants)
	if reuse != nil {
		retireCtx, retireCancel := context.WithTimeout(context.Background(), 2*time.Minute)
		_ = reuse.Retire(retireCtx, "matrix complete")
		retireCancel()
		reuse.Record(report)
	}
	os.Exit(report.Finalize(*output))
}

func reuseRetired(reuse *na.GameReuse) (bool, string) {
	if reuse == nil {
		return false, ""
	}
	return reuse.Retired()
}

func sanitize(name string) string {
	var b strings.Builder
	for _, r := range name {
		switch {
		case r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z', r >= '0' && r <= '9', r == '-', r == '_':
			b.WriteRune(r)
		default:
			b.WriteRune('-')
		}
	}
	return b.String()
}
