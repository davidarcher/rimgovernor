package combatsim

import "math/rand/v2"

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
