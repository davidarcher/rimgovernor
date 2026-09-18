package nativeaccept

import "testing"

func TestCachedStartName(t *testing.T) {
	defer func() { startCache.expansions = nil }()
	startCache.expansions = nil
	if got := cachedStartName(DebugStart{MapSize: 200, PlanetCoverage: 0.05}); got != "RimGovernor-debug-200-0_05" {
		t.Errorf("name = %q", got)
	}
	startCache.expansions = []string{"Royalty", "Biotech"}
	if got := cachedStartName(DebugStart{MapSize: 250, PlanetCoverage: 0.3}); got != "RimGovernor-debug-250-0_3-royalty-biotech" {
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
