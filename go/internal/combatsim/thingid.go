package combatsim

import (
	"math"
	"math/rand/v2"
)

// thingNumber is the thingIDNumber a pawn id carries after its def name
// ("Thing_Human7218" is 7218); an id without digits is 0.
func thingNumber(pawnID string) int32 {
	n := len(pawnID)
	for n > 0 && pawnID[n-1] >= '0' && pawnID[n-1] <= '9' {
		n--
	}
	id := 0
	for _, c := range pawnID[n:] {
		if id = id*10 + int(c-'0'); id > math.MaxInt32 {
			return 0
		}
	}
	return int32(id)
}

// hashTick ports Verse.Gen.IsHashIntervalTick(Thing, interval, delta):
// |(TicksGame + HashOffset(thingIDNumber)) % interval| < delta. With delta 1 it
// is the plain interval tick.
func (c *combatant) hashTick(tick, interval, delta int) bool {
	r := (tick + int(hashOffset(c.thingNum))) % interval
	if r < 0 {
		r = -r
	}
	return r < delta
}

// callTick is a tick on which the pawn's Pawn.TickInterval runs with the
// off-camera 15-tick delta (GenTicks.GetCameraUpdateRate): its job tracker
// then expires and starts jobs and Wait_Combat looks for a target. The shot
// tap's recorded think ticks are exactly these (thinkphase_test.go).
func (c *combatant) callTick(tick int) bool { return c.hashTick(tick, offCameraInterval, 1) }

// Thing ids and the tick phase they set (A9). The game ticks an off-camera
// Thing when (TicksGame + Gen.HashOffset(thingID)) % 15 == 0, so a projectile's
// landing tick follows from the id it was given. The sim hands out ids in the
// order the game does, from a lab-start id, and models the one thing that
// consumes ids between shots that it can: the blood filth a wounded pawn drops.

const (
	// firstProjectileID is the lowest id the first projectile of a lab-ranged
	// run took across the 20 recorded seeds (7308); the resumed checkpoint's
	// own spawns decide it, 0 to 3 higher.
	firstProjectileID = 7308
	// filthIDsPerDamagingHit is the ids a damaging hit costs on average: the
	// recorded gaps between consecutive projectile ids total 356 extra ids over
	// 410 damaging impacts. PostApplyDamage drops blood filth with chance 0.5;
	// the rest are bleeding drops (Pawn_HealthTracker.HealthTick) after the hit,
	// lumped into the hit, and filth that stacks on an existing one takes none.
	filthIDsPerDamagingHit = 356.0 / 410.0
)

// firstThingID draws the id of a run's first projectile: 7308 to 7311 with the
// frequencies 7, 7, 4 and 2 in 20 the recorded seeds showed.
func firstThingID(r *rand.Rand) int32 {
	switch n := r.IntN(20); {
	case n < 7:
		return firstProjectileID
	case n < 14:
		return firstProjectileID + 1
	case n < 18:
		return firstProjectileID + 2
	}
	return firstProjectileID + 3
}

// takeID hands out the next thing id.
func (s *Sim) takeID() int32 {
	id := s.nextID
	s.nextID++
	return id
}

// dropFilth is the ids a damaging hit's blood filth consumes: at most one per
// hit, with probability filthIDsPerDamagingHit.
func (s *Sim) dropFilth() {
	if s.streams.Phase.Float64() < filthIDsPerDamagingHit {
		s.nextID++
	}
}

// hashOffset ports Verse.Gen.HashOffset: Gen.HashCombineInt(id, 169495093)
// (int arithmetic wraps, the shift of a negative id is arithmetic).
func hashOffset(id int32) int32 {
	v := int64(169495093) + int64(2654435769) + int64(id<<6) + int64(id>>2)
	return int32(int64(id) ^ v)
}

// idPhase is the residue mod offCameraInterval a thing with this id is ticked
// on: the ticks t with (t + HashOffset(id)) % 15 == 0.
func idPhase(id int32) int {
	r := int(hashOffset(id)) % offCameraInterval
	return (offCameraInterval - r) % offCameraInterval
}
