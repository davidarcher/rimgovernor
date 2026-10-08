package buildingruntime

import (
	"context"
	"fmt"
	"sort"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// admitDecree drives an already accepted decree through existing bill, zone and
// acquisition intents. The quest's native objective progress ends the work.
func (r *RoundsPopulationJoinerPlanner) admitDecree(call, epoch context.Context, state ControlState, goal store.StandardState, review store.Rounds, read observation.RoundsReading, arbiter *stepArbiter, started time.Time) (RoundsPopulationJoinerResult, bool, error) {
	f := read.Projection.Facts
	offers, known := f.QuestOffers.Value()
	if !known {
		return RoundsPopulationJoinerResult{}, false, nil
	}
	sort.Slice(offers, func(i, j int) bool { return offers[i].Quest < offers[j].Quest })
	for _, offer := range offers {
		objective, remaining, owed := policy.DecreeObjective(offer)
		if !owed {
			continue
		}
		if player, known := offer.AskerFactionPlayer.Value(); !known || !player {
			r.decreeSkip(call, offer, "asker_faction")
			continue
		}
		if deadline, known := objective.DeadlineTicks.Value(); known && deadline <= int64(read.Projection.Identity.Tick) {
			r.decreeSkip(call, offer, "deadline")
			continue
		}
		id := domain.MintPlanID()
		snapshot := state.Snapshot
		snapshot.Plan = id
		snapshot.Revision = 1
		var actions []domain.Action
		var previews []policy.Preview
		reason := ""
		switch objective.Kind {
		case o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_PRODUCE_ITEM:
			if r.reviewer.resourceNative == nil || read.Frame.Catalog == nil {
				reason = "production_unknown"
				break
			}
			census, _, err := r.reviewer.resourceNative.ReadGearBenches(call, boundary.Identity(state.Snapshot))
			if err != nil {
				return RoundsPopulationJoinerResult{}, false, err
			}
			recipes := decreeRecipes(census, read.Projection, read.Frame.Catalog, objective)
			bill, why := policy.PlanDecreeProduction(objective, remaining, recipes, f.Resources)
			reason = why
			if reason == "" {
				action, err := domain.NewProductionBillAction(domain.ActionID(fmt.Sprintf("%s-0", id)), bill)
				if err != nil {
					return RoundsPopulationJoinerResult{}, false, err
				}
				actions = append(actions, action)
			}
		case o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_HARVEST_PLANT:
			field := r.reviewer.newResourceFieldPlanner(call, state, review, read.Projection.Identity, read.Projection)
			site, sited, err := field.siteOf()
			if err != nil {
				return RoundsPopulationJoinerResult{}, false, err
			}
			if !sited {
				reason = "plant_site_unknown"
				break
			}
			standing := int64(0)
			for _, farm := range read.Projection.Farms {
				if farm.Crop == objective.Def {
					if cells, known := farm.UsableCells.Value(); known {
						standing += int64(cells)
					}
				}
			}
			plan, why := policy.PlanDecreeHarvest(objective, remaining, standing, field.choices, field.climate, site, field.skill)
			reason = why
			if reason != "" {
				break
			}
			if r.reviewer.resourceNative == nil {
				reason = "plant_site_unknown"
				break
			}
			for i, patch := range plan.Sites.Patches {
				var cells []domain.Cell
				for x := patch.X; x < patch.X+patch.Width; x++ {
					for z := patch.Z; z < patch.Z+patch.Height; z++ {
						cells = append(cells, domain.Cell{X: x, Z: z})
					}
				}
				zone, err := domain.NewZoneCreate(domain.GrowingZone, objective.Def, cells)
				if err != nil {
					return RoundsPopulationJoinerResult{}, false, err
				}
				if reply, why, err := previewZone(call, r.reviewer.resourceNative, boundary.Identity(snapshot), zone); err != nil {
					return RoundsPopulationJoinerResult{}, false, err
				} else if why != "" {
					reason = "plant_site_refused"
					break
				} else if _, err = boundary.Context(reply.GetEvaluated().GetContext(), snapshot); err != nil {
					return RoundsPopulationJoinerResult{}, false, err
				}
				action, err := domain.NewZoneCreateAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), zone)
				if err != nil {
					return RoundsPopulationJoinerResult{}, false, err
				}
				actions = append(actions, action)
				previews = append(previews, policy.Preview{Action: action, Snapshot: snapshot, Tick: read.Projection.Identity.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(cells), Costs: domain.Known([]policy.Amount{})})
			}
		case o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_KILL_ANIMALS:
			prey, why := policy.PlanDecreeHunt(objective, remaining, offer.ViolentQuestsAllowed, read.Projection.Acquisition)
			reason = why
			for i, source := range prey {
				acquire, err := domain.NewAcquisition(source.ID, source.Resource, source.Cell)
				if err != nil {
					return RoundsPopulationJoinerResult{}, false, err
				}
				action, err := domain.NewAcquisitionAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), acquire)
				if err != nil {
					return RoundsPopulationJoinerResult{}, false, err
				}
				actions = append(actions, action)
			}
		}
		if reason == "existing_work" {
			return RoundsPopulationJoinerResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: stockWaitTicks}, true, nil
		}
		if reason != "" {
			r.decreeSkip(call, offer, reason)
			continue
		}
		if len(actions) == 0 {
			continue
		}
		prefix := fmt.Sprintf("decree-%s-%d-%d-", offer.Quest, objective.Kind, remaining)
		attempt := medicalAttemptCount(goal.History, goal.Standard.Episode, prefix)
		if attempt >= maxMedicalAttemptsPerPatient {
			return RoundsPopulationJoinerResult{Verdict: refuse(RefusalRetriesSpent, "decree_attempts", "")}, true, nil
		}
		method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
		claims := []string{}
		for _, action := range actions {
			if bill, ok := action.ProductionBill(); ok {
				claims = append(claims, "bench:"+bill.Bench())
			}
			if acquisition, ok := action.Acquisition(); ok {
				claims = append(claims, "source:"+acquisition.Thing())
			}
		}
		if !arbiter.tryClaim(nil, claims...) {
			return RoundsPopulationJoinerResult{Verdict: waitFor(WaitClaim, "decree_work")}, true, nil
		}
		plan, err := domain.NewPlan(id, 1, actions)
		if err != nil {
			return RoundsPopulationJoinerResult{}, false, err
		}
		if err = r.reviewer.player.current(call, epoch); err != nil {
			return RoundsPopulationJoinerResult{}, false, err
		}
		elapsed := r.reviewer.clock.Now().Sub(started)
		if r.reviewer.player.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
			return RoundsPopulationJoinerResult{}, false, ErrControl
		}
		if len(previews) > 0 {
			decision, err := admitMethod(call, r.reviewer.player.journal, store.BuildingMethodRequest{Owner: goal, Method: method, Plan: plan, Current: snapshot, Tick: read.Projection.Identity.Tick, Bounds: domain.Known(read.Projection.Bounds), Stock: policy.StockObservation{Snapshot: snapshot, Tick: read.Projection.Identity.Tick}, Previews: previews, Purpose: policy.Rounds})
			if err != nil {
				return RoundsPopulationJoinerResult{}, false, err
			}
			if !decision.Admitted {
				return RoundsPopulationJoinerResult{Verdict: admissionRefused(decision)}, true, nil
			}
		} else if _, err = r.reviewer.player.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
			return RoundsPopulationJoinerResult{}, false, err
		}
		return RoundsPopulationJoinerResult{Verdict: BuildingReasonAdmitted, Plan: id, NativeWorkTicks: stockWaitTicks}, true, nil
	}
	return RoundsPopulationJoinerResult{}, false, nil
}

