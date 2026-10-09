package observation

import (
	"slices"
	"testing"

	"google.golang.org/protobuf/proto"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// TestCapturableEntitiesCarryTheRulesFacts: an entity row lifts its
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

// TestCapturableEntitiesCarryCurrentlyStudiable: the study block's
// currently-studiable fact is lifted, no study block is a known false and an
// unread study block stays unknown.
func TestCapturableEntitiesCarryCurrentlyStudiable(t *testing.T) {
	studied := entityRow(100, true, true)
	studied.Pawn = &o.EntityRef{Id: proto.String("e1")}
	studied.Anomaly.Study = &o.StudyState{CurrentlyStudiable: proto.Bool(true)}
	bare := entityRow(100, true, true)
	bare.Pawn = &o.EntityRef{Id: proto.String("e2")}
	unread := entityRow(100, true, true)
	unread.Pawn = &o.EntityRef{Id: proto.String("e3")}
	unread.Anomaly.Issues = []*o.ReadIssue{{Field: proto.String("study")}}
	got, _ := capturableEntities(slices.Values([]*o.PawnState{studied, bare, unread})).Value()
	if v, ok := got[0].CurrentlyStudiable.Value(); !ok || !v {
		t.Fatalf("%+v", got[0])
	}
	if v, ok := got[1].CurrentlyStudiable.Value(); !ok || v {
		t.Fatalf("%+v", got[1])
	}
	if _, ok := got[2].CurrentlyStudiable.Value(); ok {
		t.Fatalf("%+v", got[2])
	}
}
