package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func shareMember(id string, free bool, spent domain.Fact[float64], effects TraitEffects) ShareMember {
	return ShareMember{Profile: PawnProfile{ID: PawnID(id), Effects: effects}, Free: free, Spent: spent}
}

func TestPersonalPoolExcludesPawnsAndFailsClosed(t *testing.T) {
	got, ok := PersonalPool(domain.Known(WealthFacts{Items: 100, Buildings: 50, Pawns: 9999, Total: 10149})).Value()
	if !ok || got != 150 {
		t.Fatalf("pool %v %v", got, ok)
	}
	for name, w := range map[string]domain.Fact[WealthFacts]{
		"unknown":  domain.Unknown[WealthFacts](),
		"negative": domain.Known(WealthFacts{Items: -1, Buildings: 5}),
		"NaN":      domain.Known(WealthFacts{Items: math.NaN()}),
		"inf":      domain.Known(WealthFacts{Buildings: math.Inf(1)}),
	} {
		if _, ok := PersonalPool(w).Value(); ok {
			t.Errorf("%s pool known", name)
		}
	}
}

func TestPersonalShareFractionBelowOne(t *testing.T) {
	if PersonalShareFraction <= 0 || PersonalShareFraction >= 1 {
		t.Fatalf("PersonalShareFraction %v must be in (0,1)", PersonalShareFraction)
	}
}

func TestShareWeights(t *testing.T) {
	unknown := domain.Unknown[float64]()
	for _, c := range []struct {
		name string
		m    ShareMember
		want float64
	}{
		{"base", shareMember("a", true, unknown, TraitEffects{}), 1},
		{"soldier", ShareMember{Free: true, Soldier: true}, 1.25},
		{"doctor", ShareMember{Free: true, Doctor: true}, 1.25},
		{"greedy", shareMember("a", true, unknown, TraitEffects{Greedy: true}), 1.5},
		{"jealous", shareMember("a", true, unknown, TraitEffects{Jealous: true}), 1.25},
		{"ascetic", shareMember("a", true, unknown, TraitEffects{Ascetic: true}), 0.5},
		{"soldier greedy", ShareMember{Free: true, Soldier: true, Profile: PawnProfile{Effects: TraitEffects{Greedy: true}}}, 1.75},
	} {
		if got := ShareWeight(c.m); math.Abs(got-c.want) > 1e-9 {
			t.Errorf("%s: %v want %v", c.name, got, c.want)
		}
	}
}

func TestPersonalSharesDivideByWeightAndExcludeNonFree(t *testing.T) {
	members := []ShareMember{
		shareMember("a", true, domain.Known(10.0), TraitEffects{}),
		shareMember("b", true, domain.Known(0.0), TraitEffects{Ascetic: true}),
		shareMember("slave", false, domain.Known(0.0), TraitEffects{}),
	}
	shares := PersonalShares(domain.Known(1500.0), members)
	// f x pool = 300, split 1 : 0.5.
	if got, _ := shares["a"].Share.Value(); math.Abs(got-200) > 1e-9 {
		t.Errorf("a share %v", got)
	}
	if got, _ := shares["b"].Share.Value(); math.Abs(got-100) > 1e-9 {
		t.Errorf("b share %v", got)
	}
	if got, ok := shares["a"].Remaining.Value(); !ok || math.Abs(got-190) > 1e-9 {
		t.Errorf("a remaining %v %v", got, ok)
	}
	if got, ok := shares["slave"].Share.Value(); !ok || got != 0 {
		t.Errorf("slave share %v %v", got, ok)
	}
	if shares["slave"].Allows(1) {
		t.Error("slave afforded an upgrade")
	}
	if !shares["slave"].Allows(0) {
		t.Error("necessity refused")
	}
	if !shares["a"].Allows(190) || shares["a"].Allows(190.5) {
		t.Error("a's gate is not its remaining")
	}
}

func TestPersonalShareRemainingNeverNegative(t *testing.T) {
	shares := PersonalShares(domain.Known(100.0), []ShareMember{shareMember("a", true, domain.Known(500.0), TraitEffects{})})
	if got, ok := shares["a"].Remaining.Value(); !ok || got != 0 {
		t.Fatalf("remaining %v %v", got, ok)
	}
}

func TestPersonalShareUnknownInputsFailClosed(t *testing.T) {
	unknownPool := PersonalShares(domain.Unknown[float64](), []ShareMember{shareMember("a", true, domain.Known(0.0), TraitEffects{})})
	unknownSpent := PersonalShares(domain.Known(1000.0), []ShareMember{shareMember("a", true, domain.Unknown[float64](), TraitEffects{})})
	for name, s := range map[string]PersonalShare{"pool": unknownPool["a"], "spent": unknownSpent["a"]} {
		if _, ok := s.Remaining.Value(); ok {
			t.Errorf("unknown %s: remaining known", name)
		}
		if s.Allows(0.01) {
			t.Errorf("unknown %s: upgrade allowed", name)
		}
	}
}

func TestZeroPersonalShareIsUngated(t *testing.T) {
	if !(PersonalShare{}).Allows(1e9) {
		t.Fatal("zero-value share gated")
	}
}
