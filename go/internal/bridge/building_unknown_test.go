package bridge

import (
	"strings"
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protowire"
	"google.golang.org/protobuf/proto"
)

// A building row carrying a field this build does not know (#1242) is
// refused, and the failure names the row type and the field number.
func TestBuildingUnknownNamesField(t *testing.T) {
	row := &o.BuildingState{}
	row.ProtoReflect().SetUnknown(protowire.AppendVarint(protowire.AppendTag(nil, 999, protowire.VarintType), 1))
	snapshot := &o.BuildingsSnapshot{Buildings: []*o.BuildingState{row}}
	err := buildingUnknown(snapshot)
	if err == nil || !strings.Contains(err.Error(), "[999]") || !strings.Contains(err.Error(), "BuildingState") {
		t.Fatalf("err = %v, want the unknown field 999 in BuildingState", err)
	}
	// The same row decoded off the wire keeps the field as unknown.
	data, err := proto.Marshal(snapshot)
	if err != nil {
		t.Fatal(err)
	}
	decoded := &o.BuildingsSnapshot{}
	if err = proto.Unmarshal(data, decoded); err != nil {
		t.Fatal(err)
	}
	if err = buildingUnknown(decoded); err == nil || !strings.Contains(err.Error(), "[999]") {
		t.Fatalf("decoded err = %v", err)
	}
	// explosive_radius (#1209) is a known PlanningDefinition field.
	if err = buildingUnknown(&o.PlanningDefinition{ExplosiveRadius: proto.Float64(3.9)}); err != nil {
		t.Fatalf("explosive_radius refused: %v", err)
	}
	// A scalar-valued map is walked without reading its values as messages.
	if err = buildingUnknown(&o.SurgeryOperation{DoctorChances: map[string]float64{"Thing_Human1": 0.9}}); err != nil {
		t.Fatalf("doctor_chances refused: %v", err)
	}
}