func (r *RoundsPopulationJoinerPlanner) decreeSkip(ctx context.Context, offer policy.JoinerOffer, reason string) {
	r.reviewer.logQuestSkip(ctx, policy.QuestSkip{Quest: offer.Quest, ScriptDef: offer.ScriptDef, Reason: policy.QuestSkipReason(reason)})
}

func decreeRecipes(census []bridge.GearBenchRead, projection observation.ColonyProjection, catalog *bridge.DefinitionCatalog, objective policy.QuestObjective) []policy.DecreeRecipe {
	var out []policy.DecreeRecipe
	benches, _ := projection.ProductionBenches.Value()
	for _, bench := range census {
		recipes, known := bench.Bench.Recipes.Value()
		if !known {
			continue
		}
		for _, recipe := range recipes {
			row := bridge.DefRow[*d.RecipeDef](catalog, recipe.Definition)
			if row == nil {
				continue
			}
			units := int64(0)
			for _, product := range row.GetProducts() {
				if product.GetValue().GetThingDef() == objective.Def {
					units += int64(product.GetValue().GetCount())
				}
			}
			if units <= 0 {
				continue
			}
			available, ak := recipe.Available.Value()
			on, ok := recipe.AvailableOn.Value()
			candidate := policy.DecreeRecipe{Bench: bench.Bench.ID, Recipe: recipe.Definition, Product: policy.Resource(objective.Def), Units: units, Ingredients: recipe.Ingredients, Stuff: map[policy.Resource]bool{}}
			if ak && ok {
				candidate.Available = domain.Known(available && on)
			}
			groups, _ := recipe.Ingredients.Value()
			for _, group := range groups {
				for _, alternative := range group {
					def := bridge.DefRow[*d.ThingDef](catalog, string(alternative.Resource))
					if def != nil && def.GetStuffProps() != nil && row.GetProductHasIngredientStuff() {
						candidate.Stuff[alternative.Resource] = true
					}
				}
			}
			detailed := false
			for _, observed := range benches {
				if observed.ID != bench.Bench.ID {
					continue
				}
				for _, bill := range observed.Bills {
					if bill.Recipe != recipe.Definition {
						continue
					}
					detailed = true
					active, known := bill.Active.Value()
					if !known {
						candidate.Available = domain.Unknown[bool]()
						continue
					}
					if !active {
						candidate.Replace = bill.ID
						continue
					}
					mode, mk := bill.RepeatMode.Value()
					filter, fk := bill.Ingredients.Value()
					if mk && mode == "Count" && (objective.Stuff == "" || fk && containsString(filter, objective.Stuff)) {
						candidate.Existing = true
					} else {
						candidate.Available = domain.Known(false)
					}
				}
			}
			if !detailed {
				candidate.Existing = decreeCensusWork(bench.Bench, recipe.Definition)
			}
			out = append(out, candidate)
		}
	}
	return out
}

// Missing specialized bill detail must not turn an active generic bill into a
// second production order. Native objective progress decides when it finishes.
func decreeCensusWork(bench policy.GearBench, recipe string) bool {
	rows, known := bench.Bills.Value()
	if !known {
		return false
	}
	for _, bill := range rows {
		active, known := bill.Active.Value()
		if bill.Recipe == recipe && known && active {
			return true
		}
	}
	return false
}

func containsString(values []string, want string) bool {
	for _, value := range values {
		if value == want {
			return true
		}
	}
	return false
}
