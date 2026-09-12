package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
	"reflect"
	"testing"
)

func gearFixture() GearPlanningRequest {
	p := GearPawn{Pawn: "pawn", Loadout: "native-loadout", Deficit: domain.Known(true), Candidates: domain.Known([]GearCandidate{}), Replacements: domain.Known([]GearReplacement{{Definition: "Parka", Stuff: "Cloth", Reason: "worn"}})}
	recipe := GearRecipe{Definition: "Make_Parka", Products: []Resource{"Parka"}, Available: domain.Known(true), AvailableOn: domain.Known(true), Ingredients: domain.Known([][]Amount{{{"Cloth", 80}, {"Synthread", 60}}})}
	return GearPlanningRequest{Observation: domain.Known(GearObservation{Pawns: []GearPawn{p}}), Benches: domain.Known([]GearBench{{ID: "bench", Bills: domain.Known([]GearBill{}), Recipes: domain.Known([]GearRecipe{recipe})}}), Stock: []Stock{{"Cloth", domain.Known(int64(100))}, {"Synthread", domain.Known(int64(100))}, {"Parka", domain.Known(int64(3))}}}
}

func TestGearReviewRequiresCompleteCensus(t *testing.T) {
	r := gearFixture()
	v, _ := r.Observation.Value()
	p := v.Pawns[0]
	p.Pawn = "other"
	p.Deficit = domain.Known(false)
	v.Pawns = append(v.Pawns, p)
	review, err := ReviewGear(domain.Known(v))
	if err != nil || review.Deficit != domain.Known(.5) || review.Recovered != domain.Known(false) {
		t.Fatal(review, err)
	}
	v.Pawns[0].Deficit = domain.Known(false)
	review, err = ReviewGear(domain.Known(v))
	if err != nil || review.Recovered != domain.Known(true) {
		t.Fatal(review, err)
	}
	v.Pawns[1].Candidates = domain.Known([]GearCandidate{{"replacement", 1, "Parka"}})
	review, err = ReviewGear(domain.Known(v))
	if err != nil || review.Deficit != domain.Known(.5) {
		t.Fatal(review, err)
	}
	for _, f := range []domain.Fact[GearObservation]{domain.Unknown[GearObservation](), domain.Known(GearObservation{}), domain.Known(GearObservation{Pawns: []GearPawn{{Pawn: "pawn", Loadout: "loadout", Deficit: domain.Known(false)}}})} {
		review, err = ReviewGear(f)
		if _, known := review.Recovered.Value(); err != nil || known {
			t.Fatal(review, err)
		}
	}
}

func TestGearReplacementOrderAndSeenLoadouts(t *testing.T) {
	r := gearFixture()
	v, _ := r.Observation.Value()
	p := v.Pawns[0]
	p.Pawn = "z-pawn"
	p.Candidates = domain.Known([]GearCandidate{{"z-item", 5, "Parka"}, {"a-item", 5, "Parka"}})
	v.Pawns = []GearPawn{p}
	p.Pawn = "a-pawn"
	p.Candidates = domain.Known([]GearCandidate{{"b-item", 5, "Parka"}})
	v.Pawns = append(v.Pawns, p)
	r.Observation = domain.Known(v)
	first, err := SelectGearMethod(r)
	if err != nil || first.Kind != GearReplace || first.Pawn != "a-pawn" || first.Target != "b-item" {
		t.Fatal(first, err)
	}
	r.Seen = []domain.MethodID{first.ID}
	second, err := SelectGearMethod(r)
	if err != nil || second.Pawn != "z-pawn" || second.Target != "a-item" {
		t.Fatal(second, err)
	}
	r.Seen = append(r.Seen, second.ID)
	third, err := SelectGearMethod(r)
	if err != nil || third.Target != "z-item" {
		t.Fatal(third, err)
	}
	r.Seen = append(r.Seen, third.ID)
	last, err := SelectGearMethod(r)
	if err != nil || last.Kind != GearBlocked {
		t.Fatal("seen gear must precede production", last, err)
	}
	v.Pawns[1].Loadout = "changed-native-loadout"
	r.Observation = domain.Known(v)
	renewed, err := SelectGearMethod(r)
	if err != nil || renewed.Kind != GearReplace || renewed.ID == first.ID {
		t.Fatal(renewed, err)
	}
	v.Pawns[0].Blocked = true
	v.Pawns[1].Blocked = true
	r.Observation = domain.Known(v)
	blocked, err := SelectGearMethod(r)
	if err != nil || blocked.Kind != GearBlocked {
		t.Fatal("player controlled pawn received proposal", blocked, err)
	}
}

func TestGearProductionPreservesMaterialAndSharedBudget(t *testing.T) {
	r := gearFixture()
	before := gearFixture()
	method, err := SelectGearMethod(r)
	if err != nil || method.Kind != GearProduce || method.Bench != "bench" || !reflect.DeepEqual(method.Costs, []Amount{{"Cloth", 80}}) || !reflect.DeepEqual(method.Filter, []Resource{"Cloth"}) {
		t.Fatal(method, err)
	}
	if !reflect.DeepEqual(r, before) {
		t.Fatal("selection mutated caller inputs")
	}
	for _, change := range []func(*GearPlanningRequest){
		func(r *GearPlanningRequest) { r.Rules = []ResourceRule{{"Cloth", 21, Allow}} },
		func(r *GearPlanningRequest) { r.Holds = []Amount{{"Cloth", 21}} },
		func(r *GearPlanningRequest) { r.Rules = []ResourceRule{{"Cloth", 0, DefenseOnly}} },
		func(r *GearPlanningRequest) { r.Rules = []ResourceRule{{"Cloth", 0, Stop}} },
	} {
		r := gearFixture()
		change(&r)
		m, e := SelectGearMethod(r)
		if e != nil || m.Kind != GearBlocked {
			t.Fatal(m, e)
		}
	}
	r.Seen = []domain.MethodID{method.ID}
	wait, err := SelectGearMethod(r)
	if err != nil || wait.Kind != GearWait {
		t.Fatal("duplicate production", wait, err)
	}
}

