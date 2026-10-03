package observation

import (
	"slices"
	"testing"

	"google.golang.org/protobuf/proto"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// TestCapturableEntitiesCarryTheRulesFacts (#1742): an entity row lifts its
// downed, held, capturable and needed-strength facts, a pawn that is no
// entity is left out, an entity with no holding-platform block is a known
// "cannot be captured", and an unread entity fact keeps the row with the
// facts unknown so the rule refuses it.
func TestCapturableEntitiesCarryTheRulesFacts(t *testing.T) {
	downed := entityRow(100, false, true)
	downed.Pawn = &o.EntityRef{Id: proto.String("e1")}
	downed.Downed = proto.Bool(true)
	colonist := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("c1")}, Anomaly: &o.PawnAnomaly{Entity: proto.Bool(false)}}
	noTarget := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("e2")}, Dead: proto.Bool(false), Downed: proto.Bool(true), Anomaly: &o.PawnAnomaly{Entity: proto.Bool(true)}}
	unread := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("e3")}, Anomaly: &o.PawnAnomaly{}}
	got, ok := capturableEntities(slices.Values([]*o.PawnState{{}, colonist, downed, noTarget, unread})).Value()
	if !ok || len(got) != 3 {
		t.Fatalf("%+v %v", got, ok)
	}
	e := got[0]
	need, nk := e.Need.Value()
	downedFact, dk := e.Downed.Value()
	capturable, ck := e.CanBeCaptured.Value()
	held, hk := e.Held.Value()
	if e.Pawn != "e1" || !nk || need != 100 || !dk || !downedFact || !ck || !capturable || !hk || held {
		t.Fatalf("%+v", e)
	}
	if capturable, ck := got[1].CanBeCaptured.Value(); !ck || capturable {
		t.Fatalf("no holding-platform block is a known cannot-be-captured: %+v", got[1])
	}
	if _, ck := got[2].CanBeCaptured.Value(); ck {
		t.Fatalf("an unread held block stays unknown: %+v", got[2])
	}
}
