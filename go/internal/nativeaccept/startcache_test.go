package nativeaccept

import (
	"os"
	"path/filepath"
	"regexp"
	"testing"
)

func TestCachedStartNameSeparatesTheStoryteller(t *testing.T) {
	defer func() { startCache.expansions = nil }()
	start := DebugStart{MapSize: 200, PlanetCoverage: 0.05}
	quiet, loud := cachedStartName(start, true), cachedStartName(start, false)
	if quiet == loud || loud != "RimGovernor-debug-200-0_05-loud" {
		t.Errorf("quiet %q loud %q", quiet, loud)
	}
	// Every name the op will write is a valid save name (letters, digits,
	// '_' and '-', at most 64).
	startCache.expansions = []string{"Royalty", "Biotech"}
	for _, s := range []DebugStart{
		start,
		{MapSize: 250, PlanetCoverage: 0.3, Biomes: "TemperateForest, TropicalRainforest", Flat: true, Seed: "Pin-1"},
	} {
		for _, q := range []bool{true, false} {
			name := cachedStartName(s, q)
			if len(name) > 64 || !regexp.MustCompile(`^[A-Za-z0-9_-]+$`).MatchString(name) {
				t.Errorf("name %q is not a valid save name", name)
			}
		}
	}
}

func TestCachedStartName(t *testing.T) {
	defer func() { startCache.expansions = nil }()
	startCache.expansions = nil
	if got := cachedStartName(DebugStart{MapSize: 200, PlanetCoverage: 0.05}, true); got != "RimGovernor-debug-200-0_05-quiet" {
		t.Errorf("name = %q", got)
	}
	startCache.expansions = []string{"Royalty", "Biotech"}
	if got := cachedStartName(DebugStart{MapSize: 250, PlanetCoverage: 0.3}, true); got != "RimGovernor-debug-250-0_3-royalty-biotech-quiet" {
		t.Errorf("name = %q", got)
	}
	startCache.expansions = nil
	if got := cachedStartName(DebugStart{MapSize: 200, PlanetCoverage: 0.05, Biomes: "TemperateForest, TropicalRainforest"}, true); got != "RimGovernor-debug-200-0_05-temperateforest-tropicalrain-cf435fbc" {
		t.Errorf("name = %q", got)
	}
	// A flat start never loads a plain roll's save, and a pinned
	// seed on it keeps its own name.
	if got := cachedStartName(DebugStart{MapSize: 200, PlanetCoverage: 0.05, Flat: true}, true); got != "RimGovernor-debug-200-0_05-flat-quiet" {
		t.Errorf("name = %q", got)
	}
	if got := cachedStartName(DebugStart{MapSize: 200, PlanetCoverage: 0.05, Flat: true, Seed: "abc"}, true); got != "RimGovernor-debug-200-0_05-flat-seed-abc-quiet" {
		t.Errorf("name = %q", got)
	}
}

func TestKeepGameAndCachedStartAreOnUnlessOptedOut(t *testing.T) {
	for _, tc := range []struct {
		value string
		want  bool
	}{{"", true}, {"1", true}, {"yes", true}, {"0", false}, {"false", false}, {"No", false}} {
		t.Setenv(KeepGameEnv, tc.value)
		t.Setenv(CachedStartEnv, tc.value)
		if KeepGame() != tc.want || CachedStart() != tc.want {
			t.Errorf("%q: KeepGame=%v CachedStart=%v, want %v", tc.value, KeepGame(), CachedStart(), tc.want)
		}
	}
}

func TestCachedStartStale(t *testing.T) {
	defer func() { startCache.expansions = nil }()
	dir := t.TempDir()
	write := func(name, mods string) string {
		path := filepath.Join(dir, name+".rws")
		if err := os.WriteFile(path, []byte("<savegame><meta><modIds>"+mods+"</modIds></meta></savegame>"), 0644); err != nil {
			t.Fatal(err)
		}
		return path
	}
	core := write("core", "<li>ludeon.rimworld</li><li>brrainz.harmony</li>")
	dlc := write("dlc", "<li>ludeon.rimworld</li><li>Ludeon.RimWorld.Royalty</li><li>ludeon.rimworld.biotech</li>")
	// A save recorded with every DLC is stale under a Core-only profile. Matching expansion
	// sets are current regardless of order, case or short names.
	for _, tc := range []struct {
		path       string
		expansions []string
		stale      bool
	}{
		{core, nil, false},
		{dlc, nil, true},
		{core, []string{"royalty"}, true},
		{dlc, []string{"Biotech", "royalty"}, false},
		{dlc, []string{"ludeon.rimworld.royalty"}, true},
	} {
		startCache.expansions = tc.expansions
		stale, err := cachedStartStale(tc.path, filepath.Base(tc.path))
		if err != nil {
			t.Fatal(err)
		}
		if stale != tc.stale {
			t.Errorf("stale(%s, %v) = %v, want %v", filepath.Base(tc.path), tc.expansions, stale, tc.stale)
		}
	}
}
