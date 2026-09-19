package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"slices"
	"testing"
)

func TestFoodPolicyFreshEligibilityAndUnknown(t *testing.T) {
	p := WorkPawn{Available: domain.Known(true), FoodRestriction: domain.Known(FoodRestriction{PolicyID: "diet", Allowed: []string{"Rice"}, Eligible: []string{"Rice", "MealSimple"}})}
	if got := FoodPolicyChanges(p); !slices.Equal(got, []string{"MealSimple"}) {
		t.Fatal(got)
	}
	p.FoodRestriction = domain.Known(FoodRestriction{PolicyID: "diet", Allowed: []string{"Rice", "MealSimple"}, Eligible: []string{"Rice", "MealSimple"}})
	if len(FoodPolicyChanges(p)) != 0 {
		t.Fatal("settled diet changed")
	}
	// The same saved filter may be edited repeatedly, without new identity.
	p.FoodRestriction = domain.Known(FoodRestriction{PolicyID: "diet", Eligible: []string{"Rice"}})
	if got := FoodPolicyChanges(p); !slices.Equal(got, []string{"Rice"}) {
		t.Fatal(got)
	}
	p.Available = domain.Known(false)
	if len(FoodPolicyChanges(p)) != 0 {
		t.Fatal("unavailable pawn changed")
	}
	p.Available = domain.Known(true)
	p.FoodRestriction = domain.Unknown[FoodRestriction]()
	if len(FoodPolicyChanges(p)) != 0 {
		t.Fatal("unknown diet changed")
	}
}
