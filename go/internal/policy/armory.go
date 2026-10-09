package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ArmoryTier is how far up the military ladder the armory planner reaches:
// the threat the storyteller's raid points call for, capped by the
// research the colony has finished. Higher tiers include the lower ones.
type ArmoryTier int

const (
	// ArmoryTierUnknown: the raid-point reading is not observed yet; the
	// armory plans nothing on a guess.
	ArmoryTierUnknown ArmoryTier = iota
	// ArmoryTierNeolithic needs no research: bows, clubs, tribal wear.
	ArmoryTierNeolithic
	// ArmoryTierSmithing: forged melee weapons and plate (Smithing).
	ArmoryTierSmithing
	// ArmoryTierMachining: firearms and flak (Machining).
	ArmoryTierMachining
	// ArmoryTierFabrication: charge weapons and marine armor (Fabrication).
	ArmoryTierFabrication
)

func (t ArmoryTier) String() string {
	switch t {
	case ArmoryTierNeolithic:
		return "neolithic"
	case ArmoryTierSmithing:
		return "smithing"
	case ArmoryTierMachining:
		return "machining"
	case ArmoryTierFabrication:
		return "fabrication"
	}
	return "unknown"
}

// The raid-point thresholds at which the threat tier steps up. The lower
// two match the turret budget's (TurretBudget) so defenses and gear scale
// together.
const (
	armorySmithingPoints    = turretStepPoints
	armoryMachiningPoints   = turretHighPoints
	armoryFabricationPoints = 2500
)

// armoryResearch is the research project each tier above neolithic needs.
var armoryResearch = map[ArmoryTier]ResearchProjectID{
	ArmoryTierSmithing:    "Smithing",
	ArmoryTierMachining:   "Machining",
	ArmoryTierFabrication: "Fabrication",
}

// ArmoryAssessment is the armory planner's tier selection: the threat tier
// the raid points call for, the research tier finished projects allow, and
// the tier planned (the lower of the two).
type ArmoryAssessment struct {
	Threat, Research, Tier ArmoryTier
}

// ArmoryThreatTier is the tier the observed raid points call for; unknown
// (or NaN) points are ArmoryTierUnknown.
func ArmoryThreatTier(raidPoints domain.Fact[float64]) ArmoryTier {
	points, known := raidPoints.Value()
	switch {
	case !known || math.IsNaN(points):
		return ArmoryTierUnknown
	case points < armorySmithingPoints:
		return ArmoryTierNeolithic
	case points < armoryMachiningPoints:
		return ArmoryTierSmithing
	case points < armoryFabricationPoints:
		return ArmoryTierMachining
	}
	return ArmoryTierFabrication
}

// ArmoryResearchTier is the highest tier whose research, and every lower
// tier's, is finished. An unknown census allows only neolithic.
func ArmoryResearchTier(research domain.Fact[ResearchFacts]) ArmoryTier {
	finished := map[ResearchProjectID]bool{}
	if facts, known := research.Value(); known {
		for _, project := range facts.Finished {
			finished[project] = true
		}
	}
	tier := ArmoryTierNeolithic
	for next := ArmoryTierSmithing; next <= ArmoryTierFabrication && finished[armoryResearch[next]]; next++ {
		tier = next
	}
	return tier
}

// AssessArmory selects the armory tier from raid points and research.
func AssessArmory(raidPoints domain.Fact[float64], research domain.Fact[ResearchFacts]) ArmoryAssessment {
	a := ArmoryAssessment{Threat: ArmoryThreatTier(raidPoints), Research: ArmoryResearchTier(research)}
	a.Tier = min(a.Threat, a.Research)
	return a
}

// WeaponQualityMultiplier scales a weapon's planning score by quality
// (Awful=0 through Legendary=6), the weapon counterpart of
// GearQualityMultipliers; out of range is 0.
func WeaponQualityMultiplier(quality int) float64 {
	if quality < 0 || quality > 6 {
		return 0
	}
	return [...]float64{.8, .9, 1, 1.1, 1.2, 1.35, 1.5}[quality]
}

// ArmoryPrimary is a pawn's equipped primary weapon.
type ArmoryPrimary struct {
	Definition string
	Ranged     bool
	Quality    int
	// Facts is the primary's def rows.
	Facts WeaponDef
}

