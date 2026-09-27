package bridge

import (
	"testing"

	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	"google.golang.org/protobuf/proto"
)

// The poll is always asked for a world (#795), and its pawns and colony
// facts asks are exactly the review's reads.
func TestMirrorPollRequestShape(t *testing.T) {
	id := pbIdentity()
	base := func(asks ...*mp.SectionAsk) *mp.MirrorPollRequest {
		return &mp.MirrorPollRequest{Identity: id, Asks: asks, ByteBudget: proto.Uint32(MirrorPollMaxBytes)}
	}
	if err := validateMirrorPollRequest(base(MirrorColonyFactsAsk(id), MirrorPawnsAsk(id, []string{"pawn-1"}))); err != nil {
		t.Fatalf("review asks refused: %v", err)
	}
	noIdentity := base()
	noIdentity.Identity = nil
	odd := MirrorPawnsAsk(id, []string{"pawn-1"})
	odd.Pawns.Page = nil
	misplaced := MirrorColonyFactsAsk(id)
	misplaced.Section = mp.Section_SECTION_BILLS.Enum()
	for name, request := range map[string]*mp.MirrorPollRequest{
		"no identity":      noIdentity,
		"pawns off form":   base(odd),
		"colony misplaced": base(misplaced),
		"empty roster":     base(MirrorPawnsAsk(id, nil)),
	} {
		if validateMirrorPollRequest(request) == nil {
			t.Errorf("%s: accepted", name)
		}
	}
}
