package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func knownTrue() domain.Fact[bool]  { return domain.Known(true) }
func knownFalse() domain.Fact[bool] { return domain.Known(false) }

func eligibleHauler(pawn domain.PawnID) SecureSuppliesHaulerFacts {
	return SecureSuppliesHaulerFacts{Pawn: pawn, Dead: knownFalse(), Downed: knownFalse(), Drafted: knownFalse(),
		MentalState: knownFalse(), PlayerForced: knownFalse(), NeedsTend: knownFalse(), Bleeding: knownFalse(),
		HaulingEnabled: knownTrue()}
}

func TestSelectSecureSuppliesPicksTopItemAndLowestEligiblePawn(t *testing.T) {
	items := []UpkeepItem{{ID: "meal", Definition: "MealSimple"}, {ID: "wood", Definition: "WoodLog"}}
	pawns := []SecureSuppliesHaulerFacts{eligibleHauler("z"), eligibleHauler("a")}
	item, pawn, ok := SelectSecureSupplies(items, pawns)
	if !ok || item.ID != "meal" || pawn != "a" {
		t.Fatal(item, pawn, ok)
	}
}

func TestSelectSecureSuppliesExcludesIneligibleHaulers(t *testing.T) {
	items := []UpkeepItem{{ID: "meal"}}
	base := eligibleHauler("a")
	for _, mutate := range []func(*SecureSuppliesHaulerFacts){
		func(p *SecureSuppliesHaulerFacts) { p.Dead = knownTrue() },
		func(p *SecureSuppliesHaulerFacts) { p.Downed = knownTrue() },
		func(p *SecureSuppliesHaulerFacts) { p.Drafted = knownTrue() },
		func(p *SecureSuppliesHaulerFacts) { p.MentalState = knownTrue() },
		func(p *SecureSuppliesHaulerFacts) { p.PlayerForced = knownTrue() },
		func(p *SecureSuppliesHaulerFacts) { p.NeedsTend = knownTrue() },
		func(p *SecureSuppliesHaulerFacts) { p.Bleeding = knownTrue() },
		func(p *SecureSuppliesHaulerFacts) { p.HaulingEnabled = knownFalse() },
		func(p *SecureSuppliesHaulerFacts) { p.HaulingEnabled = domain.Unknown[bool]() },
	} {
		p := base
		mutate(&p)
		if _, _, ok := SelectSecureSupplies(items, []SecureSuppliesHaulerFacts{p}); ok {
			t.Fatal("ineligible hauler selected", p)
		}
	}
}

func TestSelectSecureSuppliesRequiresItemsAndPawns(t *testing.T) {
	if _, _, ok := SelectSecureSupplies(nil, []SecureSuppliesHaulerFacts{eligibleHauler("a")}); ok {
		t.Fatal("selected with no items")
	}
	if _, _, ok := SelectSecureSupplies([]UpkeepItem{{ID: "meal"}}, nil); ok {
		t.Fatal("selected with no pawns")
	}
}
