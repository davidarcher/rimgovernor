package buildingruntime

import (
	"context"
	"slices"
	"sort"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

type tradeAcquisitionMemory struct {
	mu        sync.Mutex
	world     domain.GenerationSnapshot
	proposals map[string]policy.TradeAcquisitionPlan
	active    map[string]string
	missions  map[string]domain.TradeMission
}

// TradeAcquisitionPlan is derived supply intent for the food/resources owner.
// Executors must claim before creating shared Hands work and release only
// after reconciling its outcome; saved/native evidence owns restart recovery.
func (r *Rounder) TradeAcquisitionPlan(owner string) policy.TradeAcquisitionPlan {
	m := &r.tradeAcquisition
	m.mu.Lock()
	defer m.mu.Unlock()
	plan := m.proposals[owner]
	plan.Decisions = slices.Clone(plan.Decisions)
	if plan.Proposal != nil {
		own := *plan.Proposal
		own.Option.Compatible = slices.Clone(own.Option.Compatible)
		plan.Proposal = &own
	}
	return plan
}
func (r *Rounder) ClaimTradeAcquisition(owner, id string) bool {
	m := &r.tradeAcquisition
	m.mu.Lock()
	defer m.mu.Unlock()
	plan := m.proposals[owner]
	if plan.Proposal == nil || plan.Proposal.Option.ID != id || len(m.active) != 0 {
		return false
	}
	m.active[owner] = id
	return true
}
func (r *Rounder) ReleaseTradeAcquisition(owner, id string) {
	m := &r.tradeAcquisition
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.active[owner] == id {
		delete(m.active, owner)
	}
}

func (r *Rounder) planFoodAcquisition(ctx context.Context, world domain.GenerationSnapshot, p observation.ColonyProjection) {
	var demands []policy.SupplyDemandResult
	if plan, known := p.Facts.FoodPlan.Value(); known {
		thresholds := r.seasonal(p.Facts)
		d, err := policy.NutritionDemand(policy.NutritionDemandInput{Forecast: plan.Forecast, MinDays: thresholds.FoodMinDays, TargetDays: thresholds.FoodTargetDays})
		if err == nil {
			demands = append(demands, policy.SupplyDemandResult{Demand: d, Gap: plan.GapPerDay, Delivered: plan.DeliveredPerDay})
		}
	}
	r.planTradeAcquisition(ctx, world, "food", p, demands, nil)
}

// planTradeAcquisition runs inside the existing supply owner. Trips are exact
// native pack/crew options supplied by the mission planner, not guessed crews.
func (r *Rounder) planTradeAcquisition(ctx context.Context, world domain.GenerationSnapshot, owner string, p observation.ColonyProjection, demands []policy.SupplyDemandResult, trips []policy.TradeAcquisitionOption) {
	m := &r.tradeAcquisition
	m.mu.Lock()
	if m.world != world {
		m.world = world
		m.proposals = map[string]policy.TradeAcquisitionPlan{}
		m.active = map[string]string{}
		m.missions = map[string]domain.TradeMission{}
	}
	active := ""
	for _, id := range m.active {
		active = id
		break
	}
	m.mu.Unlock()
	options := slices.Clone(trips)
	source, ok := r.native.(interface {
		ReadTradeAcquisition(context.Context, *c.Identity, *op.FormCaravanIntent) (*o.TradeAcquisition, bridge.Result, error)
		observation.DefinitionSource
	})
	unmet := slices.ContainsFunc(demands, func(d policy.SupplyDemandResult) bool { return d.Gap > 0 })
	if ok && unmet {
		identity := boundary.Identity(world)
		facts, _, err := source.ReadTradeAcquisition(ctx, identity, nil)
		if err == nil && facts != nil {
			if len(facts.Arrivals) != 0 || len(facts.CommsWork) != 0 {
				active = "native_pending"
			}
			catalog, err := source.DefinitionCatalog(ctx, identity)
			if err == nil && catalog != nil {
				options = append(options, requestAcquisitionOptions(facts, catalog, demands)...)
				options = append(options, r.settlementAcquisitionOptions(ctx, world, owner, p, demands, catalog)...)
			}
		}
	}
	reserve, known := policy.TradeSilverReserve(p.Facts.Colonists)
	rf := domain.Unknown[int64]()
	if known {
		rf = domain.Known(reserve)
	}
	plan := policy.PlanTradeAcquisition(policy.TradeAcquisitionRequest{Demands: demands, Options: options, Silver: p.Facts.Silver(), Reserve: rf, ActiveID: active})
	m.mu.Lock()
	m.proposals[owner] = plan
	m.mu.Unlock()
}

func requestAcquisitionOptions(facts *o.TradeAcquisition, catalog *bridge.DefinitionCatalog, demands []policy.SupplyDemandResult) []policy.TradeAcquisitionOption {
	var options []policy.TradeAcquisitionOption
	consoles := slices.Clone(facts.Consoles)
	sort.Slice(consoles, func(i, j int) bool { return consoles[i].GetId() < consoles[j].GetId() })
	for _, row := range facts.Requests {
		option := policy.TradeAcquisitionOption{ID: row.GetFactionId() + "/" + row.Kind.String() + "/" + row.GetTraderKind(), Faction: row.GetFactionId(), TraderKind: row.GetTraderKind(), LastRequestTick: row.GetLastRequestTick(), PassingShips: len(facts.PassingShips), LaborTicks: domain.Known(0.0)}
		if row.Kind == c.TradeRequestKind_TRADE_REQUEST_KIND_ORBITAL {
			option.Kind = policy.TradeAcquireOrbital
		} else {
			option.Kind = policy.TradeAcquireCaravan
		}
		if row.Eligible != nil {
			option.Eligible = domain.Known(row.GetEligible())
		}
		if row.ArrivalMaxTicks != nil {
			option.LeadDays = domain.Known(float64(row.GetArrivalMaxTicks()) / float64(domain.TicksPerDay))
		}
		if row.GoodwillCost != nil {
			option.GoodwillCost = domain.Known(row.GetGoodwillCost())
		}
		if row.RelationAfterPayment != nil {
			option.RelationAfterPayment = domain.Known(row.GetRelationAfterPayment())
		}
		if row.CooldownRemainingTicks != nil {
			option.CooldownTicks = domain.Known(row.GetCooldownRemainingTicks())
		}
		if facts.OrbitalAvailable != nil {
			option.OrbitalAvailable = domain.Known(facts.GetOrbitalAvailable())
		}
		negotiators := slices.Clone(row.NegotiatorIds)
		sort.Strings(negotiators)
		for _, console := range consoles {
			for _, pawn := range negotiators {
				if slices.Contains(console.NegotiatorIds, pawn) {
					option.Console, option.Negotiator = console.GetId(), pawn
					break
				}
			}
			if option.Console != "" {
				break
			}
		}
		for _, queued := range facts.Arrivals {
			if queued.GetFactionId() == row.GetFactionId() && queued.Kind == row.Kind {
				option.Eligible = domain.Known(false)
			}
		}
		for _, work := range facts.CommsWork {
			if work.GetFactionId() == row.GetFactionId() {
				option.Eligible = domain.Known(false)
			}
		}
		for _, demand := range demands {
			if compatible, known := catalog.TraderKindCanSupply(option.TraderKind, demand.Demand.Good).Value(); known && compatible {
				option.Compatible = append(option.Compatible, demand.Demand.Good)
			}
		}
		options = append(options, option)
	}
	return options
}
