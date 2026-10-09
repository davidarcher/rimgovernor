package bridge

import (
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// TestPawnAnomalyCreepJoiner: a creepjoiner's form, benefit and
// whether its downside has fired lift as read; a pawn with no tracker is
// known not to be one; a failed read leaves the block unknown; the row has
// no field for the hidden downside def.
func TestPawnAnomalyCreepJoiner(t *testing.T) {
	row := &o.PawnAnomaly{Entity: proto.Bool(false), Creepjoiner: &o.CreepJoinerState{Form: proto.String("Gaunt"), Benefit: proto.String("Smith"), DownsideTriggered: proto.Bool(false)}}
	if err := validatePawnAnomaly(row); err != nil {
		t.Fatal(err)
	}
	a, _ := PawnAnomaly(row).Value()
	joiner, ok := a.CreepJoiner.Value()
	if !ok || joiner == nil {
		t.Fatal("creepjoiner", joiner, ok)
	}
	if form, _ := joiner.Form.Value(); form != "Gaunt" {
		t.Fatal("form", form)
	}
	if triggered, ok := joiner.DownsideTriggered.Value(); !ok || triggered {
		t.Fatal("downside triggered", triggered, ok)
	}
	if o.File_observations_proto.Messages().ByName("CreepJoinerState").Fields().ByName("downside") != nil {
		t.Fatal("the pawn row must not carry the hidden downside")
	}
	plain, _ := PawnAnomaly(&o.PawnAnomaly{Entity: proto.Bool(false)}).Value()
	if joiner, ok := plain.CreepJoiner.Value(); !ok || joiner != nil {
		t.Fatal("a pawn with no tracker is known not a creepjoiner", joiner, ok)
	}
	failed := &o.PawnAnomaly{Issues: []*o.ReadIssue{{Field: proto.String("creepjoiner"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}}
	if err := validatePawnAnomaly(failed); err != nil {
		t.Fatal(err)
	}
	a, _ = PawnAnomaly(failed).Value()
	if _, ok := a.CreepJoiner.Value(); ok {
		t.Fatal("a failed creepjoiner read must stay unknown")
	}
	bad := &o.PawnAnomaly{Creepjoiner: &o.CreepJoinerState{Form: proto.String("")}}
	if validatePawnAnomaly(bad) == nil {
		t.Fatal("invalid form name accepted")
	}
}

// A creepjoiner letter may have no timeout: its expiry is the game's
// negative sentinel, mirrored; a missing expiry is still refused.
func TestJoinerLetterCensusCreepJoinerWithoutTimeout(t *testing.T) {
	row := &o.JoinerLetter{LetterId: proto.Int32(3), SnapshotToken: proto.String("creepjoiner-token"), PawnId: proto.String("Pawn_4"), AcceptLabel: proto.String("Accept"),
		CanAccept: proto.Bool(true), Creepjoiner: proto.Bool(true)}
	if err := validateJoinerLetters([]*o.JoinerLetter{row}, 99); err == nil {
		t.Fatal("accepted a letter with no expiry fact")
	}
	row.ExpiresTick = proto.Int64(-1)
	if err := validateJoinerLetters([]*o.JoinerLetter{row}, 99); err != nil {
		t.Fatal(err)
	}
	row.ExpiresTick = proto.Int64(99)
	if err := validateJoinerLetters([]*o.JoinerLetter{row}, 99); err == nil {
		t.Fatal("accepted an expired creepjoiner letter")
	}
}
