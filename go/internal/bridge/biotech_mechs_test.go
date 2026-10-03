package bridge

import (
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestMechInputsLiftMechRows(t *testing.T) {
	row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("Mech_Lifter1")}, KindDefName: proto.String("Mech_Lifter"), Mechanoid: proto.Bool(true), Downed: proto.Bool(false),
		Biotech: &o.PawnBiotech{Mech: &o.PawnMech{Overseer: &c.Ref{Id: proto.String("Colonist1")}, WorkMode: proto.String("Work"), ControlGroup: proto.Int32(1)}}}
	got, err := MechInputs(&o.PawnSnapshot{Pawns: []*o.PawnState{row}})
	if err != nil || len(got) != 1 {
		t.Fatalf("%v %v", got, err)
	}
	m := got[0]
	if group, _ := m.ControlGroup.Value(); m.ID != "Mech_Lifter1" || m.Kind != "Mech_Lifter" || m.Overseer != "Colonist1" || group != 1 {
		t.Fatalf("row %+v", m)
	}
	for name, bad := range map[string]*o.PawnState{
		"not a mech": {Pawn: row.Pawn, KindDefName: row.KindDefName, Biotech: row.Biotech},
		"no kind":    {Pawn: row.Pawn, Mechanoid: proto.Bool(true), Biotech: row.Biotech},
		"no block":   {Pawn: row.Pawn, KindDefName: row.KindDefName, Mechanoid: proto.Bool(true), Biotech: &o.PawnBiotech{}},
	} {
		if _, err := MechInputs(&o.PawnSnapshot{Pawns: []*o.PawnState{bad}}); err == nil {
			t.Errorf("%s accepted", name)
		}
	}
}
