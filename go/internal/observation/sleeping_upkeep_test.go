package observation

import (
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestSleepingProjectionPreservesUnassignedAndUnknown(t *testing.T) {
	u := &o.UpkeepFacts{People: []*o.UpkeepPerson{{Pawn: &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("pawn")}}, OwnedBedId: proto.String("")}}}
	v := &o.ColonyFactsSnapshot{ColonistCount: proto.Uint32(1), Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	r, known := colonySleeping(v).Value()
	if !known || len(r.People) != 1 {
		t.Fatal(r, known)
	}
	if bed, known := r.People[0].OwnedBed.Value(); !known || bed != "" {
		t.Fatal("unassigned became unknown")
	}
	u.People[0].OwnedBedId = nil
	r, _ = colonySleeping(v).Value()
	if _, known := r.People[0].OwnedBed.Value(); known {
		t.Fatal("missing assignment became none")
	}
	u.Issues = []*o.ReadIssue{{Field: proto.String("beds")}}
	if _, known := colonySleeping(v).Value(); known {
		t.Fatal("failed bed census became empty")
	}
}
