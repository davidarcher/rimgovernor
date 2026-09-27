package bridge

import (
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestFoodPolicyObservation(t *testing.T) {
	diet := &o.PawnSettings{FoodRestriction: &o.FoodRestriction{PolicyId: proto.String("diet"), AllowedDefs: []string{"Rice"}, EligibleDefs: []string{"Rice", "MealSimple"}}}
	if err := validateSettings(diet, true, false, false); err != nil {
		t.Fatal(err)
	}
	if err := validateSettings(diet, false, true, false); err == nil {
		t.Fatal("unrequested diet accepted")
	}
	diet.FoodRestriction.EligibleDefs = []string{"Rice", "Rice"}
	if err := validateSettings(diet, true, false, false); err == nil {
		t.Fatal("duplicate diet definition accepted")
	}
}
