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
	p := RoutinePolicy{ResearchLadder: base}
	if got := ArmorResearchPolicy(p, true).ResearchLadder; !reflect.DeepEqual(got, want) {
		t.Fatal(got)
	}
	if got := ArmorResearchPolicy(RoutinePolicy{}, true).ResearchLadder; len(got) != 0 {
		t.Fatal("disabled roadmap grew a ladder", got)
	}
	facts := domain.Known(ResearchFacts{Projects: []ResearchProjectID{"Stonecutting", "Electricity", "Smithing", "Batteries", "FlakArmor"}, Finished: []ResearchProjectID{"Stonecutting", "Electricity"}})
	if target, _ := ResearchGoal(ArmorResearchPolicy(p, true), nil, facts); target != "Smithing" {
		t.Fatal("soldier ladder target", target)
	}
	if target, _ := ResearchGoal(p, nil, facts); target != "Batteries" {
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
	budget := []Amount{{"Steel", 500}, {"Plasteel", 20}, {"Cloth", 200}, {ComponentResource, 10}}
	targets := func(research ...string) []Resource {
		t.Helper()
		l, err := PlanGearLoadout(GearLoadoutInput{Role: GearRoleInput{DraftedSquad: true}, Research: research, Budget: budget, Options: options})
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
	p := GearLoadoutInput{Role: GearRoleInput{DraftedSquad: true}, Research: []string{"Smithing", "FlakArmor", "PlateArmor", "ReconArmor"}, Budget: budget}
	if gearEligible(p, plate) {
		t.Fatal("plate is never planned")
	}
	if gearEligible(p, recon) {
		t.Fatal("recon before plasteel and advanced components are funded")
	}
	p.Budget = append(p.Budget, Amount{"Plasteel", 100}, Amount{"ComponentSpacer", 3})
	if !gearEligible(p, recon) {
		t.Fatal("funded recon")
	}
	p.Role = GearRoleInput{}
	if gearEligible(p, vest) {
		t.Fatal("worker armor")
	}
}

func TestGearBudgetRefusesUnderReserve(t *testing.T) {
	stock := []Stock{{Resource: "Steel", Available: domain.Known(int64(100))}, {Resource: "Plasteel", Available: domain.Known(int64(30))}, {Resource: "Cloth", Available: domain.Known(int64(50))}}
	rules := []ResourceRule{{Resource: "Steel", Spending: Allow, Reserve: 80}, {Resource: "Cloth", Spending: Stop}}
	holds := []Amount{{"Plasteel", 25}}
	budget := GearMaterialBudget(stock, rules, holds)
	if !reflect.DeepEqual(budget, []Amount{{"Cloth", 0}, {"Plasteel", 5}, {"Steel", 20}}) {
		t.Fatal(budget)
	}
	helmet := armorOption("Apparel_SimpleHelmet", GearHeadgear, 50, 50, []string{"Smithing"}, Amount{"Steel", 40})
	p := GearLoadoutInput{Role: GearRoleInput{DraftedSquad: true}, Research: []string{"Smithing"}, Budget: budget}
	if gearEligible(p, helmet) {
		t.Fatal("steel under reserve")
	}
	stored := helmet
	stored.ID, stored.Source = "helmet-stored", GearStored
	if !gearEligible(p, stored) {
		t.Fatal("a stored helmet costs no steel")
	}
	p.Budget = GearMaterialBudget(stock, []ResourceRule{{Resource: "Steel", Spending: Allow, Reserve: 60}}, nil)
	if !gearEligible(p, helmet) {
		t.Fatal("steel over reserve")
	}
	p.Budget = nil
	if !gearEligible(p, helmet) {
		t.Fatal("unbudgeted")
	}
	p.Budget = []Amount{}
	if gearEligible(p, helmet) {
		t.Fatal("unmeasured steel is unfunded")
	}
	l, err := PlanGearLoadout(GearLoadoutInput{Role: GearRoleInput{DraftedSquad: true}, Research: []string{"Smithing"}, Budget: budget, Options: []GearOption{helmet}})
	if err != nil || len(l.Gaps) != 0 {
		t.Fatal("budgeted loadout raised a helmet gap", l, err)
	}
	if _, err := PlanGearLoadout(GearLoadoutInput{Budget: []Amount{{"Steel", -1}}}); err == nil {
		t.Fatal("negative budget validated")
	}
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
