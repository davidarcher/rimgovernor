package policy

import "testing"

func TestEvaluateWorldUnreadable(t *testing.T) {
	report := EvaluateWorld(WorldEvaluationPolicy{TravelFoodMarginDays: 0.5}, WorldEvaluationFacts{Readable: false})
	if report.Readable || report.Reason == "" || len(report.Caravans) != 0 || len(report.Quests) != 0 {
		t.Fatal(report)
	}
}

func TestEvaluateWorldCaravanRecovery(t *testing.T) {
	policy := WorldEvaluationPolicy{TravelFoodMarginDays: 0.5}
	healthyPawn := WorldEvaluationPawnFact{DeadKnown: true, Dead: false, DownedKnown: true, Downed: false}
	downedPawn := WorldEvaluationPawnFact{DeadKnown: true, Dead: false, DownedKnown: true, Downed: true}
	cases := []struct {
		name             string
		caravan          WorldEvaluationCaravanFact
		wantRecovery     bool
		wantHealthy      bool
		wantReachableNil bool
	}{
		{
			name: "safe caravan with margin",
			caravan: WorldEvaluationCaravanFact{
				ID: "c1", FoodDays: 3, FoodDaysKnown: true, Pawns: []WorldEvaluationPawnFact{healthyPawn},
				HomeRoutes: []WorldEvaluationRouteFact{{DestinationMapID: 1, Reachable: true, EstimatedTicks: 60000, EstimatedTicksKnown: true}},
			},
			wantRecovery: false, wantHealthy: true, wantReachableNil: false,
		},
		{
			name: "stranded: food below route+margin",
			caravan: WorldEvaluationCaravanFact{
				ID: "c2", FoodDays: 1, FoodDaysKnown: true, Pawns: []WorldEvaluationPawnFact{healthyPawn},
				HomeRoutes: []WorldEvaluationRouteFact{{DestinationMapID: 1, Reachable: true, EstimatedTicks: 60000, EstimatedTicksKnown: true}},
			},
			wantRecovery: true, wantHealthy: true, wantReachableNil: false,
		},
		{
			name:             "no reachable home route",
			caravan:          WorldEvaluationCaravanFact{ID: "c3", FoodDays: 10, FoodDaysKnown: true, Pawns: []WorldEvaluationPawnFact{healthyPawn}},
			wantRecovery:     true,
			wantHealthy:      true,
			wantReachableNil: true,
		},
		{
			name: "unknown food days",
			caravan: WorldEvaluationCaravanFact{
				ID: "c4", Pawns: []WorldEvaluationPawnFact{healthyPawn},
				HomeRoutes: []WorldEvaluationRouteFact{{DestinationMapID: 1, Reachable: true, EstimatedTicks: 60000, EstimatedTicksKnown: true}},
			},
			wantRecovery: true, wantHealthy: true, wantReachableNil: false,
		},
		{
			name: "downed crew member",
			caravan: WorldEvaluationCaravanFact{
				ID: "c5", FoodDays: 3, FoodDaysKnown: true, Pawns: []WorldEvaluationPawnFact{healthyPawn, downedPawn},
				HomeRoutes: []WorldEvaluationRouteFact{{DestinationMapID: 1, Reachable: true, EstimatedTicks: 60000, EstimatedTicksKnown: true}},
			},
			wantRecovery: true, wantHealthy: false, wantReachableNil: false,
		},
		{
			name:         "no crew at all is not healthy",
			caravan:      WorldEvaluationCaravanFact{ID: "c6", FoodDays: 3, FoodDaysKnown: true},
			wantRecovery: true, wantHealthy: false, wantReachableNil: true,
		},
		{
			name: "unreachable route ignored",
			caravan: WorldEvaluationCaravanFact{
				ID: "c7", FoodDays: 10, FoodDaysKnown: true, Pawns: []WorldEvaluationPawnFact{healthyPawn},
				HomeRoutes: []WorldEvaluationRouteFact{{DestinationMapID: 1, Reachable: false, EstimatedTicks: 60000, EstimatedTicksKnown: true}},
			},
			wantRecovery: true, wantHealthy: true, wantReachableNil: true,
		},
		{
			name: "shortest of two reachable routes chosen",
			caravan: WorldEvaluationCaravanFact{
				ID: "c8", FoodDays: 3, FoodDaysKnown: true, Pawns: []WorldEvaluationPawnFact{healthyPawn},
				HomeRoutes: []WorldEvaluationRouteFact{
					{DestinationMapID: 1, Reachable: true, EstimatedTicks: 120000, EstimatedTicksKnown: true},
					{DestinationMapID: 2, Reachable: true, EstimatedTicks: 60000, EstimatedTicksKnown: true},
				},
			},
			wantRecovery: false, wantHealthy: true, wantReachableNil: false,
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			report := EvaluateWorld(policy, WorldEvaluationFacts{Readable: true, Caravans: []WorldEvaluationCaravanFact{tc.caravan}})
			if !report.Readable || len(report.Caravans) != 1 {
				t.Fatal(report)
			}
			got := report.Caravans[0]
			if got.RecoveryRequired != tc.wantRecovery || got.Healthy != tc.wantHealthy || (got.ReachableHome == nil) != tc.wantReachableNil {
				t.Fatal(got)
			}
			if got.RecoveryRequired && got.Recommendation != "Player review: hold, resupply or explicit return" {
				t.Fatal(got.Recommendation)
			}
			if !got.RecoveryRequired && got.Recommendation != "Observed return route available" {
				t.Fatal(got.Recommendation)
			}
		})
	}
	t.Run("shortest route destination", func(t *testing.T) {
		report := EvaluateWorld(policy, WorldEvaluationFacts{Readable: true, Caravans: []WorldEvaluationCaravanFact{{
			ID: "c8", FoodDays: 3, FoodDaysKnown: true, Pawns: []WorldEvaluationPawnFact{healthyPawn},
			HomeRoutes: []WorldEvaluationRouteFact{
				{DestinationMapID: 1, Reachable: true, EstimatedTicks: 120000, EstimatedTicksKnown: true},
				{DestinationMapID: 2, Reachable: true, EstimatedTicks: 60000, EstimatedTicksKnown: true},
			},
		}}})
		if report.Caravans[0].ReachableHome == nil || report.Caravans[0].ReachableHome.DestinationMapID != 2 {
			t.Fatal(report.Caravans[0].ReachableHome)
		}
	})
}

