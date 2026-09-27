package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func craftPawn(id domain.PawnID) policy.EquipCandidatePawn {
	return policy.EquipCandidatePawn{Pawn: id, Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), IncapableOfViolence: domain.Known(false), Armed: domain.Known(false), Profile: policy.PawnProfile{Skills: map[string]policy.ProfileSkill{"Shooting": {Level: 2}, "Melee": {Level: 8}}}}
}

// A tribal start with no bench: the unarmed colonist gets a crafting spot,
// then a club bill on it, and an active weapon bill is waited on.
func TestSelectWeaponBillSpotThenBill(t *testing.T) {
	pawns := []policy.EquipCandidatePawn{craftPawn("a")}
	if got := selectWeaponBill(pawns, nil, nil); got.reason != weaponBillNoBench {
		t.Fatal("no bench did not ask for a crafting spot", got)
	}
	butcher := policy.GearBench{ID: "butcher", Bills: domain.Known([]policy.GearBill{}), Recipes: domain.Known([]policy.GearRecipe{{Definition: "ButcherCorpseFlesh", Available: domain.Known(true), AvailableOn: domain.Known(true)}})}
	if got := selectWeaponBill(pawns, nil, []policy.GearBench{butcher}); got.reason != weaponBillNoBench {
		t.Fatal("a bench without weapon recipes counted as a weapon bench", got)
	}
	spot := policy.GearBench{ID: "spot", Bills: domain.Known([]policy.GearBill{}), Recipes: domain.Known([]policy.GearRecipe{
		{Definition: "Make_MeleeWeapon_Club", Products: []policy.Resource{"MeleeWeapon_Club"}, Available: domain.Known(true), AvailableOn: domain.Known(true)},
	})}
	got := selectWeaponBill(pawns, nil, []policy.GearBench{butcher, spot})
	if got.reason != weaponBillAdd || got.bench != "spot" || got.recipe != "Make_MeleeWeapon_Club" || got.count != 1 {
		t.Fatal(got)
	}
	spot.Bills = domain.Known([]policy.GearBill{{ID: "b", Recipe: "Make_MeleeWeapon_Club", Active: domain.Known(true), Products: []policy.Resource{"MeleeWeapon_Club"}}})
	if got := selectWeaponBill(pawns, nil, []policy.GearBench{spot}); got.reason != weaponBillNone {
		t.Fatal("active weapon bill not waited on", got)
	}
	spot.Bills = domain.Unknown[[]policy.GearBill]()
	if got := selectWeaponBill(pawns, nil, []policy.GearBench{spot}); got.reason != weaponBillNone {
		t.Fatal("unknown bills admitted a bill", got)
	}
}

func TestCraftingSpotSelection(t *testing.T) {
	r := &RoutineBuildingPlanner{goal: policy.EnsureBasicDefense, definition: craftingSpotDefinition}
	f := observation.ColonyProjection{Facts: policy.RoutineFacts{Colonists: domain.Known(int64(3))}}
	if n, id, reason := r.selection(f); n != 1 || id != "crafting-spot" || reason != "" {
		t.Fatal(n, id, reason)
	}
}
