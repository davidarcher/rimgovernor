package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// BuildTier is how far the colony's construction technology has come
// (#604): the one ordered fact every layout decision reads instead of
// research or the faction tech level directly. It is derived from finished
// research with the player faction's tech level as a floor, because
// RimWorld never advances a faction's techLevel with research: a tribe that
// has finished Stonecutting and Electricity still reads Neolithic.
type BuildTier int

const (
	BuildTierCamp BuildTier = iota
	BuildTierMasonry
	BuildTierPowered
	BuildTierIndustrial
	BuildTierSpacer
)

var buildTierNames = [...]string{"Camp", "Masonry", "Powered", "Industrial", "Spacer"}

func (t BuildTier) String() string {
	if t < 0 || int(t) >= len(buildTierNames) {
		return "Camp"
	}
	return buildTierNames[t]
}

// techLevelRank orders the native TechLevel names a faction def carries;
// Undefined and anything unrecognised rank unknown.
var techLevelRank = map[string]int{"Animal": 1, "Neolithic": 2, "Medieval": 3, "Industrial": 4, "Spacer": 5, "Ultra": 6, "Archotech": 7}

// SelectBuildTier derives the build tier from the finished research and the
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
func SelectBuildTier(finished domain.Fact[[]ResearchProjectID], factionLevel domain.Fact[string]) domain.Fact[BuildTier] {
	tier, _ := buildTier(finished, factionLevel)
	return tier
}

// BuildTierEvidence names the condition that satisfied the selected tier
// (a research project, or "faction <level>" for a floor); empty for Camp
// or an unknown tier.
func BuildTierEvidence(finished domain.Fact[[]ResearchProjectID], factionLevel domain.Fact[string]) string {
	_, evidence := buildTier(finished, factionLevel)
	return evidence
}

func buildTier(finished domain.Fact[[]ResearchProjectID], factionLevel domain.Fact[string]) (domain.Fact[BuildTier], string) {
	projects, known := finished.Value()
	level, levelKnown := factionLevel.Value()
	rank, ranked := techLevelRank[level]
	if !known || !levelKnown || !ranked {
		return domain.Unknown[BuildTier](), ""
	}
	done := map[ResearchProjectID]bool{}
	for _, id := range projects {
		done[id] = true
	}
	// The floor: the tier the faction level satisfies outright.
	tier, evidence := BuildTierCamp, ""
	switch {
	case rank >= techLevelRank["Spacer"]:
		tier = BuildTierSpacer
	case rank >= techLevelRank["Industrial"]:
		tier = BuildTierIndustrial
	case rank >= techLevelRank["Medieval"]:
		tier = BuildTierMasonry
	}
	if tier > BuildTierCamp {
		evidence = "faction " + level
	}
	// Research raises the tier above the floor one condition at a time.
	unlocks := map[BuildTier][]ResearchProjectID{
		BuildTierMasonry:    {"Stonecutting"},
		BuildTierPowered:    {"Electricity"},
		BuildTierIndustrial: {"Machining", "Fabrication"},
		BuildTierSpacer:     {"AdvancedFabrication"},
	}
	for tier < BuildTierSpacer {
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
