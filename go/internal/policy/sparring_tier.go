package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// SparringTier is one rung of the melee practice ladder: a practice weapon the
// spar job issues. The table mirrors the SparringTier DefModExtension on each
// weapon def in integrations/rimgovernor-native/Defs/ThingDefs/SparringRing.xml,
// which owns the values; sparring_tier_test.go parses that XML and pins every
// field. Damage is flat per damage type and vanilla pays the melee XP, so a tier
// buys a higher ceiling and nothing else.
type SparringTier struct {
	Weapon string
	// DamageType is the weapon's one tool's damage type.
	DamageType string
	// Power is the tool's power: shared by weapons of one damage type.
	Power int
	// Ceiling is the skill level at which the weapon stops teaching.
	Ceiling int
	// Gate is the research project that unlocks the tier; empty means none.
	Gate string
	// Apparel is the practice apparel issued with the weapon, by def name: the
	// tunic always, helmet and gloves at the sharp tiers, a padded vest from tier 2.
	Apparel []string
}

const (
	practiceTunic  = "Apparel_PracticeTunic"
	practiceHelmet = "Apparel_PracticeHelmet"
	practiceGloves = "Apparel_PracticeGloves"
	practiceVest   = "Apparel_PracticeVest"
)

// SparringTiers is the ladder, tier 0 first. The power numbers are tuned by the
// lab case (#2711).
var SparringTiers = []SparringTier{
	{Weapon: "MeleeWeapon_PracticeClub", DamageType: "Blunt", Power: 3, Ceiling: 8,
		Apparel: []string{practiceTunic}},
	{Weapon: "MeleeWeapon_PracticeSword", DamageType: "Cut", Power: 3, Ceiling: 12, Gate: "Smithing",
		Apparel: []string{practiceTunic, practiceHelmet, practiceGloves}},
	{Weapon: "MeleeWeapon_PracticeSwordBetter", DamageType: "Cut", Power: 3, Ceiling: 16, Gate: "Machining",
		Apparel: []string{practiceTunic, practiceHelmet, practiceGloves, practiceVest}},
	{Weapon: "MeleeWeapon_PracticeSwordBest", DamageType: "Cut", Power: 3, Ceiling: 20, Gate: "Fabrication",
		Apparel: []string{practiceTunic, practiceHelmet, practiceGloves, practiceVest}},
}

// UnlockedSparringTier is the best tier whose gate research is finished; tier 0
// has no gate, so an unknown census holds at the club. The native rules read the
// same census through the same rule (SparringRules.UnlockedWeapon).
func UnlockedSparringTier(research domain.Fact[ResearchFacts]) SparringTier {
	facts, _ := research.Value()
	best := SparringTiers[0]
	for _, tier := range SparringTiers[1:] {
		if slices.Contains(facts.Finished, ResearchProjectID(tier.Gate)) {
			best = tier
		}
	}
	return best
}
