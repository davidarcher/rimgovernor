package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func TestDrugPolicyOperationAndEvidence(t *testing.T) {
	w, err := domain.NewDrugPolicyAssignment("pawn", "before", "social")
	if err != nil {
		t.Fatal(err)
	}
	operation := workOperation(w).GetSetDrugPolicy()
	if operation.GetName() != "social" || operation.GetPawn().GetExpectedSnapshotToken() != "before" {
		t.Fatal(operation)
	}
	effect := &r.EffectEvidence{Effect: &r.EffectEvidence_Settings{Settings: &r.SettingsEffect{Snapshot: &r.SnapshotEvidence{EntityId: proto.String("pawn"), BeforeToken: proto.String("before"), AfterToken: proto.String("after")}, Fields: []*r.FieldResult{{Field: r.SettingsField_SETTINGS_FIELD_DRUG_POLICY.Enum(), Outcome: r.FieldOutcome_FIELD_OUTCOME_APPLIED.Enum()}}}}}
	if err := workEffect(effect, w, true); err != nil {
		t.Fatal(err)
	}
	effect.GetSettings().Fields[0].Field = r.SettingsField_SETTINGS_FIELD_FOOD_RESTRICTION.Enum()
	if err := workEffect(effect, w, true); err == nil {
		t.Fatal("wrong settings evidence accepted")
	}
	bill, err := domain.NewProductionBill("brewery", "Make_Wort", "before", domain.BeerReserve, 12)
	if err != nil || !BillOperation(bill).GetAddBill().GetSettings().GetBeerReserve() {
		t.Fatal(bill, err)
	}
}

// The drug settings read carries writable, name and default-policy status
// together: a partial read would let the routine overwrite a player policy.
func TestDrugPolicySettingsReadTogether(t *testing.T) {
	settings := &o.PawnSettings{DrugPolicyWritable: proto.Bool(true), DrugPolicyName: proto.String(""), DrugPolicyDefault: proto.Bool(false)}
	if err := validateSettings(settings, true, false, false); err != nil {
		t.Fatal(err)
	}
	settings.DrugPolicyDefault = nil
	if err := validateSettings(settings, true, false, false); err == nil {
		t.Fatal("drug settings without default status accepted")
	}
}
