package policy

// TrainingTier is one rung of the shooting practice ladder: a practice weapon
// the training drill issues, and what it pays. The table mirrors the
// TrainingTier DefModExtension on each weapon def in
// integrations/rimgovernor-native/Defs/ThingDefs/TrainingRange.xml, which owns
// the values; training_range_test.go parses that XML and pins every field.
type TrainingTier struct {
	Weapon string
	// Projectile is the weapon's themed projectile, which deals exactly 1
	// damage (Thing.TakeDamage returns early at 0).
	Projectile string
	// Multiplier is the XP multiple against tier 0.
	Multiplier float64
	// XPPerShot is the skill XP one shot applies.
	XPPerShot int
	// Ceiling is the skill level at which the weapon stops teaching.
	Ceiling int
	// Gate is the research project that unlocks the tier; empty means none.
	Gate string
}

// TrainingTiers is the ladder, tier 0 first. Every weapon shares the 3 s shot
// cycle, so XP per shot is XP per cycle.
var TrainingTiers = []TrainingTier{
	{Weapon: RangeWeaponDef, Projectile: "Arrow_Practice", Multiplier: 1, XPPerShot: 75, Ceiling: 8},
	{Weapon: "Gun_PracticeRifle", Projectile: "Bullet_PracticeRubber", Multiplier: 1.5, XPPerShot: 112, Ceiling: 10, Gate: "Gunsmithing"},
	{Weapon: "Gun_PracticePulseRifle", Projectile: "Bullet_PracticePulse", Multiplier: 2.5, XPPerShot: 187, Ceiling: 12, Gate: "ChargedShot"},
	{Weapon: "Gun_PracticeBeamEmitter", Projectile: "Bullet_PracticeBeam", Multiplier: 4, XPPerShot: 300, Ceiling: 15, Gate: "BeamWeapons"},
}
