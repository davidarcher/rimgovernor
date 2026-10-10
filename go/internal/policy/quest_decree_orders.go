package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// DecreeOrderRequest is what one Round's produce-decree declaration reads. Now
// is the review's tick and Recipes the bench census's recipes making an
// objective's item.
type DecreeOrderRequest struct {
	Offers  domain.Fact[[]JoinerOffer]
	Now     int64
	Stock   domain.Fact[[]Amount]
	Recipes func(QuestObjective) []DecreeRecipe
}

// DeclareDecreeOrders is MaintainPopulation's wanted production orders: for
// every accepted produce-item decree of the player's faction that is still
// open, the batch PlanDecreeProduction funds from the crafted count left, or
// the bill that already makes it, declared as it stands. A decree the quest
// has completed, expired or that no recipe can fund declares nothing, so the
// ledger removes its bill. Abstain while the offers, the asker or the stock
// are unread.
func DeclareDecreeOrders(r DecreeOrderRequest) Declared {
	offers, known := r.Offers.Value()
	if !known {
		return Abstaining(UnreadQuestOffers)
	}
	offers = append([]JoinerOffer(nil), offers...)
	sort.Slice(offers, func(i, j int) bool { return offers[i].Quest < offers[j].Quest })
	var out Declared
	for _, offer := range offers {
		objective, remaining, owed := DecreeObjective(offer)
		if !owed || objective.Kind != o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_PRODUCE_ITEM {
			continue
		}
		player, known := offer.AskerFactionPlayer.Value()
		if !known {
			out.Unread(UnreadQuestAsker)
			continue
		}
		if deadline, known := objective.DeadlineTicks.Value(); !player || known && deadline <= r.Now {
			continue
		}
		recipes := r.Recipes(objective)
		bill, why := PlanDecreeProduction(objective, remaining, recipes, r.Stock)
		switch why {
		case "":
			spec := OrderSpec{Recipe: bill.Recipe(), Ingredients: bill.Ingredients(), Mode: domain.GearBatch, Target: bill.Target()}
			for _, recipe := range recipes {
				if recipe.Bench == bill.Bench() && recipe.Recipe == bill.Recipe() {
					spec.BenchKind = recipe.BenchKind
				}
			}
			out.Orders = append(out.Orders, spec)
		case "existing_work":
			for _, recipe := range recipes {
				if recipe.Product == Resource(objective.Def) && recipe.Existing {
					out.Orders = append(out.Orders, recipe.Standing...)
				}
			}
		case "ingredients_unknown":
			out.Unread(UnreadRecipes)
		}
	}
	return out
}
