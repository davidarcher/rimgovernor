package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
	"reflect"
	"testing"
)

func gearFixture() GearPlanningRequest {
	p := GearPawn{Pawn: "pawn", Loadout: "native-loadout", Deficit: domain.Known(true), Candidates: domain.Known([]GearCandidate{}), Replacements: domain.Known([]GearReplacement{{Definition: "Parka", Stuff: "Cloth", Reason: "worn"}})}
	recipe := GearRecipe{Definition: "Make_Parka", Products: []Resource{"Parka"}, Available: domain.Known(true), AvailableOn: domain.Known(true), Ingredients: domain.Known([][]Amount{{{"Cloth", 80}, {"Synthread", 60}}}), RequiredWork: domain.Known([]WorkRequirement{{Work: "Tailoring", Skill: "Crafting", Minimum: 6}})}
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
	// The inspected cloth protected by a reserve, hold or spending rule
	// yields to the recipe's other funded material; with both protected the
	// bill is refused.
	for _, change := range []func(*GearPlanningRequest){
		func(r *GearPlanningRequest) { r.Rules = []ResourceRule{{"Cloth", 21, Allow}} },
		func(r *GearPlanningRequest) { r.Holds = []Amount{{"Cloth", 21}} },
		func(r *GearPlanningRequest) { r.Rules = []ResourceRule{{"Cloth", 0, DefenseOnly}} },
		func(r *GearPlanningRequest) { r.Rules = []ResourceRule{{"Cloth", 0, Stop}} },
	} {
		r := gearFixture()
		change(&r)
		m, e := SelectGearMethod(r)
		if e != nil || m.Kind != GearProduce || !reflect.DeepEqual(m.Costs, []Amount{{"Synthread", 60}}) {
			t.Fatal(m, e)
		}
		r.Rules = append(r.Rules, ResourceRule{"Synthread", 0, Stop})
		m, e = SelectGearMethod(r)
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

func TestGearProductionRetainsRequiredWorkAndPlayerRefusals(t *testing.T) {
	r := gearFixture()
	m, err := SelectGearMethod(r)
	if err != nil || !reflect.DeepEqual(m.RequiredWork, []WorkRequirement{{Work: "Tailoring", Skill: "Crafting", Minimum: 6}}) {
		t.Fatal(m, err)
	}
	team := workTeam(true)
	work, _ := team[0].Work.Value()
	team[0].Work = domain.Known(append(append([]WorkPriority(nil), work...), WorkPriority{Work: "Tailoring"}))
	skills, _ := team[0].Skills.Value()
	team[0].Skills = domain.Known(append(append([]WorkSkill(nil), skills...), WorkSkill{Name: "Crafting", Level: 6}))
	ready, err := AssignWork(team, m.RequiredWork, nil)
	if err != nil || ready.Capacity != domain.Known(true) || workValue(t, ready, "builder", "Tailoring") != 1 {
		t.Fatal(ready, err)
	}
	refused, err := AssignWork(team, m.RequiredWork, []WorkOverride{{Pawn: "builder", Work: "Tailoring", Priority: 0}})
	if err != nil || refused.Capacity != domain.Known(false) {
		t.Fatal("player refusal was lost", refused, err)
	}
	m.RequiredWork[0].Minimum = 0
	again, err := SelectGearMethod(r)
	if err != nil || again.RequiredWork[0].Minimum != 6 {
		t.Fatal("proposal aliases native recipe requirements", again, err)
	}
	benches, _ := r.Benches.Value()
	recipes, _ := benches[0].Recipes.Value()
	recipes[0].RequiredWork = domain.Unknown[[]WorkRequirement]()
	unknown, err := SelectGearMethod(r)
	if err != nil || unknown.Kind != GearUnknown {
		t.Fatal(unknown, err)
	}
	recipes[0].RequiredWork = domain.Known([]WorkRequirement{{Work: "Tailoring", Minimum: -1}})
	if _, err := SelectGearMethod(r); err == nil {
		t.Fatal("invalid native work requirement accepted")
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
	// A pinned stuff the flat filter cannot isolate falls back to any funded
	// material rather than refusing the replacement.
	recipes[0].Ingredients = domain.Known([][]Amount{{{"Cloth", 60}, {"Synthread", 60}}, {{"Synthread", 10}}})
	m, err = SelectGearMethod(r)
	if err != nil || m.Kind != GearProduce || !reflect.DeepEqual(m.Costs, []Amount{{"Cloth", 60}, {"Synthread", 10}}) {
		t.Fatal(m, err)
	}
}

func TestGearProductionPrefersInspectedStuffThenAnyFundedMaterial(t *testing.T) {
	r := gearFixture()
	m, err := SelectGearMethod(r)
	if err != nil || m.Kind != GearProduce || !reflect.DeepEqual(m.Costs, []Amount{{"Cloth", 80}}) || !reflect.DeepEqual(m.Filter, []Resource{"Cloth"}) {
		t.Fatal("inspected stuff not preferred", m, err)
	}
	// The worn-out shirt was cloth; only leather is in stock, and the
	// recipe accepts it.
	benches, _ := r.Benches.Value()
	recipes, _ := benches[0].Recipes.Value()
	recipes[0].Ingredients = domain.Known([][]Amount{{{"Cloth", 80}, {"Leather_Plain", 80}}})
	r.Stock = []Stock{{"Cloth", domain.Known(int64(0))}, {"Leather_Plain", domain.Known(int64(100))}, {"Parka", domain.Known(int64(0))}}
	m, err = SelectGearMethod(r)
	if err != nil || m.Kind != GearProduce || !reflect.DeepEqual(m.Costs, []Amount{{"Leather_Plain", 80}}) || !reflect.DeepEqual(m.Filter, []Resource{"Leather_Plain"}) {
		t.Fatal("funded alternative material refused", m, err)
	}
	r.Stock[1].Available = domain.Unknown[int64]()
	m, err = SelectGearMethod(r)
	if err != nil || m.Kind != GearUnknown {
		t.Fatal("unknown alternative stock decided", m, err)
	}
	// An unobserved stock of the inspected stuff does not hold up a bill the
	// alternative funds.
	r.Stock[0].Available = domain.Unknown[int64]()
	r.Stock[1].Available = domain.Known(int64(100))
	m, err = SelectGearMethod(r)
	if err != nil || m.Kind != GearProduce || !reflect.DeepEqual(m.Costs, []Amount{{"Leather_Plain", 80}}) {
		t.Fatal("unknown inspected stuff blocked the alternative", m, err)
	}
	r.Stock[0].Available = domain.Known(int64(0))
	r.Stock[1].Available = domain.Known(int64(10))
	m, err = SelectGearMethod(r)
	if err != nil || m.Kind != GearBlocked {
		t.Fatal("unfunded alternative produced", m, err)
	}
}

func TestGearReviewApparelCensus(t *testing.T) {
	shirt := GearApparel{Definition: "Apparel_BasicShirt", Condition: .3, Groups: []string{"Torso", "Shoulders", "Arms"}}
	pants := GearApparel{Definition: "Apparel_Pants", Condition: 1, Groups: []string{"Legs"}}
	pawn := func(id PawnID, deficit bool, apparel ...GearApparel) GearPawn {
		return GearPawn{Pawn: id, Loadout: "loadout", Deficit: domain.Known(deficit), Candidates: domain.Known([]GearCandidate{}), Replacements: domain.Known([]GearReplacement{}), Apparel: domain.Known(apparel)}
	}
	v := GearObservation{Pawns: []GearPawn{pawn("a", true, shirt, pants), pawn("b", false, pants), pawn("c", false, GearApparel{Definition: "Apparel_TribalA", Condition: .9, Groups: []string{"Torso", "Legs"}}), pawn("d", false)}}
	review, err := ReviewGear(domain.Known(v))
	if err != nil || review.Deficit != domain.Known(.25) || review.WornOut != domain.Known(.25) || review.Uncovered != domain.Known(.5) {
		t.Fatal(review, err)
	}
	// One pawn with an unobserved wardrobe leaves the apparel census unknown
	// while the native deficit still decides recovery.
	v.Pawns[3].Apparel = domain.Unknown[[]GearApparel]()
	review, err = ReviewGear(domain.Known(v))
	if _, known := review.WornOut.Value(); err != nil || known || review.Deficit != domain.Known(.25) {
		t.Fatal(review, err)
	}
	if _, known := review.Uncovered.Value(); known {
		t.Fatal(review)
	}
	for _, bad := range []GearApparel{{Definition: "", Condition: 1}, {Definition: "Apparel_Pants", Condition: 1.5}, {Definition: "Apparel_Pants", Condition: math.NaN()}, {Definition: "Apparel_Pants", Condition: 1, Groups: []string{"Legs", "Legs"}}} {
		v.Pawns[3].Apparel = domain.Known([]GearApparel{bad})
		if _, err := ReviewGear(domain.Known(v)); err == nil {
			t.Fatal("malformed apparel accepted", bad)
		}
	}
}

func TestGearReplacementNeedsListDeficitDefinitions(t *testing.T) {
	pawn := func(id PawnID, deficit bool, needs ...GearReplacement) GearPawn {
		return GearPawn{Pawn: id, Loadout: "loadout", Deficit: domain.Known(deficit), Candidates: domain.Known([]GearCandidate{}), Replacements: domain.Known(needs)}
	}
	shirt := GearReplacement{Definition: "Apparel_BasicShirt", Stuff: "Cloth", Reason: "wear"}
	pants := GearReplacement{Definition: "Apparel_Pants", Reason: "missing"}
	v := GearObservation{Pawns: []GearPawn{pawn("a", true, pants, shirt, GearReplacement{Definition: "Apparel_BasicShirt", Stuff: "Leather_Plain", Reason: "wear"}), pawn("b", false, GearReplacement{Definition: "Bow_Short", Reason: "weapon"})}}
	if needs := GearReplacementNeeds(domain.Known(v)); !reflect.DeepEqual(needs, []Resource{"Apparel_BasicShirt", "Apparel_Pants"}) {
		t.Fatal("deficit pawns' needs, sorted and deduplicated", needs)
	}
	// A recovered census, an unknown census and an unobserved deficit all
	// ask for nothing.
	v.Pawns[0].Deficit = domain.Known(false)
	if needs := GearReplacementNeeds(domain.Known(v)); len(needs) != 0 {
		t.Fatal("recovered census requested", needs)
	}
	if needs := GearReplacementNeeds(domain.Unknown[GearObservation]()); len(needs) != 0 {
		t.Fatal("unknown census requested", needs)
	}
	v.Pawns[0].Deficit = domain.Unknown[bool]()
	if needs := GearReplacementNeeds(domain.Known(v)); len(needs) != 0 {
		t.Fatal("unobserved deficit requested", needs)
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
		func(r *GearPlanningRequest) {
			r.Stock[0].Available = domain.Unknown[int64]()
			r.Stock[1].Available = domain.Unknown[int64]()
		},
		func(r *GearPlanningRequest) { r.Stock = r.Stock[2:] },
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
