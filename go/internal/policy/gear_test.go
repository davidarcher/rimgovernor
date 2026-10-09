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
	return selectGear(r, v)
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
