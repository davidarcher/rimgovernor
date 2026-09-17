// Command variantsavegen drives issue #1's "larger sustained matrix" fixture
// problem from the other end: instead of hand-playing RimWorld to produce
// each variant .rws save (more seeds, scarce wood, low fertility/temperature,
// season-crossing starts), it drives the game's own programmatic scenario
// start and saves the result under the requested name -- see
// go/internal/nativeaccept/variantgen for the actual mechanics, shared with
// sustainedmatrixaccept's own -manifest mode.
//
// Requires a ScenarioStartFixture-built native mod installed in the
// configured profile's Mods folder (scripts/build_native_mod.ps1
// -Fixture ScenarioStartFixture ...; see docs/players/setup.md and
// go/README.md's other ScenarioStartFixture-dependent variants). The
// production mod does not expose test/configure_start or
// test/list_start_scenarios; this tool fails fast if they're missing from
// discovery rather than silently falling back to an ordinary debug start.
//
// -manifest takes a JSON array of variant specs for batch generation (see
// variantgen.Variant); -save/-scenario/-seed/... flags generate exactly one
// variant. Every produced save still needs to be reviewed before trusting it
// as a matrix fixture: this tool only confirms the save completed against
// the identity/tick it just generated, not that the generated map actually
// matches the intended stressor (e.g. that a "scarce wood" biome pick really
// has low tree density) -- inspect colony-facts or open the save normally to
// confirm before wiring it into sustainedmatrixaccept.
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/variantgen"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-variant-savegen)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 20*time.Minute, "overall run timeout (must cover every variant's world/colony generation and save)")
	startTimeout := flag.Duration("start-timeout", 180*time.Second, "rimworld/start_debug_game_ready timeout per variant (world/colony generation is slower than the plain debug scenario)")

	manifest := flag.String("manifest", "", "path to a JSON array of variant specs (batch mode; see variantgen.Variant)")

	save := flag.String("save", "", "single-variant mode: save name to produce")
	scenario := flag.String("scenario", "", "single-variant mode: native ScenarioDef name (discover with -list-scenarios)")
	count := flag.Int("count", 8, "single-variant mode: starting pawn count, 1..10")
	seed := flag.String("seed", "", "single-variant mode: world generation seed")
	biome := flag.String("biome", "", "single-variant mode: optional native BiomeDef restricting the settlement tile")
	difficulty := flag.String("difficulty", "Rough", "single-variant mode: native DifficultyDef")
	minTemperature := flag.Float64("min-temperature", -100, "single-variant mode: minimum seasonal settlement temperature")
	maxTemperature := flag.Float64("max-temperature", 100, "single-variant mode: maximum seasonal settlement temperature")
	worldTemperature := flag.String("world-temperature", "Normal", "single-variant mode: native OverallTemperature world-gen setting")
	mapSize := flag.Int("map-size", na.DefaultMapSize, "single-variant mode: map edge in cells, 150..400")
	planetCoverage := flag.Float64("planet-coverage", na.DefaultPlanetCoverage, "single-variant mode: planet coverage 0.05..1")

	listScenarios := flag.Bool("list-scenarios", false, "print available ScenarioDef/DifficultyDef names (via test/list_start_scenarios) and exit without generating anything")
	skipExisting := flag.Bool("skip-existing", false, "skip a variant whose save already exists at profile/Saves/<save>.rws instead of regenerating it")
	flag.Parse()

	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-variant-savegen"
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

	var variants []variantgen.Variant
	switch {
	case *listScenarios:
		// No variants to run; runAll's list-only path below still opens one
		// session to read test/list_start_scenarios.
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
		if len(variants) == 0 {
			fmt.Fprintln(os.Stderr, "-manifest named no variants")
			os.Exit(2)
		}
		for i := range variants {
			variants[i] = variants[i].WithDefaults()
		}
	default:
		if *save == "" || *scenario == "" || *seed == "" {
			fmt.Fprintln(os.Stderr, "single-variant mode requires -save, -scenario and -seed (or use -manifest / -list-scenarios)")
			os.Exit(2)
		}
		variants = []variantgen.Variant{{
			Save: *save, Scenario: *scenario, Count: *count, Seed: *seed, Biome: *biome,
			Difficulty: *difficulty, MinTemperature: *minTemperature, MaxTemperature: *maxTemperature,
			WorldTemperature: *worldTemperature, MapSize: *mapSize, PlanetCoverage: *planetCoverage,
		}}
	}
	for _, v := range variants {
		if err := v.Validate(); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(2)
		}
	}

	report := na.NewReport(fmt.Sprintf("Native scenario-start save generation for issue #1's sustained matrix: "+
		"%d variant(s) via ScenarioStartFixture's test/configure_start + rimworld/start_debug_game_ready + "+
		"rimgovernor/lifecycle_save. Requires a ScenarioStartFixture-built mod installed in the configured "+
		"profile.", len(variants)), !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()

	err := runAll(ctx, *root, *output, *game, !*rendered, *startTimeout, *listScenarios, *skipExisting, variants, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func runAll(ctx context.Context, root, output, gameID string, headless bool, startTimeout time.Duration, listOnly, skipExisting bool, variants []variantgen.Variant, report na.Report) error {
	if listOnly {
		variantDir := filepath.Join(output, "list-scenarios")
		if err := os.MkdirAll(variantDir, 0755); err != nil {
			return err
		}
		listing, err := variantgen.ListScenarios(ctx, root, variantDir, gameID, headless)
		report["scenarios"] = listing
		return err
	}
	var results []map[string]any
	for i, v := range variants {
		row := map[string]any{"variant": v}
		if skipExisting && variantgen.Exists(root, v.Save) {
			row["skipped"] = "already exists at " + variantgen.SavePath(root, v.Save)
			results = append(results, row)
			fmt.Fprintf(os.Stderr, "[%d/%d] skipping %q, already generated\n", i+1, len(variants), v.Save)
			continue
		}
		variantDir := filepath.Join(output, fmt.Sprintf("%02d-%s", i+1, sanitize(v.Save)))
		if err := os.MkdirAll(variantDir, 0755); err != nil {
			return err
		}
		fmt.Fprintf(os.Stderr, "[%d/%d] generating variant save %q (scenario=%s seed=%s)\n", i+1, len(variants), v.Save, v.Scenario, v.Seed)
		err := variantgen.Generate(ctx, root, variantDir, gameID, headless, startTimeout, v, row)
		if err != nil {
			row["error"] = err.Error()
			results = append(results, row)
			report["variants"] = results
			return fmt.Errorf("variant %q: %w", v.Save, err)
		}
		row["ok"] = true
		results = append(results, row)
	}
	report["variants"] = results
	return nil
}

func sanitize(name string) string {
	var b []byte
	for _, r := range name {
		switch {
		case r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z', r >= '0' && r <= '9', r == '-', r == '_':
			b = append(b, byte(r))
		default:
			b = append(b, '-')
		}
	}
	return string(b)
}
