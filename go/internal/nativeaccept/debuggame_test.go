package nativeaccept

import "testing"

func TestDebugStartSpec(t *testing.T) {
	t.Setenv(MapSizeEnv, "")
	t.Setenv(PlanetCoverageEnv, "")
	spec, err := DefaultDebugStart().scenarioStart("abcdefghij", "RimGovernor-debug-200-0_05-quiet").Spec(true)
	if err != nil {
		t.Fatal(err)
	}
	want := map[string]any{
		"scenario": "Crashlanded", "colonistCount": 3, "seed": "abcdefghij", "difficulty": "Rough",
		"storyteller": QuietStorytellerDef, "mapSize": 200, "planetCoverage": 0.05, "saveName": "RimGovernor-debug-200-0_05-quiet",
	}
	if len(spec) != len(want) {
		t.Fatalf("spec %v, want %v", spec, want)
	}
	for k, v := range want {
		if spec[k] != v {
			t.Errorf("spec[%s] = %v, want %v", k, spec[k], v)
		}
	}
	// Loud keeps the ordinary storyteller; biomes keep their order and
	// blanks drop; flat is a wire flag.
	spec, err = (DebugStart{MapSize: 250, PlanetCoverage: 0.3, Biomes: "TemperateForest, ,TropicalRainforest", Flat: true}).scenarioStart("s", "n").Spec(false)
	if err != nil {
		t.Fatal(err)
	}
	biomes, _ := spec["biomes"].([]any)
	if spec["storyteller"] != LoudStorytellerDef || spec["flatTile"] != true || len(biomes) != 2 || biomes[0] != "TemperateForest" || biomes[1] != "TropicalRainforest" {
		t.Errorf("spec %v", spec)
	}
	if spec["mapSize"] != 250 || spec["planetCoverage"] != 0.3 {
		t.Errorf("size %v", spec)
	}
	// A plain start carries no biomes, flat flag or temperature band.
	spec, _ = (DebugStart{MapSize: 200, PlanetCoverage: 0.05}).scenarioStart("s", "n").Spec(true)
	for _, k := range []string{"biomes", "flatTile", "minTemperature", "maxTemperature", "worldTemperature"} {
		if _, has := spec[k]; has {
			t.Errorf("plain spec carries %s", k)
		}
	}
	// An empty seed is refused by the spec; generateDebugStart draws one first.
	if _, err := (DebugStart{MapSize: 200, PlanetCoverage: 0.05}).scenarioStart("", "n").Spec(true); err == nil {
		t.Error("empty seed accepted")
	}
}

func TestQuietDecision(t *testing.T) {
	with := []string{"rimgovernor/load_game_ready", QuietStorytellerTool}
	without := []string{"rimgovernor/load_game_ready"}
	cases := []struct {
		names []string
		mode  QuietMode
		apply bool
		err   bool
	}{
		{with, QuietRequired, true, false},
		{without, QuietRequired, false, true},
		{with, QuietIfAvailable, true, false},
		{without, QuietIfAvailable, false, false},
		{with, Loud, false, false},
		{without, Loud, false, false},
		{with, QuietMode(9), false, true},
	}
	for _, c := range cases {
		apply, err := quietDecision(c.names, c.mode)
		if apply != c.apply || (err != nil) != c.err {
			t.Errorf("quietDecision(%v, %s) = %v, %v; want %v, err=%v", c.names, c.mode, apply, err, c.apply, c.err)
		}
	}
}

func TestDebugStart(t *testing.T) {
	t.Setenv(MapSizeEnv, "")
	t.Setenv(PlanetCoverageEnv, "")
	if d := DefaultDebugStart(); d.MapSize != DefaultMapSize || d.PlanetCoverage != DefaultPlanetCoverage {
		t.Fatalf("default %+v", d)
	}
	t.Setenv(MapSizeEnv, "250")
	t.Setenv(PlanetCoverageEnv, "0.3")
	if d := DefaultDebugStart(); d.MapSize != 250 || d.PlanetCoverage != 0.3 {
		t.Fatalf("env %+v", d)
	}
	for _, bad := range []DebugStart{{MapSize: 99, PlanetCoverage: 0.05}, {MapSize: 500, PlanetCoverage: 0.05}, {MapSize: 200, PlanetCoverage: 0.01}, {MapSize: 200, PlanetCoverage: 2}} {
		if bad.Validate() == nil {
			t.Fatalf("%+v validated", bad)
		}
	}
	if err := (DebugStart{MapSize: 100, PlanetCoverage: 1}).Validate(); err != nil {
		t.Fatal(err)
	}
	t.Setenv(MapSizeEnv, "")
	t.Setenv(PlanetCoverageEnv, "")
	if d := (DebugStart{Biomes: "TemperateForest"}).withDefaults(); d.MapSize != DefaultMapSize || d.PlanetCoverage != DefaultPlanetCoverage || d.Biomes != "TemperateForest" {
		t.Fatalf("withDefaults kept %+v", d)
	}
	if d := (DebugStart{MapSize: 250, PlanetCoverage: 0.3}).withDefaults(); d.MapSize != 250 || d.PlanetCoverage != 0.3 {
		t.Fatalf("withDefaults overrode %+v", d)
	}
}