func TestEvaluateWorldQuestDeficitsAndCarried(t *testing.T) {
	policy := WorldEvaluationPolicy{TravelFoodMarginDays: 0.5}
	facts := WorldEvaluationFacts{
		Readable: true,
		Caravans: []WorldEvaluationCaravanFact{
			{ID: "carrier", Inventory: map[string]int64{"Steel": 100, "Silver": 10}},
			{ID: "short", Inventory: map[string]int64{"Steel": 5}},
		},
		Quests: []WorldEvaluationQuestFact{
			{ID: "q-ongoing", State: "Ongoing", NativeEligible: true, TradeRequests: []WorldEvaluationTradeRequestFact{{Resource: "Steel", Count: 40, DestinationTile: 7}}},
			{ID: "q-ended", State: "EndedSuccess", NativeEligible: false, TradeRequests: []WorldEvaluationTradeRequestFact{{Resource: "Steel", Count: 40, DestinationTile: 7}}},
			{ID: "q-deficit", State: "Ongoing", NativeEligible: true, TradeRequests: []WorldEvaluationTradeRequestFact{{Resource: "Wood", Count: 200, DestinationTile: 7}}},
			{ID: "q-invalid-row", State: "Ongoing", NativeEligible: true, TradeRequests: []WorldEvaluationTradeRequestFact{{Resource: "", Count: 0, DestinationTile: 7}}},
		},
		HomeResources: map[string]int64{"Steel": 5, "Wood": 0},
	}
	report := EvaluateWorld(policy, facts)
	if !report.Readable || len(report.Quests) != 4 {
		t.Fatal(report)
	}
	byID := map[string]WorldEvaluationQuestReport{}
	for _, q := range report.Quests {
		byID[q.ID] = q
	}
	ongoing := byID["q-ongoing"]
	if len(ongoing.CarriedCandidates) != 1 || ongoing.CarriedCandidates[0] != "carrier" || ongoing.ResourceDeficits["Steel"] != 35 || ongoing.Recommendation != "Cargo observed in listed parties; validate native quality, freshness and settlement eligibility" {
		t.Fatal(ongoing)
	}
	ended := byID["q-ended"]
	if ended.Recommendation != "Terminal objective; do not replay" {
		t.Fatal(ended)
	}
	deficit := byID["q-deficit"]
	if len(deficit.CarriedCandidates) != 0 || deficit.ResourceDeficits["Wood"] != 200 || deficit.Recommendation != "Production or acquisition required; no future output credited" {
		t.Fatal(deficit)
	}
	invalid := byID["q-invalid-row"]
	if len(invalid.CarriedCandidates) != 0 || len(invalid.ResourceDeficits) != 0 || invalid.Recommendation != "Explicit player choice required; acceptance is separate from completion" {
		t.Fatal(invalid)
	}
}

func TestEvaluateWorldScopeAndPolicyEcho(t *testing.T) {
	policy := WorldEvaluationPolicy{TravelFoodMarginDays: 1.5}
	report := EvaluateWorld(policy, WorldEvaluationFacts{Readable: true})
	if report.Scope == "" || report.Policy != policy {
		t.Fatal(report)
	}
}
