package policy

import (
	"math"
	"reflect"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// gearFixture is a census already projected from a loadout model (Deficit,
// Candidates and Replacements as modeledGearObservation leaves them); use
// selectProjected to plan from it. Model-driven census tests build pawns with
// gearDeficitPawn and gearDressedPawn instead.
func gearFixture() GearPlanningRequest {
	return gearRequest(GearPawn{Pawn: "pawn", Loadout: "native-loadout", Deficit: domain.Known(true), Candidates: domain.Known([]GearCandidate{}), Replacements: domain.Known([]GearReplacement{{Definition: "Parka", Stuff: "Cloth", Reason: "loadout"}})})
}

// gearModelFixture is gearFixture's bench and stock with a pawn whose loadout
// model wants a cloth parka.
func gearModelFixture() GearPlanningRequest {
	parka := loadoutOption("Parka", GearSkinTorso)
	parka.Stuff = "Cloth"
	return gearRequest(gearDeficitPawn("pawn", parka))
}

func gearRequest(p GearPawn) GearPlanningRequest {
	recipe := GearRecipe{Definition: "Make_Parka", Products: []Resource{"Parka"}, Available: domain.Known(true), AvailableOn: domain.Known(true), Ingredients: domain.Known([][]Amount{{{"Cloth", 80}, {"Synthread", 60}}}), RequiredWork: domain.Known([]WorkRequirement{{Work: "Tailoring", Skill: "Crafting", Minimum: 6}})}
	return GearPlanningRequest{Observation: domain.Known(GearObservation{Pawns: []GearPawn{p}}), Benches: domain.Known([]GearBench{{ID: "bench", Bills: domain.Known([]GearBill{}), Recipes: domain.Known([]GearRecipe{recipe})}})}
}

// gearDeficitPawn is a woman wearing nothing (so a torso option is a gap) whose loadout model wants each
// option (bill-sourced), one slot apiece.
func gearDeficitPawn(id PawnID, wanted ...GearOption) GearPawn {
	return GearPawn{Pawn: id, Loadout: "loadout", LoadoutModel: domain.Known(GearLoadoutInput{Female: true, Options: wanted})}
}

// gearDressedPawn is a pawn already wearing every option: no gap.
func gearDressedPawn(id PawnID, worn ...GearOption) GearPawn {
	for i := range worn {
		worn[i].Source = GearWorn
	}
	return GearPawn{Pawn: id, Loadout: "loadout", LoadoutModel: domain.Known(GearLoadoutInput{Female: true, Worn: worn})}
}

// selectProjected plans a method from a census already projected from a
// model, as SelectGearMethod does after ReviewGear.
func selectProjected(r GearPlanningRequest) (GearMethod, error) {
	v, _ := r.Observation.Value()
	if err := v.Validate(); err != nil {
		return GearMethod{}, err
	}
	recovered := true
	for _, p := range v.Pawns {
		if deficit, _ := p.Deficit.Value(); deficit {
			recovered = false
		}
	}
	if recovered {
		return GearMethod{Kind: GearRecovered}, nil
	}
	return selectGear(r, GearReview{Recovered: domain.Known(false)}, v)
}

func TestGearReviewRequiresCompleteCensus(t *testing.T) {
	parka := loadoutOption("Parka", GearSkinTorso)
	v := GearObservation{Pawns: []GearPawn{gearDeficitPawn("pawn", parka), gearDressedPawn("other", parka)}}
	review, err := ReviewGear(domain.Known(v))
	if err != nil || review.Deficit != domain.Known(.5) || review.Recovered != domain.Known(false) {
		t.Fatal(review, err)
	}
	v.Pawns[0] = gearDressedPawn("pawn", parka)
	review, err = ReviewGear(domain.Known(v))
	if err != nil || review.Recovered != domain.Known(true) {
		t.Fatal(review, err)
	}
	for _, f := range []domain.Fact[GearObservation]{domain.Unknown[GearObservation](), domain.Known(GearObservation{})} {
		review, err = ReviewGear(f)
		if _, known := review.Recovered.Value(); err != nil || known {
			t.Fatal(review, err)
		}
	}
}

// A pawn the model refuses is an error naming the cause, never a weaker
// judgement of that pawn; a blocked pawn that cannot wear apparel is skipped.
func TestGearReviewFailsOnARefusedModel(t *testing.T) {
	parka := loadoutOption("Parka", GearSkinTorso)
	refused := GearPawn{Pawn: "refused", Loadout: "loadout", LoadoutModel: domain.Unknown[GearLoadoutInput](), ModelRefusal: "conflicting worn gear"}
	if _, err := ReviewGear(domain.Known(GearObservation{Pawns: []GearPawn{gearDressedPawn("pawn", parka), refused}})); err == nil || !strings.Contains(err.Error(), "refused") || !strings.Contains(err.Error(), "conflicting worn gear") || !strings.Contains(err.Error(), "refused") {
		t.Fatal("refused model not named", err)
	}
	missing := GearPawn{Pawn: "missing", Loadout: "loadout", LoadoutModel: domain.Unknown[GearLoadoutInput]()}
	if _, err := ReviewGear(domain.Known(GearObservation{Pawns: []GearPawn{missing}})); err == nil {
		t.Fatal("a pawn with no model and no blocker was judged")
	}
	missing.Blocked = true
	review, err := ReviewGear(domain.Known(GearObservation{Pawns: []GearPawn{missing}}))
	if err != nil || review.Recovered != domain.Known(true) {
		t.Fatal("a blocked pawn without an apparel policy gates recovery", review, err)
	}
	if _, err := GearReplacementNeeds(domain.Known(GearObservation{Pawns: []GearPawn{refused}})); err == nil {
		t.Fatal("replacement needs hid a refused model")
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
	first, err := selectProjected(r)
	if err != nil || first.Kind != GearReplace || first.Pawn != "a-pawn" || first.Target != "b-item" {
		t.Fatal(first, err)
	}
	r.Seen = []domain.MethodID{first.ID}
	second, err := selectProjected(r)
	if err != nil || second.Pawn != "z-pawn" || second.Target != "a-item" {
		t.Fatal(second, err)
	}
	r.Seen = append(r.Seen, second.ID)
	third, err := selectProjected(r)
	if err != nil || third.Target != "z-item" {
		t.Fatal(third, err)
	}
	r.Seen = append(r.Seen, third.ID)
	last, err := selectProjected(r)
	if err != nil || last.Kind != GearBlocked {
		t.Fatal("seen gear must precede production", last, err)
	}
	v.Pawns[1].Loadout = "changed-native-loadout"
	r.Observation = domain.Known(v)
	renewed, err := selectProjected(r)
	if err != nil || renewed.Kind != GearReplace || renewed.ID == first.ID {
		t.Fatal(renewed, err)
	}
	v.Pawns[0].Blocked = true
	v.Pawns[1].Blocked = true
	r.Observation = domain.Known(v)
	blocked, err := selectProjected(r)
	if err != nil || blocked.Kind != GearBlocked {
		t.Fatal("player controlled pawn received proposal", blocked, err)
	}
}

func TestGearProductionPreservesMaterialAndSharedBudget(t *testing.T) {
	r := gearFixture()
	before := gearFixture()
	method, err := selectProjected(r)
	if err != nil || method.Kind != GearProduce || method.Bench != "bench" || !reflect.DeepEqual(method.Filter, []Resource{"Cloth"}) {
		t.Fatal(method, err)
	}
	if !reflect.DeepEqual(r, before) {
		t.Fatal("selection mutated caller inputs")
	}
	r.Seen = []domain.MethodID{method.ID}
	wait, err := selectProjected(r)
	if err != nil || wait.Kind != GearWait {
		t.Fatal("duplicate production", wait, err)
	}
}

func TestGearProductionRetainsRequiredWorkAndPlayerRefusals(t *testing.T) {
	r := gearFixture()
	m, err := selectProjected(r)
	if err != nil || !reflect.DeepEqual(m.RequiredWork, []WorkRequirement{{Work: "Tailoring", Skill: "Crafting", Minimum: 6}}) {
		t.Fatal(m, err)
	}
	team := workTeam(true)
	work, _ := team[0].Work.Value()
	team[0].Work = domain.Known(append(append([]WorkPriority(nil), work...), WorkPriority{Work: "Tailoring"}))
	skills, _ := team[0].Skills.Value()
	team[0].Skills = domain.Known(append(append([]WorkSkill(nil), skills...), WorkSkill{Name: "Crafting", Level: 6}))
	ready, err := AssignWork(team, m.RequiredWork)
	if err != nil || ready.Capacity != domain.Known(true) || workValue(t, ready, "builder", "Tailoring") != 1 {
		t.Fatal(ready, err)
	}
	m.RequiredWork[0].Minimum = 0
	again, err := selectProjected(r)
	if err != nil || again.RequiredWork[0].Minimum != 6 {
		t.Fatal("proposal aliases native recipe requirements", again, err)
	}
	benches, _ := r.Benches.Value()
	recipes, _ := benches[0].Recipes.Value()
	recipes[0].RequiredWork = domain.Unknown[[]WorkRequirement]()
	unknown, err := selectProjected(r)
	if err != nil || unknown.Kind != GearUnknown {
		t.Fatal(unknown, err)
	}
	recipes[0].RequiredWork = domain.Known([]WorkRequirement{{Work: "Tailoring", Minimum: -1}})
	if _, err := selectProjected(r); err == nil {
		t.Fatal("invalid native work requirement accepted")
	}
}

func TestGearProductionFindsExistingBillOnLaterBench(t *testing.T) {
	r := gearFixture()
	benches, _ := r.Benches.Value()
	benches[0].ID = "a-new-bench"
	benches = append(benches, GearBench{ID: "z-player-bench", Bills: domain.Known([]GearBill{{Active: domain.Known(true), Products: []Resource{"Parka"}}})})
	r.Benches = domain.Known(benches)
	m, err := selectProjected(r)
	if err != nil || m.Kind != GearWait {
		t.Fatal("duplicated player production", m, err)
	}
	benches[1].Bills = domain.Unknown[[]GearBill]()
	r.Benches = domain.Known(benches)
	m, err = selectProjected(r)
	if err != nil || m.Kind != GearUnknown {
		t.Fatal("unknown existing bills spent materials", m, err)
	}
}

func TestGearProductionAggregatesSlotsAndRejectsAmbiguousFlatFilter(t *testing.T) {
	r := gearFixture()
	benches, _ := r.Benches.Value()
	recipes, _ := benches[0].Recipes.Value()
	recipes[0].Ingredients = domain.Known([][]Amount{{{"Cloth", 60}}, {{"Cloth", 50}}})
	m, err := selectProjected(r)
	if err != nil || m.Kind != GearProduce || !reflect.DeepEqual(m.Filter, []Resource{"Cloth"}) {
		t.Fatal(m, err)
	}
	// A wanted stuff the flat filter cannot isolate is refused.
	recipes[0].Ingredients = domain.Known([][]Amount{{{"Cloth", 60}, {"Synthread", 60}}, {{"Synthread", 10}}})
	m, err = selectProjected(r)
	if err != nil || m.Kind != GearBlocked {
		t.Fatal(m, err)
	}
}

func TestGearProductionSpendsOnlyTheWantedStuff(t *testing.T) {
	r := gearFixture()
	m, err := selectProjected(r)
	if err != nil || m.Kind != GearProduce || !reflect.DeepEqual(m.Filter, []Resource{"Cloth"}) {
		t.Fatal("wanted stuff not preferred", m, err)
	}
	// Cloth is wanted and the recipe also accepts leather and silver: with no
	// shared category the model's stuff alone is admitted.
	benches, _ := r.Benches.Value()
	recipes, _ := benches[0].Recipes.Value()
	recipes[0].Ingredients = domain.Known([][]Amount{{{"Cloth", 80}, {"Leather_Plain", 80}, {"Silver", 10}}})
	m, err = selectProjected(r)
	if err != nil || m.Kind != GearProduce || !reflect.DeepEqual(m.Filter, []Resource{"Cloth"}) {
		t.Fatal("unwanted material substituted", m, err)
	}
	// Stuffs of the wanted stuff's catalog category stand in, a valuable
	// of the same category never does.
	r.StuffCategories = map[Resource][]string{"Cloth": {"Fabric"}, "Leather_Plain": {"Fabric"}, "Silver": {"Fabric"}}
	m, err = selectProjected(r)
	if err != nil || m.Kind != GearProduce || !reflect.DeepEqual(m.Filter, []Resource{"Cloth", "Leather_Plain"}) {
		t.Fatal("category equivalents not admitted", m, err)
	}
}

// A slot without the loadout's stuff names its cheapest member and never a
// valuable; a slot only valuables fill is refused.
func TestGearFilterNamesCheapestMemberNeverValuable(t *testing.T) {
	filter, ok := gearFilter([][]Amount{{{"Gold", 5}, {"Steel", 60}, {"Plasteel", 40}}, {{"Silver", 9}, {"Gold", 8}}}, "", nil)
	if ok {
		t.Fatal("valuables filled a slot", filter)
	}
	filter, ok = gearFilter([][]Amount{{{"Gold", 5}, {"Steel", 60}, {"Plasteel", 40}}}, "", nil)
	if !ok || !reflect.DeepEqual(filter, []Resource{"Plasteel"}) {
		t.Fatal(filter, ok)
	}
	// The loadout may name a valuable.
	filter, ok = gearFilter([][]Amount{{{"Gold", 5}, {"Steel", 60}}}, "Gold", nil)
	if !ok || !reflect.DeepEqual(filter, []Resource{"Gold"}) {
		t.Fatal(filter, ok)
	}
}

func TestGearReviewApparelCensus(t *testing.T) {
	shirt := GearApparel{Definition: "Apparel_BasicShirt", Condition: .3, Groups: []string{"Torso", "Shoulders", "Arms"}}
	pants := GearApparel{Definition: "Apparel_Pants", Condition: 1, Groups: []string{"Legs"}}
	torso := loadoutOption("Apparel_BasicShirt", GearSkinTorso)
	pawn := func(id PawnID, deficit bool, apparel ...GearApparel) GearPawn {
		p := gearDressedPawn(id, torso)
		if deficit {
			p = gearDeficitPawn(id, torso)
		}
		p.Apparel = domain.Known(apparel)
		return p
	}
	v := GearObservation{Pawns: []GearPawn{pawn("a", true, shirt, pants), pawn("b", false, pants), pawn("c", false, GearApparel{Definition: "Apparel_TribalA", Condition: .9, Groups: []string{"Torso", "Legs"}}), pawn("d", false)}}
	review, err := ReviewGear(domain.Known(v))
	if err != nil || review.Deficit != domain.Known(.25) || review.WornOut != domain.Known(.25) || review.Uncovered != domain.Known(.5) {
		t.Fatal(review, err)
	}
	// One pawn with an unobserved wardrobe leaves the apparel census unknown
	// while the model still decides recovery.
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
	shirt := loadoutOption("Apparel_BasicShirt", GearSkinTorso)
	shirt.Stuff = "Cloth"
	pants := loadoutOption("Apparel_Pants", GearSkinLegs)
	v := GearObservation{Pawns: []GearPawn{gearDeficitPawn("a", pants, shirt), gearDressedPawn("b", loadoutOption("Bow_Short", GearPrimary))}}
	needs, err := GearReplacementNeeds(domain.Known(v))
	if err != nil || !reflect.DeepEqual(needs, []Resource{"Apparel_BasicShirt", "Apparel_Pants"}) {
		t.Fatal("deficit pawns' needs, sorted and deduplicated", needs, err)
	}
	// A recovered census and an unknown census ask for nothing.
	v.Pawns[0] = gearDressedPawn("a", pants, shirt)
	if needs, err := GearReplacementNeeds(domain.Known(v)); err != nil || len(needs) != 0 {
		t.Fatal("recovered census requested", needs, err)
	}
	if needs, err := GearReplacementNeeds(domain.Unknown[GearObservation]()); err != nil || len(needs) != 0 {
		t.Fatal("unknown census requested", needs, err)
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
		func(r *GearPlanningRequest) {
			b, _ := r.Benches.Value()
			recipes, _ := b[0].Recipes.Value()
			recipes[0].Ingredients = domain.Known([][]Amount{{{"Cloth", -1}}})
		},
	} {
		r := gearFixture()
		change(&r)
		if _, err := selectProjected(r); err == nil {
			t.Fatal("malformed evidence accepted")
		}
	}
}

func TestGearProductionKeepsUnknownEvidenceUnknown(t *testing.T) {
	for _, change := range []func(*GearPlanningRequest){
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
		m, err := selectProjected(r)
		if err != nil || m.Kind != GearUnknown {
			t.Fatal(m, err)
		}
	}
}
