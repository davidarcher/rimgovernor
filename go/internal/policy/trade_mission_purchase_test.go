package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
	"reflect"
	"testing"
)

func TestMissionCargoUsesDemandNutritionAndCompatibility(t *testing.T) {
	steel := ResourceKey{Def: "Steel"}
	demands := []SupplyDemandResult{{Demand: SupplyDemand{Good: NutritionKey}, Gap: 1.1}, {Demand: SupplyDemand{Good: steel}, Gap: 2.2}}
	compatible := map[ResourceKey]domain.Fact[bool]{NutritionKey: domain.Known(true), steel: domain.Known(true)}
	definitions := []MissionSupplyDefinition{{Definition: "ZMeal", Prepared: domain.Known(true), Nutrition: domain.Known(1.0), CanSupply: domain.Known(true)}, {Definition: "AMeal", Prepared: domain.Known(true), Nutrition: domain.Known(0.5), CanSupply: domain.Known(true)}}
	cargo, goods := TradeMissionCargo(demands, compatible, definitions, 2)
	if !reflect.DeepEqual(cargo, []domain.CargoItem{{Definition: "AMeal", Count: 5}, {Definition: "Steel", Count: 3}}) || !reflect.DeepEqual(goods, []ResourceKey{NutritionKey, steel}) {
		t.Fatal(cargo, goods)
	}
	if definitions[0].Definition != "ZMeal" {
		t.Fatal("mutated caller catalog")
	}
	compatible[NutritionKey] = domain.Unknown[bool]()
	cargo, _ = TradeMissionCargo(demands, compatible, definitions, 2)
	if len(cargo) != 1 || cargo[0].Definition != "Steel" {
		t.Fatal(cargo)
	}
	demands[1].Gap = math.Inf(1)
	cargo, _ = TradeMissionCargo(demands, compatible, definitions, 2)
	if len(cargo) != 0 {
		t.Fatal(cargo)
	}
}

func purchaseRequest() MissionPurchaseRequest {
	return MissionPurchaseRequest{
		Mission: domain.TradeMission{SilverBudget: 100, Demand: []domain.CargoItem{{Definition: "Steel", Count: 20}}},
		Targets: map[Resource]int64{"Steel": 30}, Stock: domain.Known([]Amount{{Resource: "Steel", Count: 5}}),
		Food: domain.Known(FoodPlan{}), FoodTargetDays: 2,
		Rows:   []MissionPurchaseRow{{LineID: "steel", DefName: "Steel", TraderCount: 50, BuyPrice: domain.Known(2.0), UnitMass: domain.Known(1.0), WillTrade: domain.Known(true)}},
		Silver: domain.Known(int64(100)), Mass: domain.Known(0.0), Capacity: domain.Known(100.0),
	}
}

func TestMissionPurchaseBounds(t *testing.T) {
	tests := []struct {
		name  string
		edit  func(*MissionPurchaseRequest)
		count uint64
	}{
		{"saved authorization", func(in *MissionPurchaseRequest) {}, 20},
		{"current demand", func(in *MissionPurchaseRequest) { in.Targets["Steel"] = 8 }, 3},
		{"silver authorization", func(in *MissionPurchaseRequest) { in.Mission.SilverBudget = 7 }, 3},
		{"current silver", func(in *MissionPurchaseRequest) { in.Silver = domain.Known(int64(9)) }, 4},
		{"carry headroom", func(in *MissionPurchaseRequest) { in.Mass = domain.Known(96.5) }, 3},
		{"live stock", func(in *MissionPurchaseRequest) { in.Rows[0].TraderCount = 2 }, 2},
		{"unknown stock", func(in *MissionPurchaseRequest) { in.Stock = domain.Unknown[[]Amount]() }, 0},
		{"unknown mass", func(in *MissionPurchaseRequest) { in.Rows[0].UnitMass = domain.Unknown[float64]() }, 0},
		{"unknown price", func(in *MissionPurchaseRequest) { in.Rows[0].BuyPrice = domain.Unknown[float64]() }, 0},
		{"unknown capacity", func(in *MissionPurchaseRequest) { in.Capacity = domain.Unknown[float64]() }, 0},
		{"satisfied", func(in *MissionPurchaseRequest) { in.Targets["Steel"] = 5 }, 0},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			in := purchaseRequest()
			tt.edit(&in)
			lines, goods, err := PlanTradeMissionPurchases(in)
			if err != nil {
				t.Fatal(err)
			}
			if tt.count == 0 {
				if len(lines) != 0 || len(goods) != 0 {
					t.Fatal(lines, goods)
				}
				return
			}
			if len(lines) != 1 || lines[0].AbsoluteCount != int32(tt.count) || !reflect.DeepEqual(goods, []domain.CargoItem{{Definition: "Steel", Count: tt.count}}) {
				t.Fatal(lines, goods)
			}
		})
	}
}

