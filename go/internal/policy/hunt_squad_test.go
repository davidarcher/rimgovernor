package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func preyRow(id string, x, z int32, revenge float64) AcquisitionSource {
	return AcquisitionSource{ID: id, Resource: "Corpse_" + id, Token: "t", Hunt: true, Food: true, Yield: 1, NutritionYield: 10, RevengeChance: revenge, HerdSize: 1, Cell: domain.Cell{X: x, Z: z}}
}

func huntByID(channels []FoodChannel) map[string]FoodChannel {
	out := map[string]FoodChannel{}
	for _, c := range channels {
		out[c.ID] = c
	}
	return out
}

func TestHuntCandidatesOnePerGroupWithMode(t *testing.T) {
	rows := []AcquisitionSource{
		preyRow("deer", 10, 10, 0.05), preyRow("hare", 14, 12, 0.01), preyRow("ibex", 18, 10, 0.1),
		preyRow("pair-a", 80, 80, 0.05), preyRow("pair-b", 82, 80, 0.05),
		preyRow("moose", 150, 150, 0.5),
	}
	// A taken animal is not squad prey: it stays a lone candidate.
	taken := preyRow("taken", 12, 12, 0.05)
	taken.Taken = true
	rows = append(rows, taken)
	by := huntByID(HuntCandidates(rows, SquadHuntMinGunners, domain.Fact[float64]{}))
	if len(by) != 5 {
		t.Fatalf("candidates = %+v", by)
	}
	if g := by["squad:deer"]; g.Mode() != HuntFormation || !reflect.DeepEqual(g.Prey, []string{"deer", "hare", "ibex"}) {
		t.Fatalf("mixed group = %+v", g)
	}
	if g := by["squad:moose"]; g.Mode() != HuntFormation || !reflect.DeepEqual(g.Prey, []string{"moose"}) {
		t.Fatalf("high-revenge single = %+v", g)
	}
	for _, id := range []string{"pair-a", "pair-b", "taken"} {
		if by[id].Mode() != HuntLone || by[id].Prey != nil {
			t.Fatalf("%s = %+v", id, by[id])
		}
	}
	g := by["squad:deer"]
	if n, _ := g.NutritionPerDay.Value(); n != 30 {
		t.Fatalf("group nutrition = %v", n)
	}
	if g.Kind != FoodHunt || len(g.Risk) != 1 || g.Risk[0].Kind != FoodRevenge {
		t.Fatalf("channel risk = %+v", g)
	}
}

// Below the squad's gunners a formation is a Hold row carrying needs_gunners,
// never a vanished candidate and never one that yields.
func TestHuntFormationHoldsWithoutGunners(t *testing.T) {
	rows := []AcquisitionSource{preyRow("a", 1, 1, 0.05), preyRow("b", 2, 2, 0.05), preyRow("c", 3, 3, 0.05), preyRow("moose", 150, 150, 0.5), preyRow("hare", 90, 90, 0.01)}
	by := huntByID(HuntCandidates(rows, SquadHuntMinGunners-1, domain.Fact[float64]{}))
	if len(by) != 3 {
		t.Fatalf("candidates = %+v", by)
	}
	for _, id := range []string{"squad:a", "squad:moose"} {
		c := by[id]
		terms := map[string]float64{}
		for _, term := range c.Terms {
			terms[term.Name] = term.Value
		}
		if n, _ := c.NutritionPerDay.Value(); c.Mode() != HuntFormation || n != 0 || terms["needs_gunners"] != 1 || terms["held_nutrition_per_day"] <= 0 {
			t.Fatalf("%s = %+v", id, c)
		}
	}
	if hare := by["hare"]; hare.Mode() != HuntLone {
		t.Fatalf("hare = %+v", hare)
	}
	plan, err := SupplyFoodPlan(FoodPlanRequest{Demand: foodPlanRequest().Demand, MinDays: 2, TargetDays: 5, Channels: domain.Known(HuntCandidates(rows, 0, domain.Fact[float64]{})), Labor: domain.Known(1e6)})
	if err != nil {
		t.Fatal(err)
	}
	for _, e := range plan.Portfolio {
		if e.Channel.Mode() == HuntFormation && e.Decision != FoodPlanHold {
			t.Fatalf("formation without gunners is %s: %+v", e.Decision, e)
		}
	}
}

