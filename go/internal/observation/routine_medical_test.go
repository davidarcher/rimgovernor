package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestRoutineMedicalRequiresCompleteMatchingHealth(t *testing.T) {
	for _, test := range []struct {
		name      string
		change    func(*o.ColonyFactsSnapshot, *policy.EmergencyFacts, *o.PawnSnapshot)
		known     bool
		recovered domain.Fact[bool]
	}{
		{"healthy", nil, true, domain.Known(true)},
		{"chronic", func(_ *o.ColonyFactsSnapshot, _ *policy.EmergencyFacts, p *o.PawnSnapshot) {
			p.Pawns[0].Health.Hediffs[0].Bad = proto.Bool(true)
		}, true, domain.Known(false)},
		{"rest", func(_ *o.ColonyFactsSnapshot, _ *policy.EmergencyFacts, p *o.PawnSnapshot) {
			p.Pawns[0].Health.ShouldSeekMedicalRest = proto.Bool(true)
		}, true, domain.Known(false)},
		{"hidden", func(_ *o.ColonyFactsSnapshot, _ *policy.EmergencyFacts, p *o.PawnSnapshot) {
			p.Pawns[0].Health.HiddenHediffs = proto.Uint32(1)
		}, true, domain.Unknown[bool]()},
		{"filtered", func(_ *o.ColonyFactsSnapshot, _ *policy.EmergencyFacts, p *o.PawnSnapshot) {
			p.Pawns[0].Health.HediffCompleteness.Filtered = proto.Uint64(1)
		}, true, domain.Unknown[bool]()},
		{"unknown completeness", func(_ *o.ColonyFactsSnapshot, _ *policy.EmergencyFacts, p *o.PawnSnapshot) {
			p.Pawns[0].Health.HediffCompleteness = nil
		}, true, domain.Unknown[bool]()},
		{"unknown condition", func(_ *o.ColonyFactsSnapshot, _ *policy.EmergencyFacts, p *o.PawnSnapshot) {
			p.Pawns[0].Health.Hediffs[0].Bad = nil
		}, true, domain.Unknown[bool]()},
		{"unknown rest", func(_ *o.ColonyFactsSnapshot, _ *policy.EmergencyFacts, p *o.PawnSnapshot) {
			p.Pawns[0].Health.ShouldSeekMedicalRest = nil
		}, true, domain.Unknown[bool]()},
		{"unknown health", func(_ *o.ColonyFactsSnapshot, _ *policy.EmergencyFacts, p *o.PawnSnapshot) { p.Pawns[0].Health = nil }, true, domain.Unknown[bool]()},
		{"colony mismatch", func(v *o.ColonyFactsSnapshot, _ *policy.EmergencyFacts, _ *o.PawnSnapshot) {
			v.ColonistCount = proto.Uint32(2)
		}, false, domain.Unknown[bool]()},
		{"census unknown", func(_ *o.ColonyFactsSnapshot, e *policy.EmergencyFacts, _ *o.PawnSnapshot) {
			e.ColonistsComplete = domain.Unknown[bool]()
		}, false, domain.Unknown[bool]()},
		{"pawn mismatch", func(_ *o.ColonyFactsSnapshot, _ *policy.EmergencyFacts, p *o.PawnSnapshot) {
			p.Pawns[0].Pawn.Id = proto.String("other")
		}, false, domain.Unknown[bool]()},
		{"death mismatch", func(_ *o.ColonyFactsSnapshot, _ *policy.EmergencyFacts, p *o.PawnSnapshot) {
			p.Pawns[0].Dead = proto.Bool(true)
		}, false, domain.Unknown[bool]()},
	} {
		t.Run(test.name, func(t *testing.T) {
			v := &o.ColonyFactsSnapshot{ColonistCount: proto.Uint32(1)}
			e := policy.EmergencyFacts{ColonistsComplete: domain.Known(true), Colonists: []policy.EmergencyPawn{{ID: "p", Dead: domain.Known(false), Downed: domain.Known(false)}}}
			p := &o.PawnSnapshot{Pawns: []*o.PawnState{{Pawn: &o.EntityRef{Id: proto.String("p")}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Health: &o.PawnHealth{ShouldSeekMedicalRest: proto.Bool(false), NeedsTend: proto.Bool(false), HiddenHediffs: proto.Uint32(0), Hediffs: []*o.Hediff{{Bad: proto.Bool(false)}}, HediffCompleteness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}}
			if test.change != nil {
				test.change(v, &e, p)
			}
			observed := routineMedical(v, e, p)
			_, known := observed.Value()
			if known != test.known {
				t.Fatal(observed)
			}
			h, err := policy.ReviewMedicalCare(observed, policy.MedicalCareHistory{})
			if err != nil || h.Recovered() != test.recovered {
				t.Fatal(h, err)
			}
		})
	}
}

func TestRoutineMedicalConditionFacts(t *testing.T) {
	for _, present := range []bool{false, true} {
		h := &o.Hediff{Definition: &o.DefinitionRef{DefName: proto.String("Plague")}, Bad: proto.Bool(true)}
		if present {
			h.Severity = proto.Float64(.2)
			h.SeverityPerDay = proto.Float64(-.3)
			h.Immunity = proto.Float64(0)
			h.ImmunityPerDay = proto.Float64(.5)
			h.Tended = proto.Bool(false)
			h.TendQuality = proto.Float64(0)
		}
		health := &o.PawnHealth{Hediffs: []*o.Hediff{h}, HiddenHediffs: proto.Uint32(0),
			HediffCompleteness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}
		colony := &o.ColonyFactsSnapshot{ColonistCount: proto.Uint32(1)}
		emergency := policy.EmergencyFacts{ColonistsComplete: domain.Known(true), Colonists: []policy.EmergencyPawn{{ID: "p", Dead: domain.Known(false), Downed: domain.Known(false)}}}
		snapshot := &o.PawnSnapshot{Pawns: []*o.PawnState{{Pawn: &o.EntityRef{Id: proto.String("p")}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Health: health}}}
		rows, ok := routineMedical(colony, emergency, snapshot).Value()
		if !ok {
			t.Fatal("missing census")
		}
		conditions, ok := rows[0].Conditions.Value()
		if !ok || len(conditions) != 1 {
			t.Fatal("missing conditions")
		}
		got := conditions[0]
		if got.DefName != domain.Known("Plague") {
			t.Fatal(got)
		}
		for _, pair := range []struct {
			fact domain.Fact[float64]
			want float64
		}{
			{got.Severity, .2}, {got.SeverityPerDay, -.3}, {got.Immunity, 0}, {got.ImmunityPerDay, .5}, {got.TendQuality, 0},
		} {
			value, known := pair.fact.Value()
			if known != present || present && value != pair.want {
				t.Fatal(got)
			}
		}
		tended, known := got.Tended.Value()
		if known != present || tended {
			t.Fatal(got)
		}
		health.HediffCompleteness = nil
		rows, _ = routineMedical(colony, emergency, snapshot).Value()
		if _, known := rows[0].Conditions.Value(); known {
			t.Fatal("incomplete condition list claimed complete")
		}
	}
}
