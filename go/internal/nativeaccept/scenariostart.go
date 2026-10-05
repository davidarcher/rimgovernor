package nativeaccept

import (
	"context"
	"fmt"
	"strings"
	"time"
)

// Storytellers a scenario start asks for: the production RimGovernorQuiet def
// (no incident comps) unless the run's QuietMode is Loud.
const (
	QuietStorytellerDef = "RimGovernorQuiet"
	LoudStorytellerDef  = "Cassandra"
)

// ScenarioStart starts a programmatic scenario from the main menu through the
// production new-colony op (rimgovernor/lifecycle_new_colony): world and
// colony generation under the built-in team policy, the clock paused, the
// naming dialog confirmed and the colony saved as SaveName in the profile's
// Saves. It is how a save variant is generated (variantgen) rather than
// hand-played. The seed is required and pins the world; the team policy's
// rerolls make a colony differ from one a different build generated.
type ScenarioStart struct {
	Scenario string
	Count    int
	Seed     string
	// Biome is one BiomeDef name or a comma-separated preference list; the
	// start settles the first the generated planet offers.
	Biome string
	// Flat settles a flat tile without rivers, roads or tile mutators when
	// the planet offers one.
	Flat           bool
	Difficulty     string
	MinTemperature float64
	MaxTemperature float64
	// WorldTemperature is a RimWorld OverallTemperature name; empty leaves native's default.
	WorldTemperature string
	Size             DebugStart
	// SaveName is the save the op writes; empty is DefaultScenarioSaveName.
	SaveName string
	// Timeout bounds the whole start (timeoutMs); zero is NewColonyTimeout.
	Timeout time.Duration
}

// DefaultScenarioSaveName is the save a ScenarioStart without a SaveName leaves behind.
const DefaultScenarioSaveName = "RimGovernor-scenario-start"

func (ScenarioStart) saves() []string      { return nil }
func (ScenarioStart) fixtureOps() []string { return nil }
func (v ScenarioStart) world() worldSource { return worldSource{seed: v.Seed, pinned: true} }

// Spec is the op's wire spec for v. quiet picks the storyteller. A temperature
// band of 0..0 or the unconstrained -100..100 is left out.
func (v ScenarioStart) Spec(quiet bool) (map[string]any, error) {
	if v.Seed == "" {
		return nil, fmt.Errorf("scenario start needs a pinned seed")
	}
	size := v.Size.withDefaults()
	difficulty := v.Difficulty
	if difficulty == "" {
		difficulty = "Rough"
	}
	storyteller := LoudStorytellerDef
	if quiet {
		storyteller = QuietStorytellerDef
	}
	saveName := v.SaveName
	if saveName == "" {
		saveName = DefaultScenarioSaveName
	}
	spec := map[string]any{
		"scenario": v.Scenario, "colonistCount": v.Count, "seed": v.Seed,
		"difficulty": difficulty, "storyteller": storyteller,
		"mapSize": size.MapSize, "planetCoverage": size.PlanetCoverage, "saveName": saveName,
	}
	if biomes := splitBiomes(v.Biome); len(biomes) > 0 {
		spec["biomes"] = biomes
	}
	if v.Flat {
		spec["flatTile"] = true
	}
	if v.WorldTemperature != "" {
		spec["worldTemperature"] = v.WorldTemperature
	}
	constrained := (v.MinTemperature != 0 || v.MaxTemperature != 0) && (v.MinTemperature != -100 || v.MaxTemperature != 100)
	if constrained {
		spec["minTemperature"], spec["maxTemperature"] = v.MinTemperature, v.MaxTemperature
	}
	return spec, nil
}

func (v ScenarioStart) load(ctx context.Context, s *Session, quiet QuietMode) (map[string]any, error) {
	run, err := v.Run(ctx, s.Harness, quiet != Loud)
	if err != nil {
		return nil, err
	}
	s.Report["quiet"] = quiet != Loud
	s.Report["new_colony"] = map[string]any{"phases": run.Phases, "rerolls": run.Rerolls, "seed": run.Completed["seed"], "saveName": run.Completed["saveName"]}
	return map[string]any{"kind": "scenario", "scenario": v.Scenario, "seed": v.Seed, "count": v.Count, "biome": v.Biome,
		"mapSize": v.Size.MapSize, "planetCoverage": v.Size.PlanetCoverage}, nil
}

// Run starts v through the op on h's main-menu game and waits for the
// completed colony (paused, saved). The caller owns resuming the clock.
func (v ScenarioStart) Run(ctx context.Context, h *Harness, quiet bool) (NewColonyRun, error) {
	spec, err := v.Spec(quiet)
	if err != nil {
		return NewColonyRun{}, err
	}
	request := NewColonyRequest(fmt.Sprintf("scenario-start-%d", time.Now().UnixNano()), spec, v.Timeout)
	run, err := RunNewColony(ctx, h, request, "new-colony")
	if err != nil {
		return run, fmt.Errorf("scenario start %s seed %s: %w", v.Scenario, v.Seed, err)
	}
	return run, nil
}

// splitBiomes is a comma-separated biome preference list as the wire's biomes
// array, in order, without blanks; nil when none is named.
func splitBiomes(list string) []any {
	var out []any
	for _, name := range strings.Split(list, ",") {
		if name = strings.TrimSpace(name); name != "" {
			out = append(out, name)
		}
	}
	return out
}
