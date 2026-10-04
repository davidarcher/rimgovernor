package bridge

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestRoundsSurgeryFactsMapping(t *testing.T) {
	leg := &o.MissingBodyPart{PartIndex: proto.Int32(40), PartDefName: proto.String("Leg"), ParentIndex: proto.Int32(0), ParentDefName: proto.String("Torso"), Vital: proto.Bool(false)}
	restore := &o.SurgeryOperation{
		Recipe: &o.DefinitionRef{DefName: proto.String("InstallPegLeg")}, PartIndex: proto.Int32(40), PartDefName: proto.String("Leg"),
		Kind: o.SurgeryKind_SURGERY_KIND_RESTORE, SuccessChance: proto.Float64(.87), EligibleDoctors: proto.Uint32(2),
		IngredientsOnMap: proto.Bool(true), Violation: proto.Bool(false), Lethal: proto.Bool(false),
	}
	noDoctor := &o.SurgeryOperation{Recipe: &o.DefinitionRef{DefName: proto.String("RemoveBodyPart")}, Kind: o.SurgeryKind_SURGERY_KIND_HARVEST, EligibleDoctors: proto.Uint32(0), Lethal: proto.Bool(true), MedicineCareLimited: proto.Bool(true)}
	issue := func(field string) *o.ReadIssue { return &o.ReadIssue{Field: proto.String(field)} }
	for _, test := range []struct {
		name  string
		h     *o.PawnHealth
		parts domain.Fact[[]policy.MissingPart]
		ops   domain.Fact[[]policy.SurgeryOperation]
	}{
		{"empty is known none", &o.PawnHealth{}, domain.Known([]policy.MissingPart{}), domain.Known([]policy.SurgeryOperation{})},
		{"restore row", &o.PawnHealth{MissingParts: []*o.MissingBodyPart{leg}, Operations: []*o.SurgeryOperation{restore}},
			domain.Known([]policy.MissingPart{{PartIndex: domain.Known(40), ParentIndex: domain.Known(0), PartDefName: domain.Known("Leg"), ParentDefName: domain.Known("Torso"), Vital: domain.Known(false)}}),
			domain.Known([]policy.SurgeryOperation{{Recipe: domain.Known("InstallPegLeg"), PartDefName: domain.Known("Leg"), PartIndex: domain.Known(40), Kind: policy.SurgeryRestore,
				SuccessChance: domain.Known(.87), EligibleDoctors: domain.Known(2), IngredientsOnMap: domain.Known(true), Violation: domain.Known(false), Lethal: domain.Known(false)}})},
		{"no doctor leaves chance unknown", &o.PawnHealth{Operations: []*o.SurgeryOperation{noDoctor}}, domain.Known([]policy.MissingPart{}),
			domain.Known([]policy.SurgeryOperation{{Recipe: domain.Known("RemoveBodyPart"), Kind: policy.SurgeryHarvest, EligibleDoctors: domain.Known(0), Lethal: domain.Known(true), CareLimited: domain.Known(true)}})},
		{"read issues are unknown", &o.PawnHealth{Issues: []*o.ReadIssue{issue("missing_parts"), issue("operations")}, Operations: []*o.SurgeryOperation{restore}},
			domain.Unknown[[]policy.MissingPart](), domain.Unknown[[]policy.SurgeryOperation]()},
	} {
		t.Run(test.name, func(t *testing.T) {
			parts, ops, err := SurgeryFacts(test.h, nil)
			if err != nil {
				t.Fatal(err)
			}
			if !reflect.DeepEqual(parts, test.parts) || !reflect.DeepEqual(ops, test.ops) {
				t.Fatalf("parts %+v ops %+v", parts, ops)
			}
		})
	}
}

func TestSurgeryKindsCoverEveryWireKind(t *testing.T) {
	for n := range o.SurgeryKind_name {
		k := o.SurgeryKind(n)
		if k != o.SurgeryKind_SURGERY_KIND_UNSPECIFIED && surgeryKinds[k] == policy.SurgeryUnknown {
			t.Fatal(k)
		}
	}
}

func TestInstalledPartsMapping(t *testing.T) {
	arm := &o.InstalledPart{Definition: &o.DefinitionRef{DefName: proto.String("BionicArm")}, PartDefName: proto.String("Shoulder"), PartIndex: proto.Int32(12), SpawnThingDefName: proto.String("BionicArm")}
	peg := &o.InstalledPart{Definition: &o.DefinitionRef{DefName: proto.String("PegLeg")}, PartDefName: proto.String("Leg"), PartIndex: proto.Int32(40)}
	got := InstalledParts(&o.PawnHealth{InstalledParts: []*o.InstalledPart{arm, peg}})
	want := domain.Known([]policy.InstalledPart{
		{Hediff: "BionicArm", Part: domain.Known("Shoulder"), PartIndex: domain.Known(12), Item: domain.Known(policy.Resource("BionicArm")), Tier: 1.25},
		{Hediff: "PegLeg", Part: domain.Known("Leg"), PartIndex: domain.Known(40), Item: domain.Unknown[policy.Resource](), Tier: 0.6},
	})
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("got %+v", got)
	}
	if none := InstalledParts(&o.PawnHealth{}); !reflect.DeepEqual(none, domain.Known([]policy.InstalledPart{})) {
		t.Fatalf("none %+v", none)
	}
	bad := InstalledParts(&o.PawnHealth{Issues: []*o.ReadIssue{{Field: proto.String("installed_parts")}}, InstalledParts: []*o.InstalledPart{arm}})
	if _, known := bad.Value(); known {
		t.Fatal("read issue must be unknown")
	}
}
