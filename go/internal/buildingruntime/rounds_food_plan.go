package buildingruntime

import (
	"context"
	"fmt"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// planFood retains one complete review per observed tick and invalidation
// generation, independently of additional definition/room reads by planners.
// It also marks the tradeable caravans the offer book has no fresh record of
// as Unpriced, so the always-browse phase reads their sheets.
func (r *Rounder) planFood(p *observation.ColonyProjection) {
	p.Facts.FoodPlan = r.foodPlan(*p)
	r.markUnpriced(p)
}

func (r *Rounder) foodPlan(p observation.ColonyProjection) domain.Fact[policy.FoodPlan] {
	s := &r.census
	s.mu.Lock()
	defer s.mu.Unlock()
	seasonal := r.seasonal(p.Facts)
	trade := r.foodTrade(p)
	if _, known := s.foodPlan.Value(); known && s.foodGeneration == s.generation && s.foodOffers == trade.revision &&
		s.foodMin == seasonal.FoodMinDays && s.foodTarget == seasonal.FoodTargetDays && sameObservedIdentity(s.foodIdentity, p.Identity) {
		return s.foodPlan
	}
	plan := reviewFoodPlan(p, r.policy, &r.foodCredit, &r.huntAdmission, trade)
	if v, known := plan.Value(); known {
		r.huntAdmission.Admit(policy.HuntRequest(domain.Known(v)))
	}
	logFoodCredit(r.foodCredit.Drain())
	if v, known := plan.Value(); known && r.foodGapZero {
		v.GapPerDay = 0
		plan = domain.Known(v)
	}
	if _, known := plan.Value(); known {
		s.foodIdentity, s.foodPlan, s.foodGeneration = p.Identity, plan, s.generation
		s.foodMin, s.foodTarget, s.foodOffers = seasonal.FoodMinDays, seasonal.FoodTargetDays, trade.revision
	}
	return plan
}

// foodTrade is what the plan may buy: the fresh priced offers of the traders
// on the census and the silver above the colony's reserve. Without a known
// trader census, silver or colonist count there are none.
type foodTrade struct {
	offers          []policy.TradeOffers
	silver, reserve int64
	// revision is the offer book's, so a new record rebuilds the plan.
	revision uint64
}

func (r *Rounder) foodTrade(p observation.ColonyProjection) foodTrade {
	traders, tk := p.Facts.Traders.Value()
	silver, sk := p.Facts.Silver().Value()
	reserve, rk := policy.TradeSilverReserve(p.Facts.Colonists)
	var out foodTrade
	if tk && sk && rk {
		world := domain.GenerationSnapshot{Colony: p.Identity.Colony, Map: p.Identity.Map, Load: p.Identity.Load}
		out.offers, out.silver, out.reserve = r.tradeOffers.fresh(world, p.Identity.Tick, silver, traders), silver, reserve
	}
	out.revision = r.tradeOffers.version()
	return out
}

// markUnpriced flags each tradeable caravan on the census without a fresh
// offer record. With the colony's silver unknown no record can be judged
// fresh, so nothing is flagged.
func (r *Rounder) markUnpriced(p *observation.ColonyProjection) {
	traders, tk := p.Facts.Traders.Value()
	silver, sk := p.Facts.Silver().Value()
	if !tk || !sk {
		return
	}
	world := domain.GenerationSnapshot{Colony: p.Identity.Colony, Map: p.Identity.Map, Load: p.Identity.Load}
	priced := map[string]bool{}
	for _, o := range r.tradeOffers.fresh(world, p.Identity.Tick, silver, traders) {
		priced[o.Trader] = true
	}
	marked := slices.Clone(traders)
	for i := range marked {
		marked[i].Unpriced = marked[i].CanTrade && !priced[marked[i].ID]
	}
	p.Facts.Traders = domain.Known(marked)
}

// reviewFoodPlan budgets the complete competing-consumer census. It is called
// by the rounds, before its reading is retained for method planners.
// A missing census never becomes an empty portfolio that certifies surplus.
func reviewFoodPlan(p observation.ColonyProjection, thresholds policy.RoundsPolicy, credit *policy.DeliveryCredit, hunt *policy.HuntAdmission, trade foodTrade) domain.Fact[policy.FoodPlan] {
	supply, sk := p.CombinedFoodSupply.Value()
	sources, ak := p.Acquisition.Value()
	if !sk || !ak {
		return domain.Unknown[policy.FoodPlan]()
	}
	workers, wk := p.Workers.Value()
	if pawns, pk := p.WorkPawns.Value(); pk {
		workers, wk = policy.RoundsWorkers(pawns).Value()
	}
	if !wk {
		return domain.Unknown[policy.FoodPlan]()
	}
	forecast, err := policy.ForecastFood(supply, nil)
	if err != nil {
		return domain.Unknown[policy.FoodPlan]()
	}
	if human, hk := p.FoodSupply.Value(); hk {
		forecast = forecast.GateOnColonists(foodConsumerIDs(human), thresholds.Seasonal(p.Facts.Calendar, p.Facts.DisasterConditions).FoodMinDays)
	}
	var gunners, hunters int
	if pawns, known := p.WorkPawns.Value(); known {
		profiles := policy.Profiles(pawns)
		gunners, hunters = policy.SquadGunners(profiles), policy.Hunters(profiles)
	}
	var weatherAccuracy domain.Fact[float64]
	if env, known := p.Environment.Value(); known {
		weatherAccuracy = env.WeatherAccuracy
	}
	channels := append(policy.ForageChannels(sources), policy.HuntCandidates(sources, gunners, weatherAccuracy)...)
	channels = append(channels, policy.HuntPrerequisiteCandidates(p.HuntHolds)...)
	hunting := foodHuntInput(p)
	channels = policy.HuntThroughput(channels, hunters, policy.HuntCadence(hunting.kills, hunters, int64(p.Identity.Tick)))
	if benches, bk := p.ProductionBenches.Value(); bk {
		if human, hk := p.FoodSupply.Value(); hk {
			var ids []policy.PawnID
			for _, c := range human.Consumers {
				ids = append(ids, c.ID)
			}
			if channel, ok := policy.HumanFoodChannel(benches, supply, ids, thresholds.Seasonal(p.Facts.Calendar, p.Facts.DisasterConditions).FoodTargetDays, p.Facts.IdeologyRead()); ok {
				channels = append(channels, channel)
			}
		}
	}
	if census, known := p.FoodChannels.Value(); known {
		if water, known := census.FishableWater.Value(); known {
			request := policy.FishingRequest{Researched: water.FishingResearched, ResearchLeadDays: water.ResearchLeadDays}
			for _, region := range water.Regions {
				request.Regions = append(request.Regions, policy.FishingRegion{ID: policy.FishingRegionID(region.Root), Population: region.Population, MaxPopulation: region.MaxPopulation,
					NutritionPerFish: region.NutritionPerFish, FishPerBatch: region.FishPerBatch, WorkTicksPerBatch: region.WorkTicksPerBatch, PawnFishWorkCapacity: region.PawnFishWorkCapacity, Reachable: region.Reachable, Frozen: region.Frozen, Designated: region.Delivering, Source: fmt.Sprintf("fish:%d,%d", region.Root.X, region.Root.Z), DistanceSquared: region.DistanceSquared})
			}
			fishing, err := policy.FishingChannels(request)
			if err != nil {
				return domain.Unknown[policy.FoodPlan]()
			}
			channels = append(channels, fishing...)
		}
	}
	if animals, known := p.FoodChannels.Value(); known {
		channels = append(channels, policy.AnimalProductChannels(policy.WithFeed(animals.AnimalProducts(), p.Facts.AnimalUpkeep.Animals))...)
	}
	channels = append(channels, policy.StockIngredientChannels(supply)...)
	if fields, known := p.FoodFields.Value(); known {
		channels = append(channels, policy.CropChannels(fields, policy.CropKitchen{Benches: p.ProductionBenches, Cooks: domain.Known(float64(workers))},
			policy.CropSeason{Calendar: p.Facts.Calendar, Conditions: p.Facts.DisasterConditions})...)
	}
	// A field not yet sown is a candidate per viable crop, opened by the plan
	// like any channel; the field executor acts on the ones it opens.
	field, _ := fieldRequest(p, thresholds.Seasonal(p.Facts.Calendar, p.Facts.DisasterConditions).FoodTargetDays)
	channels = append(channels, policy.NewFieldChannels(field, policy.CropKitchen{Benches: p.ProductionBenches, Cooks: domain.Known(float64(workers))})...)
	// Capacity and stock protection do not create nutrition by themselves.
	// Zero-contribution Hold rows leave these supporting methods to their own
	// observed preconditions; their existing admission owns labor and resources.
	for _, support := range []struct {
		kind policy.CandidateKind
		id   string
	}{
		{policy.CandidateCook, "cooking-capacity"}, {policy.CandidateReserve, "stock-protection"},
	} {
		channels = append(channels, policy.FoodSupportCandidate(support.kind, support.id))
	}
	channels = hunt.Apply(channels)
	channels = credit.Apply(channels, foodCreditInput(p, credit, hunting))
	// Work capacity is a planning budget, not a promise of pawn work. Eight
	// hours per available worker leaves the rest of the day for sleep and needs.
	seasonal := thresholds.Seasonal(p.Facts.Calendar, p.Facts.DisasterConditions)
	plan, err := policy.SupplyFoodPlan(policy.FoodPlanRequest{Demand: forecast,
		MinDays: seasonal.FoodMinDays, TargetDays: seasonal.FoodTargetDays, EmergencyDays: seasonal.FootholdFoodDays,
		Channels: domain.Known(channels), Labor: domain.Known(float64(workers) * 20000)})
	if err != nil {
		return domain.Unknown[policy.FoodPlan]()
	}
	credit.Opened(plan, channels, p.Identity.Tick)
	// A food slaughter offer protects productive animals selected
	// by the non-destructive portfolio before adding a single removal method.
	// A present caravan's priced food is a one-shot candidate beside it, sized
	// to the gap over the plan's window; the ranker opens it or not.
	if plan.GapPerDay > 0 {
		var offers []policy.SupplyCandidate
		if animals, known := p.FoodChannels.Value(); known {
			offers = policy.SlaughterFoodChannels(animals.Slaughter, p.Facts.AnimalUpkeep.Animals, herdPolicyOf(p.Facts, plan))
		}
		offers = append(offers, policy.TradeFoodChannels(trade.offers, trade.silver, trade.reserve, plan.GapPerDay*plan.HorizonDays)...)
		// Animals the colony could acquire: a wild one to tame, a trader's to buy.
		acquire := policy.AnimalAcquisition{Races: p.Facts.AnimalUpkeep.AnimalRaces, Owned: p.Facts.AnimalUpkeep.Animals, Wild: p.Facts.AnimalUpkeep.WildAnimals, Herd: herdPolicyOf(p.Facts, plan), Handlers: p.Facts.WorkProfiles}
		offers = append(offers, policy.TameFoodChannels(acquire)...)
		offers = append(offers, policy.AnimalPurchaseFoodChannels(trade.offers, acquire, trade.silver, trade.reserve)...)
		if len(offers) > 0 {
			channels = append(channels, offers...)
			plan, err = policy.SupplyFoodPlan(policy.FoodPlanRequest{Demand: forecast, MinDays: seasonal.FoodMinDays, TargetDays: seasonal.FoodTargetDays, EmergencyDays: seasonal.FootholdFoodDays, Channels: domain.Known(channels), Labor: domain.Known(float64(workers) * 20000)})
			if err != nil {
				return domain.Unknown[policy.FoodPlan]()
			}
		}
	}
	herd := herdPolicyOf(p.Facts, plan)
	for i := range plan.Portfolio {
		e := &plan.Portfolio[i]
		if e.Channel.Kind == policy.CandidateAnimalProduct {
			floor := herd.PopulationMin[policy.Resource(e.Channel.ID)]
			e.Terms = append(e.Terms, policy.CandidateTerm{Name: "effective_herd_floor", Value: float64(floor)})
			e.Reason += fmt.Sprintf("; MaintainHerd-%s floor %d", e.Channel.ID, floor)
		}
	}
	return domain.Known(plan)
}

// huntInput is the ledger's kills and butchers as the hunt credit reads them.
type huntInput struct {
	kills    []policy.HuntKill
	butchers []policy.HuntButcher
}

func foodHuntInput(p observation.ColonyProjection) huntInput {
	var in huntInput
	if ledger, known := p.DeliveryLedger.Value(); known {
		for _, k := range ledger.Kills {
			in.kills = append(in.kills, policy.HuntKill{CorpseID: k.CorpseID, Potential: k.PotentialNutrition, Tick: k.Tick})
		}
		for _, b := range ledger.Butchers {
			in.butchers = append(in.butchers, policy.HuntButcher{CorpseID: b.CorpseID, MeatNutrition: b.MeatNutrition})
		}
	}
	return in
}

// foodCreditInput is the review's delivery ledger as the credit tracker reads
// it: cumulative nutrition per counter group, keyed "<kind>:<source id>" as the
// channel builders name their Source from their own census. Hunting is the
// kill and butcher records credited per corpse (policy.HuntDelivered).
func foodCreditInput(p observation.ColonyProjection, credit *policy.DeliveryCredit, hunting huntInput) policy.CreditInput {
	ledger, known := p.DeliveryLedger.Value()
	in := policy.CreditInput{Tick: p.Identity.Tick, Known: known}
	if !known {
		return in
	}
	in.LoadToken = ledger.LoadToken
	in.Delivered = map[string]float64{}
	for key, count := range ledger.Counts {
		in.Delivered[string(key.Kind)+":"+key.SourceID] += count.Nutrition
	}
	var stocks []policy.FoodStock
	complete := false
	if supply, ok := p.CombinedFoodSupply.Value(); ok {
		stocks, complete = supply.Stocks, supply.Complete == domain.Known(true)
	}
	in.Delivered[policy.HuntSource] = credit.HuntDelivered(hunting.kills, hunting.butchers, stocks, complete)
	return in
}

// logFoodCredit writes one food_credit row per factor move, state change or
// held factor.
func logFoodCredit(changes []policy.CreditChange) {
	for _, c := range changes {
		telemetry.Decide(context.Background(), foodCreditDecision(c))
	}
}

func foodCreditDecision(c policy.CreditChange) telemetry.Decision {
	return telemetry.Decision{Kind: "food_credit", Component: "routine", Verdict: "credited", Reason: c.Reason, Target: c.Source,
		Attrs: map[string]any{"expected": c.Expected, "observed": c.Observed, "factor": c.Factor, "window_days": c.WindowDays, "state": c.State}}
}

func foodPlanSupport(p domain.Fact[policy.FoodPlan], kind policy.CandidateKind, id string) bool {
	return policy.FoodPlanSupport(p, kind, id)
}

// foodPlanFieldRoom is whether the plan opened a new-field candidate that the
// open field work does not already cover. Pending zone creates and add-cells
// are designated candidates with the full grow days as lead: their nutrition
// per day (priced as the plan prices a candidate) is taken off the gap, and
// completed zones are already in the native field census. Infrastructure
// without a known crop yield keeps the work barrier rather than guessing its
// future production.
func foodPlanFieldRoom(p observation.ColonyProjection, plans []store.PlanState) bool {
	plan, known := p.Facts.FoodPlan.Value()
	if !known || plan.GapPerDay <= 0 || !foodPlanOpensField(plan) {
		return false
	}
	gap := plan.GapPerDay
	for _, existing := range plans {
		for _, progress := range existing.Progress {
			if !domain.StandardWorkOpen([]domain.Progress{progress}) {
				continue
			}
			crop, cells := "", 0
			if zone, ok := progress.Action().ZoneCreate(); ok {
				crop, cells = zone.Crop(), len(zone.Cells())
			} else if edit, ok := progress.Action().ZoneCellEdit(); ok && edit.Mode() == domain.AddZoneCells {
				// A field block grown by add-cells yields its zone's crop.
				for _, farm := range p.Farms {
					if farm.ID == edit.Zone() {
						crop = farm.Crop
					}
				}
				if crop == "" {
					continue
				}
				cells = len(edit.Cells())
			} else {
				if b, building := progress.Action().Building(); building && b.Definition() != "TableButcher" {
					return false
				}
				continue
			}
			found := false
			for _, d := range p.Definitions {
				if d.Name != crop {
					continue
				}
				yield, yk := d.HarvestNutrition.Value()
				days, dk := d.GrowDays.Value()
				if !yk || !dk || yield < 0 || days <= 0 {
					return false
				}
				gap -= yield * float64(cells) / days
				found = true
				break
			}
			if !found {
				return false
			}
		}
	}
	return gap > 0
}

// foodPlanOpensField is whether the plan opened a new-field candidate.
func foodPlanOpensField(plan policy.FoodPlan) bool {
	for _, entry := range plan.Portfolio {
		if entry.Channel.Kind == policy.CandidateCrop && strings.HasPrefix(entry.Channel.ID, policy.NewFieldPrefix) && entry.Decision == policy.FoodPlanOpen {
			return true
		}
	}
	return false
}

func foodConsumerIDs(supply policy.FoodSupply) []policy.PawnID {
	ids := make([]policy.PawnID, 0, len(supply.Consumers))
	for _, c := range supply.Consumers {
		ids = append(ids, c.ID)
	}
	return ids
}
