package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestArmorResearchLadderOrdering(t *testing.T) {
	base := DefaultResearchLadder()
	if got := ArmorResearchLadder(base, false); !reflect.DeepEqual(got, base) {
		t.Fatal("no soldier changed the ladder", got)
	}
	want := []string{"Stonecutting", "Electricity", "Smithing", "ComplexClothing", "FlakArmor", "Shields", "Batteries", "GeothermalPower", "SolarPanels", "CarpetMaking", "Machining", "Gunsmithing"}
	if got := ArmorResearchLadder(base, true); !reflect.DeepEqual(got, want) {
		t.Fatal(got)
	}
	if got := ArmorResearchLadder([]string{"Stonecutting"}, true); !reflect.DeepEqual(got, []string{"Smithing", "ComplexClothing", "FlakArmor", "Shields", "Stonecutting"}) {
		t.Fatal("no Electricity rung", got)
	}
	p := RoundsPolicy{ResearchLadder: base}
	if got := ArmorResearchPolicy(p, true).ResearchLadder; !reflect.DeepEqual(got, want) {
		t.Fatal(got)
	}
	if got := ArmorResearchPolicy(RoundsPolicy{}, true).ResearchLadder; len(got) != 0 {
		t.Fatal("disabled roadmap grew a ladder", got)
	}
	facts := domain.Known(ResearchFacts{Projects: []ResearchProjectID{"Stonecutting", "Electricity", "Smithing", "Batteries", "FlakArmor"}, Finished: []ResearchProjectID{"Stonecutting", "Electricity"}})
	if target, _ := ResearchConcern(ArmorResearchPolicy(p, true), nil, facts); target != "Smithing" {
		t.Fatal("soldier ladder target", target)
	}
	if target, _ := ResearchConcern(p, nil, facts); target != "Batteries" {
		t.Fatal("civilian ladder target", target)
	}
}

func TestGearSoldierPresentAndLatch(t *testing.T) {
	if GearSoldierPresent(domain.Unknown[GearObservation]()) {
		t.Fatal("unknown census")
	}
	worker := GearPawn{Pawn: "a", Policy: domain.Known(ApparelPolicyState{})}
	soldier := GearPawn{Pawn: "b", Policy: domain.Known(ApparelPolicyState{Role: GearRoleInput{DraftedSquad: true}})}
	if GearSoldierPresent(domain.Known(GearObservation{Pawns: []GearPawn{worker}})) {
		t.Fatal("worker only")
	}
	if !GearSoldierPresent(domain.Known(GearObservation{Pawns: []GearPawn{worker, soldier}})) {
		t.Fatal("drafted squad")
	}
	modeled := GearPawn{Pawn: "c", LoadoutModel: domain.Known(GearLoadoutInput{Role: GearRoleInput{DraftedSquad: true}})}
	if !GearSoldierPresent(domain.Known(GearObservation{Pawns: []GearPawn{modeled}})) {
		t.Fatal("modeled soldier")
	}
}

func armorOption(id string, slot GearSlot, sharp, blunt float64, research []string, ingredients ...Amount) GearOption {
	o := loadoutOption(id, slot)
	o.Sharp, o.Blunt = sharp, blunt
	o.Research = research
	o.Ingredients = ingredients
	return o
}

