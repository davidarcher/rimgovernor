package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestFoodPolicyObservationAndEvidence(t *testing.T) {
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
	assignment, err := domain.NewFoodAssignment("pawn", "before", []string{"MealSimple"})
	if err != nil {
		t.Fatal(err)
	}
	if got := workOperation(assignment).GetPatchPawn().GetFoodAllow(); len(got.GetDefs()) != 1 || got.Defs[0] != "MealSimple" {
		t.Fatal(got)
	}
	effect := workTestEffect()
	effect.GetSettings().Fields = []*r.FieldResult{{Field: r.SettingsField_SETTINGS_FIELD_FOOD_RESTRICTION.Enum(), Outcome: r.FieldOutcome_FIELD_OUTCOME_APPLIED.Enum()}}
	if err := workEffect(effect, assignment, true); err != nil {
		t.Fatal(err)
	}
	effect.GetSettings().Snapshot.BeforeToken = proto.String("stale")
	if err := workEffect(effect, assignment, true); err == nil {
		t.Fatal("stale evidence accepted")
	}
}