func TestGearProductionFindsExistingBillOnLaterBench(t *testing.T) {
	r := gearFixture()
	benches, _ := r.Benches.Value()
	benches[0].ID = "a-new-bench"
	benches = append(benches, GearBench{ID: "z-player-bench", Bills: domain.Known([]GearBill{{Active: domain.Known(true), Products: []Resource{"Parka"}}})})
	r.Benches = domain.Known(benches)
	m, err := SelectGearMethod(r)
	if err != nil || m.Kind != GearWait {
		t.Fatal("duplicated player production", m, err)
	}
	benches[1].Bills = domain.Unknown[[]GearBill]()
	r.Benches = domain.Known(benches)
	m, err = SelectGearMethod(r)
	if err != nil || m.Kind != GearUnknown {
		t.Fatal("unknown existing bills spent materials", m, err)
	}
}

func TestGearProductionAggregatesSlotsAndRejectsAmbiguousFlatFilter(t *testing.T) {
	r := gearFixture()
	benches, _ := r.Benches.Value()
	recipes, _ := benches[0].Recipes.Value()
	recipes[0].Ingredients = domain.Known([][]Amount{{{"Cloth", 60}}, {{"Cloth", 50}}})
	m, err := SelectGearMethod(r)
	if err != nil || m.Kind != GearBlocked {
		t.Fatal("double spent ingredient stock", m, err)
	}
	r.Stock[0].Available = domain.Known(int64(110))
	m, err = SelectGearMethod(r)
	if err != nil || m.Kind != GearProduce || !reflect.DeepEqual(m.Costs, []Amount{{"Cloth", 110}}) {
		t.Fatal(m, err)
	}
	recipes[0].Ingredients = domain.Known([][]Amount{{{"Cloth", 60}, {"Synthread", 60}}, {{"Synthread", 10}}})
	m, err = SelectGearMethod(r)
	if err != nil || m.Kind != GearBlocked {
		t.Fatal("flat filter allowed wrong stuff", m, err)
	}
}

func TestGearRejectsMalformedCandidatesAndProduction(t *testing.T) {
	for _, change := range []func(*GearPlanningRequest){
		func(r *GearPlanningRequest) {
			v, _ := r.Observation.Value()
			v.Pawns[0].Candidates = domain.Known([]GearCandidate{{"item", math.NaN(), "Parka"}})
		},
		func(r *GearPlanningRequest) {
			v, _ := r.Observation.Value()
			v.Pawns = append(v.Pawns, v.Pawns[0])
			r.Observation = domain.Known(v)
		},
		func(r *GearPlanningRequest) { r.Holds = []Amount{{"Cloth", -1}} },
		func(r *GearPlanningRequest) { r.Stock = append(r.Stock, r.Stock[0]) },
		func(r *GearPlanningRequest) {
			b, _ := r.Benches.Value()
			recipes, _ := b[0].Recipes.Value()
			recipes[0].Ingredients = domain.Known([][]Amount{{{"Cloth", -1}}})
		},
	} {
		r := gearFixture()
		change(&r)
		if _, err := SelectGearMethod(r); err == nil {
			t.Fatal("malformed evidence accepted")
		}
	}
}

func TestGearProductionKeepsUnknownEvidenceUnknown(t *testing.T) {
	for _, change := range []func(*GearPlanningRequest){
		func(r *GearPlanningRequest) { r.Stock[0].Available = domain.Unknown[int64]() },
		func(r *GearPlanningRequest) { r.Stock = r.Stock[1:] },
		func(r *GearPlanningRequest) {
			b, _ := r.Benches.Value()
			recipes, _ := b[0].Recipes.Value()
			recipes[0].Available = domain.Unknown[bool]()
		},
		func(r *GearPlanningRequest) {
			b, _ := r.Benches.Value()
			recipes, _ := b[0].Recipes.Value()
			recipes[0].AvailableOn = domain.Unknown[bool]()
		},
		func(r *GearPlanningRequest) {
			b, _ := r.Benches.Value()
			recipes, _ := b[0].Recipes.Value()
			recipes[0].Ingredients = domain.Unknown[[][]Amount]()
		},
	} {
		r := gearFixture()
		change(&r)
		m, err := SelectGearMethod(r)
		if err != nil || m.Kind != GearUnknown {
			t.Fatal(m, err)
		}
	}
}

func TestGearExistingItemsRespectGoReservationsAndUnknownStock(t *testing.T) {
	r := gearFixture()
	v, _ := r.Observation.Value()
	v.Pawns[0].Candidates = domain.Known([]GearCandidate{{Target: "parka", Gain: 1, Definition: "Parka"}})
	for _, change := range []func(*GearPlanningRequest){
		func(r *GearPlanningRequest) { r.Rules = []ResourceRule{{"Parka", 3, Allow}} },
		func(r *GearPlanningRequest) { r.Rules = []ResourceRule{{"Parka", 0, Stop}} },
		func(r *GearPlanningRequest) { r.Holds = []Amount{{"Parka", 3}} },
	} {
		copy := r
		change(&copy)
		m, err := SelectGearMethod(copy)
		if err != nil || m.Kind != GearBlocked {
			t.Fatal("native eligibility bypassed Go budgets", m, err)
		}
	}
	r.Stock[2].Available = domain.Unknown[int64]()
	m, err := SelectGearMethod(r)
	if err != nil || m.Kind != GearUnknown {
		t.Fatal(m, err)
	}
}
