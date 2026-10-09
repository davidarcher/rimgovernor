package tend

import (
	"testing"

	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// A doctor whose reachability the native budget skipped is unknown, not a
// doctor who reaches nobody: only the read row yields a known false.
func TestTendReachabilitySkippedDoctorIsUnknown(t *testing.T) {
	row := func(id string, doctor *n.PawnTendDoctor) *n.PawnState {
		return &n.PawnState{Pawn: &n.EntityRef{Id: proto.String(id)}, TendDoctor: doctor}
	}
	reach := TendReachability([]*n.PawnState{
		row("read", &n.PawnTendDoctor{}),
		row("skipped", &n.PawnTendDoctor{ReachSkipped: proto.Bool(true)}),
	})
	if _, known := reach.Reaches("read", "patient").Value(); !known {
		t.Fatal("a read doctor with an empty list must be a known false")
	}
	if _, known := reach.Reaches("skipped", "patient").Value(); known {
		t.Fatal("a skipped doctor must stay unknown")
	}
}
