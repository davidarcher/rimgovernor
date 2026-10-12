package combatsim

import (
	"math/rand/v2"
	"testing"
)

// Recorded lab-ranged projectiles (seed 1): thing id and landing tick. Every
// recorded landing is on a tick where (tick + HashOffset(id)) % 15 == 0.
func TestIDPhaseMatchesRecordedLandings(t *testing.T) {
	for _, c := range []struct{ id, impact int }{
		{7308, 111}, {7310, 102}, {7311, 99}, {7309, 108}, {7317, 106}, {7312, 107},
		{7316, 109}, {7315, 112}, {7313, 117}, {7314, 117}, {7320, 118}, {7318, 119},
	} {
		if got := c.impact % offCameraInterval; got != idPhase(int32(c.id)) {
			t.Errorf("id %d landed on %d, idPhase %d", c.id, got, idPhase(int32(c.id)))
		}
	}
}

func TestFirstThingIDStaysInTheRecordedRange(t *testing.T) {
	r := rand.New(rand.NewPCG(1, 2))
	seen := map[int32]int{}
	for range 2000 {
		seen[firstThingID(r)]++
	}
	if len(seen) != 4 || seen[firstProjectileID] < seen[firstProjectileID+3] {
		t.Errorf("first ids %v, want 7308..7311 with the lowest likeliest", seen)
	}
}

// Every launched projectile takes an id, wild or not; a damaging hit may take
// more, so the counter never falls behind the shots.
func TestEachShotTakesAnID(t *testing.T) {
	sc := duel(t, rapid(), immortal, "a.........b")
	s := newSim(t, sc, 4)
	start := s.nextID
	_, ev := runUntil(t, s, []Order{autoAttack("a", "b")}, 300, nil)
	if n := int32(len(ofKind(ev, EventShot))); n == 0 || s.nextID < start+n {
		t.Errorf("%d shots took ids %d..%d", n, start, s.nextID)
	}
}