func TestSoldierArmorGapProgression(t *testing.T) {
	helmet := armorOption("Apparel_SimpleHelmet", GearHeadgear, 50, 50, []string{"Smithing"}, Amount{"Steel", 40})
	flakHelmet := armorOption("Apparel_AdvancedHelmet", GearHeadgear, 70, 70, []string{"FlakArmor"}, Amount{"Steel", 40}, Amount{"Plasteel", 10}, Amount{ComponentResource, 2})
	vest := armorOption("Apparel_FlakVest", GearMiddleTorso, 100, 36, []string{"FlakArmor"}, Amount{"Steel", 60}, Amount{"Cloth", 30}, Amount{ComponentResource, 1})
	vest.MoveSpeed = -.12
	jacket := armorOption("Apparel_FlakJacket", GearOuter, 55, 8, []string{"FlakArmor"}, Amount{"Steel", 60})
	pants := armorOption("Apparel_FlakPants", GearSkinLegs, 55, 8, []string{"FlakArmor"}, Amount{"Steel", 60})
	plate := armorOption("Apparel_PlateArmor", GearOuter, 90, 90, []string{"PlateArmor"}, Amount{"Steel", 170})
	plate.MoveSpeed = -.8
	recon := armorOption("Apparel_PowerArmor", GearOuter, 92, 40, []string{"ReconArmor"}, Amount{"Plasteel", 80}, Amount{"ComponentSpacer", 3})
	recon.Layers, recon.Groups = []string{"Middle", "Shell"}, []string{"Torso", "Legs"}
	options := []GearOption{helmet, flakHelmet, vest, jacket, pants, plate, recon}
	targets := func(research ...string) []Resource {
		t.Helper()
		l, err := PlanGearLoadout(GearLoadoutInput{Role: GearRoleInput{DraftedSquad: true}, Research: research, Options: options})
		if err != nil {
			t.Fatal(err)
		}
		out := []Resource{}
		for _, g := range l.Gaps {
			out = append(out, g.Wanted.Definition)
		}
		return out
	}
	if got := targets(); len(got) != 0 {
		t.Fatal("no research", got)
	}
	if got := targets("Smithing"); !reflect.DeepEqual(got, []Resource{"Apparel_SimpleHelmet"}) {
		t.Fatal("smithing", got)
	}
	got := targets("Smithing", "FlakArmor", "PlateArmor", "ReconArmor")
	if len(got) != 4 || got[0] != "Apparel_FlakVest" {
		t.Fatal("flak ladder", got)
	}
	for _, def := range got {
		if def == "Apparel_SimpleHelmet" || def == "Apparel_PlateArmor" || def == "Apparel_PowerArmor" {
			t.Fatal("flak ladder", got)
		}
	}
	p := GearLoadoutInput{Role: GearRoleInput{DraftedSquad: true}, Research: []string{"Smithing", "FlakArmor", "PlateArmor", "ReconArmor"}}
	if gearEligible(p, plate) {
		t.Fatal("plate is never planned")
	}
	if !gearEligible(p, recon) {
		t.Fatal("recon is planned; its materials are demand, not a gate")
	}
	p.Role = GearRoleInput{}
	if gearEligible(p, vest) {
		t.Fatal("worker armor")
	}
}

func TestGearOptionZeroIngredientRefused(t *testing.T) {
	if _, err := PlanGearLoadout(GearLoadoutInput{Options: []GearOption{armorOption("x", GearOuter, 0, 0, nil, Amount{"Steel", 0})}}); err == nil {
		t.Fatal("zero ingredient validated")
	}
}

func TestGearUtilityExtras(t *testing.T) {
	shield := loadoutOption("shield", GearBelt)
	shield.Shield = true
	smoke := loadoutOption("smoke", GearBelt)
	smoke.Smokepop = true
	foil := loadoutOption("foil", GearHeadgear)
	foil.Psychic = true
	medic := GearLoadoutInput{Role: GearRoleInput{Work: WorkPawn{Work: domain.Known([]WorkPriority{{Work: WorkDoctor, Priority: 1}, {Work: WorkConstruction, Priority: 2}})}}}
	if !gearEligible(medic, shield) {
		t.Fatal("medic shield")
	}
	builder := GearLoadoutInput{Role: GearRoleInput{Work: WorkPawn{Work: domain.Known([]WorkPriority{{Work: WorkDoctor, Priority: 2}, {Work: WorkConstruction, Priority: 1}})}}}
	if gearEligible(builder, shield) {
		t.Fatal("builder shield")
	}
	melee := GearLoadoutInput{Role: GearRoleInput{DraftedSquad: true, Work: WorkPawn{Skills: domain.Known([]WorkSkill{{Name: "Melee", Level: 8}})}}}
	if !gearEligible(melee, shield) || gearEligible(melee, smoke) {
		t.Fatal("melee soldier belts")
	}
	if gearEligible(melee, foil) {
		t.Fatal("foil helmet without a drone")
	}
	melee.PsychicDrone = true
	if !gearEligible(melee, foil) {
		t.Fatal("foil helmet after a drone")
	}
}

