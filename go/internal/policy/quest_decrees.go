package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// DecreeObjective identifies outstanding native progress; inventory is never
// progress for a production or harvest decree.
func DecreeObjective(offer JoinerOffer) (QuestObjective, int64, bool) {
	if offer.State != "Ongoing" {
		return QuestObjective{}, 0, false
	}
	profile, known := offer.Profile.Value()
	if !known {
		return QuestObjective{}, 0, false
	}
	var kind o.QuestObjectiveKind
	switch profile.Family {
	case QuestFamilyDecreeProduce:
		kind = o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_PRODUCE_ITEM
	case QuestFamilyDecreeHarvest:
		kind = o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_HARVEST_PLANT
	case QuestFamilyDecreeHunt:
		kind = o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_KILL_ANIMALS
	default:
		return QuestObjective{}, 0, false
	}
	for _, objective := range offer.Objectives {
		count, ck := objective.Count.Value()
		produced, pk := objective.Produced.Value()
		active, ak := objective.Active.Value()
		if objective.Kind == kind && ck && pk && ak && active && count > produced && objective.Def != "" {
			return objective, count - produced, true
		}
	}
	return QuestObjective{}, 0, false
}

func DecreeDeficit(f RoundsFacts) bool {
	rows, known := f.QuestOffers.Value()
	if !known {
		return false
	}
	for _, offer := range rows {
		if _, _, owed := DecreeObjective(offer); owed {
			return true
		}
	}
	return false
}

type DecreeRecipe struct {
	Bench, Recipe string
	Product       Resource
	Units         int64
	Available     domain.Fact[bool]
	Ingredients   domain.Fact[[][]Amount]
	// Stuff lists the recipe alternatives that determine its output's stuff.
	Stuff map[Resource]bool
	// Existing tracks a finite bill with the required ingredient filter.
	Existing bool
	Replace  string
}

// PlanDecreeProduction funds a repeat-count batch from the outstanding crafted
// count. A stock-target bill would stop on inventory that the quest never counts.
func PlanDecreeProduction(objective QuestObjective, remaining int64, recipes []DecreeRecipe, stock domain.Fact[[]Amount]) (domain.ProductionBill, string) {
	if remaining <= 0 {
		return domain.ProductionBill{}, "complete"
	}
	rows, known := stock.Value()
	if !known {
		return domain.ProductionBill{}, "ingredients_unknown"
	}
	available := map[Resource]int64{}
	for _, row := range rows {
		available[row.Resource] = row.Count
	}
	recipes = slices.Clone(recipes)
	sort.Slice(recipes, func(i, j int) bool {
		if recipes[i].Bench != recipes[j].Bench {
			return recipes[i].Bench < recipes[j].Bench
		}
		return recipes[i].Recipe < recipes[j].Recipe
	})
	for _, recipe := range recipes {
		if recipe.Product != Resource(objective.Def) || recipe.Units <= 0 {
			continue
		}
		if recipe.Existing {
			return domain.ProductionBill{}, "existing_work"
		}
		if usable, known := recipe.Available.Value(); !known || !usable {
			continue
		}
		groups, known := recipe.Ingredients.Value()
		if !known {
			continue
		}
		iterations := (remaining + recipe.Units - 1) / recipe.Units
		if iterations > 10000 {
			return domain.ProductionBill{}, "batch_too_large"
		}
		filter := []string{}
		spent := map[Resource]int64{}
		funded := true
		stuffFound := objective.Stuff == ""
		for _, group := range groups {
			var chosen *Amount
			for i, alternative := range group {
				if recipe.Stuff[alternative.Resource] && objective.Stuff != "" && alternative.Resource != Resource(objective.Stuff) {
					continue
				}
				need := alternative.Count * iterations
				if alternative.Count <= 0 || available[alternative.Resource]-spent[alternative.Resource] < need {
					continue
				}
				if chosen == nil || alternative.Resource < chosen.Resource {
					chosen = &group[i]
				}
			}
			if chosen == nil {
				funded = false
				break
			}
			spent[chosen.Resource] += chosen.Count * iterations
			filter = append(filter, string(chosen.Resource))
			if string(chosen.Resource) == objective.Stuff && recipe.Stuff[chosen.Resource] {
				stuffFound = true
			}
		}
		if !funded || !stuffFound {
			continue
		}
		sort.Strings(filter)
		filter = slices.Compact(filter)
		bill, err := domain.NewProductionBill(recipe.Bench, recipe.Recipe, domain.GearBatch, int32(iterations), filter...)
		if err != nil {
			continue
		}
		if recipe.Replace != "" {
			bill, err = bill.ReplaceOwnedBill(recipe.Replace)
			if err != nil {
				continue
			}
		}
		return bill, ""
	}
	return domain.ProductionBill{}, "production_infeasible"
}

func PlanDecreeHarvest(objective QuestObjective, remaining, standing int64, choices []CropChoice, climate CropClimate, site FarmSiteRequest, skill domain.Fact[int32]) (FieldPlan, string) {
	if remaining <= 0 {
		return FieldPlan{}, "complete"
	}
	if standing >= remaining {
		return FieldPlan{}, "existing_work"
	}
	var exact []CropChoice
	for _, crop := range choices {
		if crop.Name == objective.Def {
			crop.Harvests = domain.Known(Resource(crop.Name))
			crop.UnitsPerCell = domain.Known(1.0)
			exact = append(exact, crop)
		}
	}
	plan, ok := PlanFieldByResource(ResourceFieldRequest{Resource: Resource(objective.Def), Deficit: domain.Known(float64(remaining - standing)), Choices: exact, Climate: climate, Site: site, GrowerSkill: skill})
	if !ok {
		return plan, "plant_infeasible"
	}
	return plan, ""
}

func PlanDecreeHunt(objective QuestObjective, remaining int64, allowed domain.Fact[bool], sources domain.Fact[[]AcquisitionSource]) ([]AcquisitionSource, string) {
	if violent, known := allowed.Value(); !known || !violent {
		return nil, "violent_quests_off"
	}
	rows, known := sources.Value()
	if !known {
		return nil, "prey_unknown"
	}
	var pending int64
	var prey []AcquisitionSource
	for _, row := range rows {
		if row.Hunt && row.Definition == objective.Def {
			if row.Designated || row.Taken {
				pending++
			} else {
				prey = append(prey, row)
			}
		}
	}
	if pending >= remaining {
		return nil, "existing_work"
	}
	if int64(len(prey))+pending < remaining {
		return nil, "species_absent"
	}
	sort.Slice(prey, func(i, j int) bool { return prey[i].ID < prey[j].ID })
	return prey[:remaining-pending], ""
}
