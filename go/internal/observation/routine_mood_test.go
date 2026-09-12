package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestRoutineMoodProjectionPreservesIndependentUnknowns(t *testing.T) {
	for _, test := range []struct {
		name                 string
		change               func(*o.ColonyFactsSnapshot, *policy.EmergencyFacts, *o.PawnSnapshot)
		census               bool
		rows                 int
		mood, forced, mental bool
	}{
		{"complete", nil, true, 1, true, true, true},
		{"missing needs", func(_ *o.ColonyFactsSnapshot, _ *policy.EmergencyFacts, p *o.PawnSnapshot) { p.Pawns[0].Needs = nil }, true, 1, false, true, true},
		{"missing job", func(_ *o.ColonyFactsSnapshot, _ *policy.EmergencyFacts, p *o.PawnSnapshot) { p.Pawns[0].Job = nil }, true, 1, true, false, true},
		{"unknown mental", func(_ *o.ColonyFactsSnapshot, _ *policy.EmergencyFacts, p *o.PawnSnapshot) { p.Pawns[0].Issues = nil }, true, 1, true, true, false},
		{"missing pawn", func(_ *o.ColonyFactsSnapshot, _ *policy.EmergencyFacts, p *o.PawnSnapshot) { p.Pawns = nil }, true, 0, false, false, false},
		{"count mismatch", func(v *o.ColonyFactsSnapshot, _ *policy.EmergencyFacts, _ *o.PawnSnapshot) {
			v.ColonistCount = proto.Uint32(2)
		}, false, 0, false, false, false},
		{"census unknown", func(_ *o.ColonyFactsSnapshot, e *policy.EmergencyFacts, _ *o.PawnSnapshot) {
			e.ColonistsComplete = domain.Unknown[bool]()
		}, false, 0, false, false, false},
		{"death conflict", func(_ *o.ColonyFactsSnapshot, _ *policy.EmergencyFacts, p *o.PawnSnapshot) {
			p.Pawns[0].Dead = proto.Bool(true)
		}, false, 0, false, false, false},
	} {
		t.Run(test.name, func(t *testing.T) {
			v := &o.ColonyFactsSnapshot{ColonistCount: proto.Uint32(1)}
			e := policy.EmergencyFacts{ColonistsComplete: domain.Known(true), Colonists: []policy.EmergencyPawn{{ID: "p", Dead: domain.Known(false), Downed: domain.Known(false)}}}
			p := &o.PawnSnapshot{Pawns: []*o.PawnState{{Pawn: &o.EntityRef{Id: proto.String("p")}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Job: &o.JobEvidence{PlayerForced: proto.Bool(false)}, Needs: &o.PawnNeeds{Mood: proto.Float64(0), BreakThresholdMinor: proto.Float64(.3), Food: proto.Float64(.1)}, Issues: []*o.ReadIssue{{Field: proto.String("mental_state"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}}}}
			if test.change != nil {
				test.change(v, &e, p)
			}
			rows, known := routineMood(v, e, p).Value()
			if known != test.census || len(rows) != test.rows {
				t.Fatal(rows, known)
			}
			if len(rows) > 0 {
				_, mk := rows[0].Mood.Value()
				_, fk := rows[0].PlayerForced.Value()
				_, nk := rows[0].Mental.Value()
				if mk != test.mood || fk != test.forced || nk != test.mental {
					t.Fatal(rows)
				}
			}
		})
	}
}
