package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// TechTier is how far the colony's construction technology has come:
// the one ordered fact every layout decision reads instead of
// research or the faction tech level directly. It is derived from finished
// research with the player faction's tech level as a floor, because
// RimWorld never advances a faction's techLevel with research: a tribe that
// has finished Stonecutting and Electricity still reads Neolithic.
type TechTier int

const (
	TechTierCamp TechTier = iota
	TechTierMasonry
	TechTierPowered
	TechTierIndustrial
	TechTierSpacer
)

var techTierNames = [...]string{"Camp", "Masonry", "Powered", "Industrial", "Spacer"}

func (t TechTier) String() string {
	if t < 0 || int(t) >= len(techTierNames) {
		return "Camp"
	}
	return techTierNames[t]
}

// techLevelRank orders the native TechLevel names a faction def carries;
// Undefined and anything unrecognised rank unknown.
var techLevelRank = map[string]int{"Animal": 1, "Neolithic": 2, "Medieval": 3, "Industrial": 4, "Spacer": 5, "Ultra": 6, "Archotech": 7}

// SelectTechTier derives the tech tier from the finished research and the
// faction tech level. A faction floor satisfies every tier at or below it;
// research raises the tier above the floor one condition at a time:
//
//	Camp        default
//	Masonry     Stonecutting finished, or faction >= Medieval
//	Powered     Masonry and Electricity finished
//	Industrial  Powered and (Machining or Fabrication finished, or faction >= Industrial)
//	Spacer      Industrial and (AdvancedFabrication finished, or faction >= Spacer)
//
// A faction floor satisfies every tier at or below it, so an Industrial
// faction stands at Industrial without Electricity in its finished list.
//
// Unknown research or an unknown faction level is an unknown fact; the tier
// never advances on unknown inputs, and consumers treat unknown as Camp.
func SelectTechTier(finished domain.Fact[[]ResearchProjectID], factionLevel domain.Fact[string]) domain.Fact[TechTier] {
	tier, _ := techTier(finished, factionLevel)
	return tier
}

// TechTierEvidence names the condition that satisfied the selected tier
// (a research project, or "faction <level>" for a floor); empty for Camp
// or an unknown tier.
func TechTierEvidence(finished domain.Fact[[]ResearchProjectID], factionLevel domain.Fact[string]) string {
	_, evidence := techTier(finished, factionLevel)
	return evidence
}

func techTier(finished domain.Fact[[]ResearchProjectID], factionLevel domain.Fact[string]) (domain.Fact[TechTier], string) {
	projects, known := finished.Value()
	level, levelKnown := factionLevel.Value()
	rank, ranked := techLevelRank[level]
	if !known || !levelKnown || !ranked {
		return domain.Unknown[TechTier](), ""
	}
	done := map[ResearchProjectID]bool{}
	for _, id := range projects {
		done[id] = true
	}
	// The floor: the tier the faction level satisfies outright.
	tier, evidence := TechTierCamp, ""
	switch {
	case rank >= techLevelRank["Spacer"]:
		tier = TechTierSpacer
	case rank >= techLevelRank["Industrial"]:
		tier = TechTierIndustrial
	case rank >= techLevelRank["Medieval"]:
		tier = TechTierMasonry
	}
	if tier > TechTierCamp {
		evidence = "faction " + level
	}
	// Research raises the tier above the floor one condition at a time.
	unlocks := map[TechTier][]ResearchProjectID{
		TechTierMasonry:    {"Stonecutting"},
		TechTierPowered:    {"Electricity"},
		TechTierIndustrial: {"Machining", "Fabrication"},
		TechTierSpacer:     {"AdvancedFabrication"},
	}
	for tier < TechTierSpacer {
		next, raised := tier+1, ""
		for _, id := range unlocks[next] {
			if done[id] {
				raised = string(id)
				break
			}
		}
		if raised == "" {
			break
		}
		tier, evidence = next, raised
	}
	return domain.Known(tier), evidence
}
