package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Tier-styled buildings (#610): the shell, flooring and lighting planners
// read their definitions and stuff from policy's tier rules, each
// f(tier, role, stock) with one stock rung of fallback. Every helper here
// folds the projection into the rule's inputs: the build tier, the stock
// census, the finished research and whether a powered source runs. An
// unknown tier reads as Camp, so a colony whose research census has not
// arrived yet is styled as a Camp and never receives a stone, powered or
// floored proposal.

// styleTier is the build tier the rules read: the projection's, Camp when
// unknown.
func styleTier(facts observation.ColonyProjection) policy.BuildTier {
	if tier, known := facts.BuildTier.Value(); known {
		return tier
	}
	return policy.BuildTierCamp
}

// styleStock is the stock census the rules read; an unknown census holds
// nothing, and the rules then fall back to their lowest rung.
func styleStock(facts observation.ColonyProjection) policy.TierStyleStock {
	stock, known := facts.Resources.Value()
	if !known {
		return policy.TierStyleStock{}
	}
	return policy.TierStyleStock(stock)
}

// styleResearchFinished reports one finished ResearchProjectDef; an unknown
// census reads as unfinished.
func styleResearchFinished(facts observation.ColonyProjection, project string) bool {
	return policy.ResearchGate([]string{project}, facts.Facts.Research) == ""
}

// poweredSource reports whether any observed power network generates:
// unknown without a power topology.
func poweredSource(facts observation.ColonyProjection) domain.Fact[bool] {
	topology, known := facts.PowerPlanning.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	for _, net := range topology.Networks {
		if generation, gk := net.GenerationW.Value(); gk && generation > 0 {
			return domain.Known(true)
		}
	}
	return domain.Known(false)
}

// shellStyle is the wall and door style a shell planner expands a ring
// with: the wall stuff ladder per part and the door ladder, with a wood
// Door where the rules name nothing (the Camp rung with no wood stocked),
// which the placement preview then refuses on stock as it always has.
func shellStyle(facts observation.ColonyProjection) domain.ShellStyle {
	tier, stock := styleTier(facts), styleStock(facts)
	powered, _ := poweredSource(facts).Value()
	style := domain.ShellStyle{WallDef: "Wall", DoorDef: "Door", DoorStuff: "WoodLog"}
	autodoors := styleResearchFinished(facts, "Autodoors") && routineDefinitionsAvailable(facts, []string{"Autodoor"}, true)
	if door, ok := policy.DoorDef(tier, stock, autodoors, powered); ok {
		style.DoorDef, style.DoorStuff = door.Definition, door.Stuff
	}
	style.WallStuff = func(part domain.ShellPart) string {
		wallPart := policy.WallRun
		switch part {
		case domain.ShellCorner:
			wallPart = policy.WallCorner
		case domain.ShellDoorFrame:
			wallPart = policy.WallDoorFrame
		}
		if stuff, ok := policy.WallStuff(tier, wallPart, stock); ok {
			return string(stuff)
		}
		return "WoodLog"
	}
	return style
}

// floorStyle is the floor rule the flooring planner prefers per room role
// before it scores the census: nil at Camp, where no floor is styled.
func floorStyle(facts observation.ColonyProjection) func(policy.RoomRole) (string, bool) {
	tier, stock := styleTier(facts), styleStock(facts)
	if tier < policy.BuildTierMasonry {
		return nil
	}
	research := policy.FloorStyleFacts{CarpetMaking: styleResearchFinished(facts, "CarpetMaking"), SterileMaterials: styleResearchFinished(facts, "SterileMaterials")}
	return func(role policy.RoomRole) (string, bool) {
		return policy.FloorDef(tier, role, stock, research)
	}
}

// lampStyle is the fixture the lighting planner prefers for a work cell:
// the module lighting rule for a non-farm module, empty when the tier
// styles no lamp.
func lampStyle(facts observation.ColonyProjection) string {
	powered, _ := poweredSource(facts).Value()
	if lamp, ok := policy.ModuleLighting(styleTier(facts), false, styleStock(facts), powered); ok {
		return lamp.Definition
	}
	return ""
}