// A hunt yields the animal's meat and its butcher products; labor is one cost.
func TestHuntCandidateYieldsMeatAndLeather(t *testing.T) {
	deer := preyRow("deer", 10, 10, 0.05)
	deer.Products = []SourceProduct{{Def: "Leather_Plain", Amount: 40}}
	hare := preyRow("hare", 50, 50, 0.01)
	hare.Products = []SourceProduct{{Def: "Leather_Light", Amount: 5}}
	by := huntByID(HuntCandidates([]AcquisitionSource{deer, hare}, 0, domain.Fact[float64]{}))
	candidate := SupplyCandidateOfFood(by["deer"])
	if len(candidate.Yields) != 2 || candidate.Yields[0].Good.Def != CandidateNutrition || candidate.Yields[1].Good.Def != "Leather_Plain" {
		t.Fatalf("yields = %+v", candidate.Yields)
	}
	if leather, _ := candidate.Yields[1].PerDay.Value(); leather != 40 {
		t.Fatalf("leather = %v", leather)
	}
	if back, ok := FoodChannelOfSupply(candidate); !ok || !reflect.DeepEqual(back.Products, by["deer"].Products) {
		t.Fatalf("round trip = %+v %v", back, ok)
	}
	// A formation sums its animals' products.
	rows := []AcquisitionSource{deer, preyRow("b", 11, 11, 0.05), preyRow("c", 12, 12, 0.05)}
	rows[1].Products = []SourceProduct{{Def: "Leather_Plain", Amount: 2}}
	g := HuntCandidates(rows, SquadHuntMinGunners, domain.Fact[float64]{})[0]
	if g.Mode() != HuntFormation || len(g.Products) != 1 || g.Products[0].PerDay != domain.Known(42.0) {
		t.Fatalf("formation products = %+v", g)
	}
}

func TestHuntRequestAndCombatCleared(t *testing.T) {
	rows := []AcquisitionSource{preyRow("a", 1, 1, 0.05), preyRow("b", 2, 2, 0.05), preyRow("c", 3, 3, 0.05)}
	squads := HuntCandidates(rows, 4, domain.Fact[float64]{})
	plan := domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{{Channel: squads[0], Decision: FoodPlanOpen}}})
	if got := HuntRequest(plan); !reflect.DeepEqual(got, []domain.PawnID{"a", "b", "c"}) {
		t.Fatalf("request = %v", got)
	}
	held := domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{{Channel: squads[0], Decision: FoodPlanHold}}})
	if len(HuntRequest(held)) != 0 || len(HuntRequest(domain.Unknown[FoodPlan]())) != 0 {
		t.Fatal("a held or unknown plan requested a hunt")
	}
	f := RoundsFacts{Hostiles: domain.Known(int64(0)), FoodPlan: plan}
	if positive(combatCleared(f)) {
		t.Fatal("an open squad hunt left ActiveCombat cleared")
	}
	f.Hostiles = domain.Known(int64(2))
	if positive(combatCleared(f)) {
		t.Fatal("hostiles left ActiveCombat cleared")
	}
	f.Hostiles = domain.Known(int64(0))
	f.FoodPlan = held
	if !positive(combatCleared(f)) {
		t.Fatal("no hunt and no hostile left ActiveCombat open")
	}
}

func TestHuntFormationCapsTheSquad(t *testing.T) {
	view := huntView(wildGroup()...)
	for _, id := range []domain.PawnID{"d", "e", "f"} {
		view.Defenders = append(view.Defenders, combatRifleman(id))
		view.Pawns = append(view.Pawns, CombatPawnState{ID: id, Cell: domain.Known(domain.Cell{X: 4, Z: 30}), Stance: StanceIdle, WeaponRange: 30, WeaponFacts: WeaponDef{Ranged: true, Range: 30}})
	}
	if got := len(huntFormation(view)); got != SquadHuntMaxGunners {
		t.Fatalf("squad of %d", got)
	}
	view.Defenders = view.Defenders[:SquadHuntMinGunners-1]
	if got := huntFormation(view); got != nil {
		t.Fatalf("squad of %d gunners formed: %+v", len(view.Defenders), got)
	}
}

// One predicate: a launcher (explosive) is no hunting weapon, so it neither
// counts toward the channel's gunners nor stands in the formation.
func TestHuntPredicateIsOneAcrossChannelAndTactic(t *testing.T) {
	launcher := WeaponDef{Ranged: true, Explosive: true, Range: 30}
	if launcher.Hunts() || !(WeaponDef{Ranged: true}).Hunts() || (WeaponDef{Ranged: true, Incendiary: true}).Hunts() || (WeaponDef{Melee: true}).Hunts() {
		t.Fatal("WeaponDef.Hunts is not the vanilla rule")
	}
	profiles := []PawnProfile{{ID: "a", Hunts: true}, {ID: "b", Ranged: true}, {ID: "c", Hunts: true}}
	for i := range profiles {
		profiles[i].Skills = nil
	}
	if got := SquadGunners(profiles); got > 2 {
		t.Fatalf("a non-hunting ranged pawn counted: %d", got)
	}
	view := huntView(wildGroup()...)
	view.Pawns[0].WeaponFacts = launcher
	if got := huntFormation(view); got != nil {
		t.Fatalf("a launcher pawn formed with a squad of two: %+v", got)
	}
}

// A lone bow hunter is not held by the formation gate: its single non-
// retaliating prey is a lone candidate that needs no gunners.
func TestLoneHunterIsNotGatedByFormation(t *testing.T) {
	rows := []AcquisitionSource{{ID: "deer", Hunt: true, Food: true, NutritionYield: 100}}
	got := HuntCandidates(rows, 1, domain.Fact[float64]{})
	if len(got) != 1 || got[0].Mode() != HuntLone {
		t.Fatalf("%+v", got)
	}
	for _, term := range got[0].Terms {
		if term.Name == "needs_gunners" {
			t.Fatalf("lone hunt held: %+v", got[0].Terms)
		}
	}
}
