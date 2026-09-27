package bridge

import (
	"testing"

	ob "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestJoinerLetterCensusRejectsExpiredAndIncompleteOffers(t *testing.T) {
	row := &ob.JoinerLetter{LetterId: proto.Int32(3), SnapshotToken: proto.String("token"), PawnId: proto.String("Pawn_4"), ExpiresTick: proto.Int64(100), AcceptLabel: proto.String("Accept"), CanAccept: proto.Bool(true)}
	if err := validateJoinerLetters([]*ob.JoinerLetter{row}, 99); err != nil {
		t.Fatal(err)
	}
	if err := validateJoinerLetters([]*ob.JoinerLetter{row}, 100); err == nil {
		t.Fatal("accepted expired letter")
	}
	if err := validateJoinerLetters([]*ob.JoinerLetter{row, row}, 99); err == nil {
		t.Fatal("accepted duplicate letter")
	}
	row.PawnId = nil
	if err := validateJoinerLetters([]*ob.JoinerLetter{row}, 99); err == nil {
		t.Fatal("accepted missing pawn")
	}
}
