package nativeaccept

import "testing"

func TestQuietDecision(t *testing.T) {
	with := []string{"rimworld/start_debug_game_ready", QuietStorytellerTool}
	without := []string{"rimworld/start_debug_game_ready"}
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
	for _, bad := range []DebugStart{{100, 0.05}, {500, 0.05}, {200, 0.01}, {200, 2}} {
		if bad.Validate() == nil {
			t.Fatalf("%+v validated", bad)
		}
	}
	if err := (DebugStart{150, 1}).Validate(); err != nil {
		t.Fatal(err)
	}
}