func armoryArmorFixture() GearPlanningRequest {
	r := gearModelFixture()
	v, _ := r.Observation.Value()
	armor := armorOption("Apparel_PowerArmor", GearOuter, 1, 1, nil)
	v.Pawns[0] = gearDeficitPawn("pawn", armor)
	p := v.Pawns[0]
	p.Pawn = "second"
	v.Pawns = append(v.Pawns, p)
	r.Observation = domain.Known(v)
	recipe := func(def string, product Resource, slots [][]Amount) GearRecipe {
		return GearRecipe{Definition: def, Products: []Resource{product}, Available: domain.Known(true), AvailableOn: domain.Known(true), Ingredients: domain.Known(slots), RequiredWork: domain.Known([]WorkRequirement{})}
	}
	r.Benches = domain.Known([]GearBench{{ID: "fab", Bills: domain.Known([]GearBill{}), Recipes: domain.Known([]GearRecipe{
		recipe("Make_Apparel_PowerArmor", "Apparel_PowerArmor", [][]Amount{{{"Plasteel", 100}}, {{"ComponentSpacer", 2}}}),
		recipe("Make_Apparel_ArmorRecon", "Apparel_ArmorRecon", [][]Amount{{{"Plasteel", 60}}}),
		recipe("Make_Apparel_FlakVest", "Apparel_FlakVest", [][]Amount{{{"Steel", 60}}}),
	})}})
	return r
}

func TestArmoryArmorLadderByTier(t *testing.T) {
	for tier, want := range map[ArmoryTier]Resource{ArmoryTierFabrication: "Apparel_PowerArmor", ArmoryTierMachining: "Apparel_FlakVest", ArmoryTierSmithing: "", ArmoryTierNeolithic: ""} {
		m, err := SelectArmoryArmorMethod(armoryArmorFixture(), tier)
		if err != nil || want == "" && m.Kind != GearBlocked || want != "" && (m.Kind != GearProduce || m.Need.Definition != want || m.Count != 2) {
			t.Fatal(tier, m, err)
		}
	}
	if m, err := SelectArmoryArmorMethod(armoryArmorFixture(), ArmoryTierUnknown); err != nil || m.Kind != GearUnknown {
		t.Fatal(m, err)
	}
}

// An armor bill is placed with nothing in stock and its materials become
// demand; the bill never waits on, or is held back by, its own demand (#2373).
func TestArmoryArmorBillPlacedBeforeStockBecomesDemand(t *testing.T) {
	r := armoryArmorFixture()
	m, err := SelectArmoryArmorMethod(r, ArmoryTierFabrication)
	if err != nil || m.Kind != GearProduce || m.Need.Definition != "Apparel_PowerArmor" || m.Count != 2 || !reflect.DeepEqual(m.Filter, []Resource{"ComponentSpacer", "Plasteel"}) {
		t.Fatal(m, err)
	}
	benches, _ := r.Benches.Value()
	recipes, _ := benches[0].Recipes.Value()
	slots, _ := recipes[0].Ingredients.Value()
	filter := make([]string, len(m.Filter))
	for i, f := range m.Filter {
		filter[i] = string(f)
	}
	bill := OpenBill{Recipe: m.Recipe, Count: m.Count, Filter: filter, Slots: domain.Known(slots)}
	empty := StockReader{Resources: domain.Known([]Amount{})}
	demand := OpenBillDemand([]OpenBill{bill}, empty)
	if demand["Plasteel"] != 200 || demand["ComponentSpacer"] != 4 || len(demand) != 2 {
		t.Fatal(demand)
	}
	// The same selection stands whatever the demand, so a floor raised by the
	// bill's own ingredients cannot starve it.
	again, err := SelectArmoryArmorMethod(r, ArmoryTierFabrication)
	if err != nil || !reflect.DeepEqual(again, m) {
		t.Fatal(again, err)
	}
}
func TestGearLeavesArmorBillsToArmory(t *testing.T) {
	r := armoryArmorFixture()
	if m, err := SelectGearMethod(r); err != nil || m.Kind == GearProduce {
		t.Fatal(m, err)
	}
}
