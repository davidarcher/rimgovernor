package combatsim

import "testing"

// Two raiders' recorded Wait_Combat jobs (lab-split-ranged seed 1: Thing_Human7302
// from 780 to 1401, Thing_Human7294 from 1493 to 1986) both ended later than
// the 450 to 550 ticks they were given, and each ending is the first expiry
// window (A41) after the nominal end for some length in that range.
func TestWaitCombatExpiryWindowReproducesRecordedEnds(t *testing.T) {
	for _, c := range []struct {
		id         string
		start, end int
	}{{"Thing_Human7302", 780, 1401}, {"Thing_Human7294", 1493, 1986}} {
		p := &pawn{}
		p.thingNum = thingNumber(c.id)
		found := false
		for e := 450; e <= 550 && !found; e++ {
			for tick := c.start + e; ; tick++ {
				if p.callTick(tick) && p.hashTick(tick, e, offCameraInterval) {
					found = tick == c.end
					break
				}
			}
		}
		if !found {
			t.Errorf("%s: no expiry interval in 450..550 ends the job started at %d on %d", c.id, c.start, c.end)
		}
	}
}

// The recorded think cadence (lab-split-ranged seed 1, the shot tap's first
// JobTrackerTickInterval ticks of each pawn) is the pawn's 15-tick hash phase.
func TestPawnThinkPhaseMatchesRecordedTicks(t *testing.T) {
	for id, want := range map[int32]int{
		7198: 2, 7210: 2, 7218: 10, 7252: 6, 7264: 3, 7273: 3,
		7282: 11, 7294: 6, 7302: 6, 7307: 13, 7316: 4, 7325: 12,
	} {
		if got := idPhase(id); got != want {
			t.Errorf("idPhase(%d) = %d, the tap recorded %d", id, got, want)
		}
	}
}
