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

// TestRoutineScheduleDetailValidation covers the validateSettings schedule
// gap: ReadRoutinePawns/ValidateRoutinePawnSnapshot must accept
// PawnSettings.Schedule (EnsureMood-* relief dispatch needs a pawn's current
// timetable assignment to fence its native writes via
// boundary.ExpectedScheduleDef), while selections that never requested
// schedule detail (combat, tend) must keep refusing it as unrequested.
func TestRoutineScheduleDetailValidation(t *testing.T) {
	for _, test := range []struct {
		name    string
		change  func(*o.PawnSettings)
		routine bool
	}{
		{"known slots", nil, true},
		{"duplicate hour", func(s *o.PawnSettings) {
			s.Schedule = append(s.Schedule, &o.TimetableSlot{Hour: proto.Uint32(0), AssignmentDefName: proto.String("Sleep")})
		}, false},
		{"hour out of range", func(s *o.PawnSettings) {
			s.Schedule[0].Hour = proto.Uint32(24)
		}, false},
		{"invalid def name", func(s *o.PawnSettings) {
			s.Schedule[0].AssignmentDefName = proto.String("  ")
		}, false},
	} {
		t.Run(test.name, func(t *testing.T) {
			s := combatPawnsFixture()
			settings := &o.PawnSettings{Schedule: []*o.TimetableSlot{{Hour: proto.Uint32(0), AssignmentDefName: proto.String("Anything")}}}
			if test.change != nil {
				test.change(settings)
			}
			s.Pawns[0].Settings = settings
			if err := ValidateRoutinePawnSnapshot(s, pbIdentity(), []string{"pawn-1"}); (err == nil) != test.routine {
				t.Fatal("routine", err)
			}
			if ValidateCombatPawnSnapshot(s, pbIdentity(), []string{"pawn-1"}) == nil {
				t.Fatal("combat-only selection accepted schedule")
			}
			if ValidateTendPawnSnapshot(s, pbIdentity(), []string{"pawn-1"}) == nil {
				t.Fatal("tend-only selection accepted schedule")
			}
		})
	}
}
