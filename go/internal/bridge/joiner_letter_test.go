package bridge

import (
	ob "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
	"testing"
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

func TestJoinerAnswerRequiresMatchingTokenAndNativeArrival(t *testing.T) {
	w := DialogAttempt{WindowID: 3, OptionIndex: 0, OptionLabel: "Accept", LetterToken: "token"}
	effect := &r.DialogEffect{WindowId: proto.Int32(3), OptionIndex: proto.Int32(0), OptionLabel: proto.String("Accept"), Activated: proto.Bool(true), Closed: proto.Bool(true), JoinerLetterToken: proto.String("token"), JoinerPawnId: proto.String("Pawn_4"), Joined: proto.Bool(true)}
	evidence := &r.EffectEvidence{Effect: &r.EffectEvidence_Dialog{Dialog: effect}}
	if err := dialogEffect(evidence, w, true); err != nil {
		t.Fatal(err)
	}
	effect.Joined = proto.Bool(false)
	if err := dialogEffect(evidence, w, true); err == nil {
		t.Fatal("letter closure alone proved arrival")
	}
	effect.Joined = proto.Bool(true)
	effect.JoinerLetterToken = proto.String("other")
	if err := dialogEffect(evidence, w, true); err == nil {
		t.Fatal("accepted changed token")
	}
}
