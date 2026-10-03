package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func preyRow(id string, x, z int32, revenge float64) AcquisitionSource {
	return AcquisitionSource{ID: id, Resource: "Corpse_" + id, Token: "t", Hunt: true, Food: true, Yield: 1, NutritionYield: 10, RevengeChance: revenge, HerdSize: 1, Cell: domain.Cell{X: x, Z: z}}
}

func TestSquadHuntsGroupMixedSpeciesAndKeepLoneHunters(t *testing.T) {
	rows := []AcquisitionSource{
		preyRow("deer", 10, 10, 0.05), preyRow("hare", 14, 12, 0.01), preyRow("ibex", 18, 10, 0.1),
		preyRow("pair-a", 80, 80, 0.05), preyRow("pair-b", 82, 80, 0.05),
		preyRow("moose", 150, 150, 0.5),
	}
	// A designated or taken animal is not squad prey.
	taken := preyRow("taken", 12, 12, 0.05)
	taken.Taken = true
	rows = append(rows, taken)
	channels, lone := SquadHunts(rows, SquadHuntMinGunners, domain.Fact[float64]{})
	if len(channels) != 2 {
		t.Fatalf("channels = %+v", channels)
	}
	if got := channels[0].Prey; !reflect.DeepEqual(got, []string{"deer", "hare", "ibex"}) {
		t.Fatalf("mixed group = %v", got)
	}
	if got := channels[1].Prey; !reflect.DeepEqual(got, []string{"moose"}) {
		t.Fatalf("high-revenge single = %v", got)
	}
	if n, _ := channels[0].NutritionPerDay.Value(); n != 30 {
		t.Fatalf("group nutrition = %v", n)
	}
	if channels[0].Kind != FoodHunt || len(channels[0].Risk) != 1 || channels[0].Risk[0].Kind != FoodRevenge {
		t.Fatalf("channel risk = %+v", channels[0])
	}
	// The pair stays with designation hunting, as does a taken animal.
	var ids []string
	for _, s := range lone {
		ids = append(ids, s.ID)
	}
	if !reflect.DeepEqual(ids, []string{"pair-a", "pair-b", "taken"}) {
		t.Fatalf("lone = %v", ids)
	}
	// Without a squad's gunners nothing is grouped.
	if channels, lone = SquadHunts(rows, SquadHuntMinGunners-1, domain.Fact[float64]{}); len(channels) != 0 || len(lone) != len(rows) {
		t.Fatalf("squad without gunners: %v", channels)
	}
}

func TestHuntRequestAndCombatCleared(t *testing.T) {
	rows := []AcquisitionSource{preyRow("a", 1, 1, 0.05), preyRow("b", 2, 2, 0.05), preyRow("c", 3, 3, 0.05)}
	squads, _ := SquadHunts(rows, 4, domain.Fact[float64]{})
	plan := domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{{Channel: squads[0], Decision: FoodPlanOpen}}})
	if got := HuntRequest(plan); !reflect.DeepEqual(got, []domain.PawnID{"a", "b", "c"}) {
		t.Fatalf("request = %v", got)
	}
	held := domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{{Channel: squads[0], Decision: FoodPlanHold}}})
	if len(HuntRequest(held)) != 0 || len(HuntRequest(domain.Unknown[FoodPlan]())) != 0 {
		t.Fatal("a held or unknown plan requested a hunt")
	}
	f := RoutineFacts{Hostiles: domain.Known(int64(0)), FoodPlan: plan}
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
		view.Pawns = append(view.Pawns, CombatPawnState{ID: id, Cell: domain.Known(domain.Cell{X: 4, Z: 30}), Stance: StanceIdle, WeaponRange: 30})
	}
	if got := len(huntFormation(view)); got != SquadHuntMaxGunners {
		t.Fatalf("squad of %d", got)
	}
	view.Defenders = view.Defenders[:SquadHuntMinGunners-1]
	if got := huntFormation(view); got != nil {
		t.Fatalf("squad of %d gunners formed: %+v", len(view.Defenders), got)
	}
}