// ArmoryWeaponDemand is the armory's bill target: per colonist, the
// best weapon a hosted, researched recipe at or under the tier makes. An
// unarmed colonist the loose weapons cannot arm wants one (an unknown tier
// arms at neolithic: arming the unarmed is never held on a guess); an armed
// colonist wants one only at a known tier, only when it beats the
// quality-scaled primary, and only when no loose weapon of
// that definition is left for it. The gear planner's GearReplace wears the
// upgrade once it is made.
//
// A hunter (WeaponRoleHunter) wants a hunting weapon (WeaponDef.Hunts), so one
// unarmed or armed without it is demand like any unarmed colonist. Those
// weapons are returned apart as hunters: food owns that craft
// (EnsureFoodSupply). A hunter's upgrade
// stays an ordinary fighter's.
func ArmoryWeaponDemand(tier ArmoryTier, pawns []EquipCandidatePawn, primaries map[domain.PawnID]ArmoryPrimary, weapons []EquipCandidateWeapon, recipes []GearRecipe, products map[Resource]WeaponDef) (fighters, hunters []Amount) {
	assigned := map[domain.PawnID]bool{}
	for _, pair := range AssignEquip(pawns, weapons) {
		assigned[pair.Pawn] = true
	}
	loose := map[Resource]int{}
	for _, w := range weapons {
		loose[Resource(w.Definition)]++
	}
	counts, hunts := map[Resource]int64{}, map[Resource]int64{}
	for _, p := range pawns {
		armed, known := p.Armed.Value()
		if !known || assigned[p.Pawn] || !armoryFighter(p) {
			continue
		}
		reach, floor, lacks := tier, 0.0, !armed
		if armed {
			current, ok := primaries[p.Pawn]
			if !ok || tier == ArmoryTierUnknown {
				continue
			}
			class := WeaponMelee
			if current.Ranged {
				class = WeaponRanged
			}
			lacks = !current.Facts.Hunts()
			floor = ScoreWeapon(p, EquipCandidateWeapon{Definition: current.Definition, Class: class, Facts: current.Facts}) * WeaponQualityMultiplier(current.Quality)
		} else if reach == ArmoryTierUnknown {
			reach = ArmoryTierNeolithic
		}
		best, score := armoryBestWeapon(p, reach, recipes, products)
		if best == "" || score <= floor {
			continue
		}
		if armed && loose[best] > 0 {
			loose[best]--
			continue
		}
		if p.Role == WeaponRoleHunter && lacks {
			hunts[best]++
		} else {
			counts[best]++
		}
	}
	return amounts(counts), amounts(hunts)
}

func amounts(counts map[Resource]int64) []Amount {
	demand := make([]Amount, 0, len(counts))
	for def, count := range counts {
		demand = append(demand, Amount{Resource: def, Count: count})
	}
	sort.Slice(demand, func(i, j int) bool { return demand[i].Resource < demand[j].Resource })
	return demand
}

// armoryFighter is a present colonist capable of violence, armed or not.
func armoryFighter(p EquipCandidatePawn) bool {
	if p.NoArms != "" {
		return false
	}
	for _, f := range []domain.Fact[bool]{p.Dead, p.Downed, p.Drafted, p.MentalState, p.IncapableOfViolence} {
		if value, known := f.Value(); !known || value {
			return false
		}
	}
	return true
}

func armoryBestWeapon(p EquipCandidatePawn, reach ArmoryTier, recipes []GearRecipe, products map[Resource]WeaponDef) (Resource, float64) {
	var best Resource
	bestScore := 0.0
	for _, recipe := range recipes {
		if !WeaponRecipe(recipe) || recipe.Armory > reach || !positive(recipe.Available) || !positive(recipe.AvailableOn) {
			continue
		}
		for _, def := range recipe.Products {
			facts, known := products[def]
			if !known || !facts.Armament() {
				continue
			}
			class := WeaponRanged
			if !facts.Ranged {
				class = WeaponMelee
			}
			score := ScoreWeapon(p, EquipCandidateWeapon{Definition: string(def), Class: class, Facts: facts})
			if score > bestScore || score > 0 && score == bestScore && def < best {
				best, bestScore = def, score
			}
		}
	}
	return best, bestScore
}
