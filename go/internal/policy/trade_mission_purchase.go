package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
	"slices"
)

// MissionSupplyDefinition contains decoded catalog facts for a settlement.
// Unknown compatibility or nutrition cannot authorize a food cargo choice.
type MissionSupplyDefinition struct {
	Definition string
	Prepared   domain.Fact[bool]
	Nutrition  domain.Fact[float64]
	CanSupply  domain.Fact[bool]
}

// TradeMissionCargo sizes saved authorization from the owning supply gaps.
func TradeMissionCargo(demands []SupplyDemandResult, compatible map[ResourceKey]domain.Fact[bool], definitions []MissionSupplyDefinition, foodTargetDays float64) ([]domain.CargoItem, []ResourceKey) {
	definitions = slices.Clone(definitions)
	slices.SortFunc(definitions, func(a, b MissionSupplyDefinition) int {
		if a.Definition < b.Definition {
			return -1
		}
		if a.Definition > b.Definition {
			return 1
		}
		return 0
	})
	var cargo []domain.CargoItem
	var goods []ResourceKey
	for _, d := range demands {
		can, known := compatible[d.Demand.Good].Value()
		if !foodNumber(d.Gap) || d.Gap <= 0 || !known || !can {
			continue
		}
		def := string(d.Demand.Good.Def)
		count := math.Ceil(d.Gap)
		if d.Demand.Good == NutritionKey {
			def = ""
			for _, row := range definitions {
				prepared, pk := row.Prepared.Value()
				nutrition, nk := row.Nutrition.Value()
				supply, sk := row.CanSupply.Value()
				if pk && prepared && nk && foodNumber(nutrition) && nutrition > 0 && sk && supply {
					def = row.Definition
					count = math.Ceil(d.Gap * math.Max(1, foodTargetDays) / nutrition)
					break
				}
			}
		}
		if def == "" || !foodNumber(count) || count <= 0 || count > math.MaxInt32 || slices.ContainsFunc(cargo, func(item domain.CargoItem) bool { return item.Definition == def }) {
			continue
		}
		cargo = append(cargo, domain.CargoItem{Definition: def, Count: uint64(count)})
		goods = append(goods, d.Demand.Good)
	}
	return cargo, goods
}

// TradeMissionBudget retains the shared colony silver floor before departure.
func TradeMissionBudget(silver domain.Fact[int64], colonists domain.Fact[int64]) domain.Fact[int64] {
	amount, known := silver.Value()
	reserve, rk := TradeSilverReserve(colonists)
	if !known || !rk {
		return domain.Unknown[int64]()
	}
	return domain.Known(min(max(0, amount-reserve), int64(math.MaxInt32)))
}

// MissionReturnFacts are the current native mass, provisions and exact home route.
type MissionReturnFacts struct {
	Mass, Capacity, FoodDays, RotDays domain.Fact[float64]
	RouteTicks                        domain.Fact[int64]
}

func TradeMissionReturnSafe(in MissionReturnFacts) bool {
	mass, mk := in.Mass.Value()
	capacity, ck := in.Capacity.Value()
	food, fk := in.FoodDays.Value()
	rot, rk := in.RotDays.Value()
	ticks, tk := in.RouteTicks.Value()
	if !mk || !ck || !fk || !rk || !tk || ticks < 0 || !foodNumber(mass) || !foodNumber(capacity) || !foodNumber(food) || !foodNumber(rot) || mass > capacity {
		return false
	}
	days := float64(ticks) / float64(domain.TicksPerDay)
	return food >= days && rot >= days
}

// MissionPurchaseRow is a live native offer with decoded catalog mass.
type MissionPurchaseRow struct {
	LineID, DefName    string
	TraderCount        int64
	Pawn, Currency     bool
	BuyPrice, UnitMass domain.Fact[float64]
	WillTrade          domain.Fact[bool]
}

type MissionPurchaseRequest struct {
	Mission        domain.TradeMission
	Targets        map[Resource]int64
	Stock          domain.Fact[[]Amount]
	Food           domain.Fact[FoodPlan]
	FoodTargetDays float64
	Nutrition      map[string]domain.Fact[float64]
	Rows           []MissionPurchaseRow
	Silver         domain.Fact[int64]
	Mass, Capacity domain.Fact[float64]
}

