package bridge

import (
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestValidatePolicyInputs(t *testing.T) {
	ok := &o.PawnPolicyInputs{OutfitPolicyId: proto.String("ApparelPolicy_1"), Chemicals: []*o.ChemicalState{{Chemical: proto.String("Alcohol"), Tolerance: proto.Float64(0.1)}}}
	if err := validatePolicyInputs(ok); err != nil {
		t.Fatal(err)
	}
	for name, bad := range map[string]*o.PawnPolicyInputs{
		"duplicate chemical":      {Chemicals: []*o.ChemicalState{{Chemical: proto.String("Alcohol"), Tolerance: proto.Float64(0.1)}, {Chemical: proto.String("Alcohol"), Tolerance: proto.Float64(0.2)}}},
		"addiction w/o stage":     {Chemicals: []*o.ChemicalState{{Chemical: proto.String("Alcohol"), Addiction: proto.Float64(0.1)}}},
		"empty chemical row":      {Chemicals: []*o.ChemicalState{{Chemical: proto.String("Alcohol")}}},
		"negative stock":          {InventoryStock: []*o.InventoryStockSetting{{Group: proto.String("Medicine"), ThingDef: proto.String("MedicineHerbal"), Count: proto.Int32(-1)}}},
		"blank policy identifier": {ReadingPolicyId: proto.String(" ")},
	} {
		if validatePolicyInputs(bad) == nil {
			t.Error(name)
		}
	}
}
