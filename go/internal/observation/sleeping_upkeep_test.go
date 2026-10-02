package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestSleepingProjectionPreservesUnassignedAndUnknown(t *testing.T) {
	u := &o.UpkeepFacts{People: []*o.UpkeepPerson{{Pawn: &commonpb.Ref{Id: proto.String("pawn")}}}}
	v := &o.ColonyFactsSnapshot{ColonistCount: proto.Uint32(1), Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	r, known := colonySleeping(v, nil).Value()
	if !known || len(r.People) != 1 {
		t.Fatal(r, known)
	}
	if bed, known := r.People[0].OwnedBed.Value(); !known || bed != "" {
		t.Fatal("unassigned became unknown")
	}
	u.Issues = []*o.ReadIssue{{Field: proto.String("beds")}}
	if _, known := colonySleeping(v, nil).Value(); known {
		t.Fatal("failed bed census became empty")
	}
}
