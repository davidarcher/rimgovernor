package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ArmoryTier is how far up the military ladder the armory planner reaches
// (#1198): the threat the storyteller's raid points call for, capped by the
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
	armorySmithingPoints    = turretMidPoints
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
