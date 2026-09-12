package bridge

import (
	"math"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestRoutineMoodNeedsValidationAndSelection(t *testing.T) {
	for _, test := range []struct {
		name   string
		change func(*o.PawnNeeds)
		valid  bool
	}{
		{"known zero", nil, true},
		{"unknown mood", func(n *o.PawnNeeds) { n.Mood = nil }, true},
		{"nonfinite mood", func(n *o.PawnNeeds) { n.Mood = proto.Float64(math.NaN()) }, false},
		{"nonfinite threshold", func(n *o.PawnNeeds) { n.BreakThresholdMinor = proto.Float64(math.Inf(1)) }, false},
		{"conflicting unknown", func(n *o.PawnNeeds) {
			n.Issues = []*o.ReadIssue{{Field: proto.String("mood"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}
		}, false},
	} {
		t.Run(test.name, func(t *testing.T) {
			s := combatPawnsFixture()
			n := &o.PawnNeeds{Mood: proto.Float64(0), Food: proto.Float64(.1), BreakThresholdMinor: proto.Float64(.3)}
			if test.change != nil {
				test.change(n)
			}
			s.Pawns[0].Needs = n
			err := ValidateRoutinePawnSnapshot(s, pbIdentity(), []string{"pawn-1"})
			if (err == nil) != test.valid {
				t.Fatal(err)
			}
			if ValidateCombatPawnSnapshot(s, pbIdentity(), []string{"pawn-1"}) == nil {
				t.Fatal("combat-only selection accepted needs")
			}
		})
	}
}