// PlanTradeMissionPurchases bounds live demand by saved authorization, silver and mass.
func PlanTradeMissionPurchases(in MissionPurchaseRequest) ([]domain.TradeLine, []domain.CargoItem, error) {
	m := in.Mission
	targets := in.Targets
	current := map[string]int64{}
	stocks, known := in.Stock.Value()
	var quantities []ResourceQuantity
	for _, stock := range stocks {
		quantities = append(quantities, ResourceQuantity{Key: ResourceKey{Def: stock.Resource}, Count: stock.Count})
	}
	var resourceTargets []ResourceDemand
	for def, target := range targets {
		resourceTargets = append(resourceTargets, ResourceDemand{Key: ResourceKey{Def: def}, Count: target, Priority: 1})
	}
	stockFact := domain.Unknown[[]ResourceQuantity]()
	if known {
		stockFact = domain.Known(quantities)
	}
	demand, err := BuildResourceDemand(ResourceDemandInput{Targets: resourceTargets, Stock: stockFact})
	if err != nil {
		return nil, nil, err
	}
	needs, known := demand.Value()
	if !known {
		return nil, nil, nil
	}
	for _, need := range needs {
		current[string(need.Key.Def)] = need.Count
	}
	foodWanted := 0.0
	if food, known := in.Food.Value(); known {
		foodWanted = math.Max(0, food.GapPerDay) * math.Max(1, in.FoodTargetDays)
	}
	var demands []SupplyDemand
	var candidates []SupplyCandidate
	rows := map[string]MissionPurchaseRow{}
	budget := float64(m.SilverBudget)
	mass, mk := in.Mass.Value()
	capacity, ck := in.Capacity.Value()
	if !mk || !ck || !foodNumber(mass) || !foodNumber(capacity) || mass > capacity {
		return nil, nil, nil
	}
	headroom := capacity - mass
	silver, sk := in.Silver.Value()
	if !sk {
		return nil, nil, nil
	}
	budget = math.Min(budget, float64(silver))
	wanted := map[string]int64{}
	for _, item := range m.Demand {
		count := min(int64(item.Count), current[item.Definition])
		if nutrition, known := in.Nutrition[item.Definition].Value(); known && nutrition > 0 {
			count = min(int64(item.Count), int64(math.Ceil(foodWanted/float64(nutrition))))
		}
		if count > 0 {
			wanted[item.Definition] = count
			demands = append(demands, SupplyDemand{Good: ResourceKey{Def: Resource(item.Definition)}, Units: count, Priority: 1})
		}
	}
	for _, row := range in.Rows {
		price, pk := row.BuyPrice.Value()
		willTrade, wk := row.WillTrade.Value()
		if wanted[row.DefName] == 0 || row.Pawn || row.Currency || !pk || price <= 0 || !foodNumber(price) || !wk || !willTrade {
			continue
		}
		count := min(wanted[row.DefName], row.TraderCount, int64(math.Floor(budget/price)))
		unitMass, known := row.UnitMass.Value()
		if !known || !foodNumber(float64(unitMass)) {
			continue
		}
		if unitMass > 0 {
			count = min(count, int64(math.Floor(headroom/float64(unitMass))))
		}
		if count <= 0 {
			continue
		}
		candidate, ok := TradeCandidate(Resource(row.DefName), row.LineID, count, price)
		if ok {
			candidates = append(candidates, candidate)
			rows[candidate.ID] = row
			budget -= float64(count) * price
			headroom -= float64(count) * float64(unitMass)
			wanted[row.DefName] -= count
		}
	}
	plan, err := PlanSupply(SupplyPlanRequest{Demands: domain.Known(demands), Candidates: domain.Known(candidates), Labor: domain.Known(float64(m.SilverBudget) * tradeLaborPerSilver)})
	if err != nil {
		return nil, nil, err
	}
	var lines []domain.TradeLine
	goods := map[string]uint64{}
	for _, entry := range plan.Portfolio {
		if entry.Decision != SupplyOpen || entry.Wanted <= 0 {
			continue
		}
		row := rows[entry.Candidate.ID]
		lines = append(lines, domain.TradeLine{LineID: row.LineID, AbsoluteCount: int32(entry.Wanted)})
		goods[row.DefName] += uint64(entry.Wanted)
	}
	var cargo []domain.CargoItem
	for def, count := range goods {
		cargo = append(cargo, domain.CargoItem{Definition: def, Count: count})
	}
	slices.SortFunc(cargo, func(a, b domain.CargoItem) int {
		if a.Definition < b.Definition {
			return -1
		}
		if a.Definition > b.Definition {
			return 1
		}
		return 0
	})
	return lines, cargo, nil
}

// TradeMissionPaymentSafe rechecks the staged deal against saved authorization.
func TradeMissionPaymentSafe(budget int64, silver domain.Fact[int64], balance domain.Fact[float64]) bool {
	amount, sk := silver.Value()
	charge, bk := balance.Value()
	return sk && bk && foodNumber(-charge) && -charge <= float64(budget) && float64(amount)+charge >= 0
}
