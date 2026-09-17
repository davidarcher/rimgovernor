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