func TestMissionPurchaseSpendsSharedBudgetOnce(t *testing.T) {
	in := purchaseRequest()
	in.Mission.SilverBudget = 13
	in.Capacity = domain.Known(6.0)
	in.Rows[0].TraderCount = 4
	second := in.Rows[0]
	second.LineID = "steel2"
	second.TraderCount = 10
	in.Rows = append(in.Rows, second)
	lines, goods, err := PlanTradeMissionPurchases(in)
	if err != nil || len(lines) != 2 || len(goods) != 1 || goods[0].Count != 6 {
		t.Fatal(lines, goods, err)
	}
}

func TestMissionPurchaseConvertsFoodGap(t *testing.T) {
	in := purchaseRequest()
	in.Mission.Demand = []domain.CargoItem{{Definition: "Meal", Count: 10}}
	in.Nutrition = map[string]domain.Fact[float64]{"Meal": domain.Known(0.5)}
	in.Food = domain.Known(FoodPlan{GapPerDay: 1.1})
	in.Rows[0].DefName = "Meal"
	_, goods, err := PlanTradeMissionPurchases(in)
	if err != nil || !reflect.DeepEqual(goods, []domain.CargoItem{{Definition: "Meal", Count: 5}}) {
		t.Fatal(goods, err)
	}
}

func TestMissionReturnSafetyPolicy(t *testing.T) {
	base := MissionReturnFacts{Mass: domain.Known(10.0), Capacity: domain.Known(10.0), FoodDays: domain.Known(2.0), RotDays: domain.Known(2.0), RouteTicks: domain.Known(2 * int64(domain.TicksPerDay))}
	if !TradeMissionReturnSafe(base) {
		t.Fatal("exact provisions refused")
	}
	for name, edit := range map[string]func(*MissionReturnFacts){
		"unknown route":     func(in *MissionReturnFacts) { in.RouteTicks = domain.Unknown[int64]() },
		"asymmetric return": func(in *MissionReturnFacts) { in.RouteTicks = domain.Known(3 * int64(domain.TicksPerDay)) },
		"unknown food":      func(in *MissionReturnFacts) { in.FoodDays = domain.Unknown[float64]() },
		"rot":               func(in *MissionReturnFacts) { in.RotDays = domain.Known(1.9) },
		"overloaded":        func(in *MissionReturnFacts) { in.Mass = domain.Known(10.1) },
		"invalid":           func(in *MissionReturnFacts) { in.Capacity = domain.Known(math.NaN()) },
	} {
		t.Run(name, func(t *testing.T) {
			in := base
			edit(&in)
			if TradeMissionReturnSafe(in) {
				t.Fatal("unsafe return admitted")
			}
		})
	}
}

func TestMissionPaymentRechecksAuthorization(t *testing.T) {
	for _, tt := range []struct {
		budget, silver int64
		balance        float64
		want           bool
	}{{10, 20, -10, true}, {10, 20, -11, false}, {20, 9, -10, false}, {20, 20, 1, false}, {20, 20, math.NaN(), false}} {
		if got := TradeMissionPaymentSafe(tt.budget, domain.Known(tt.silver), domain.Known(tt.balance)); got != tt.want {
			t.Fatal(tt, got)
		}
	}
	if TradeMissionPaymentSafe(10, domain.Unknown[int64](), domain.Known(-1.0)) || TradeMissionPaymentSafe(10, domain.Known(int64(10)), domain.Unknown[float64]()) {
		t.Fatal("unknown economics admitted")
	}
}
