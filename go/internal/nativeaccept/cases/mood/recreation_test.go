package mood

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	common "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func recreationSnapshot(offset float64) *o.PawnSnapshot {
	return &o.PawnSnapshot{
		Completeness: &o.Completeness{Page: &common.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1)},
		Pawns: []*o.PawnState{{
			Pawn: &o.EntityRef{Id: proto.String("pawn-1")}, Colonist: proto.Bool(true), Dead: proto.Bool(false),
			Social:   &o.PawnSocial{Situational: []*o.Thought{{DefName: proto.String("NeedJoy"), MoodOffsetTotal: proto.Float64(offset)}}},
			Settings: &o.PawnSettings{DrugPolicyName: proto.String(policy.SocialDrugPolicyName)},
		}},
	}
}

func TestRecreationOutcomes(t *testing.T) {
	for _, tc := range []struct {
		name               string
		brewing, wantError bool
		edit               func(*o.PawnSnapshot)
	}{
		{"threshold", true, false, func(*o.PawnSnapshot) {}},
		{"below-threshold", false, true, func(s *o.PawnSnapshot) { s.Pawns[0].Social.Situational[0].MoodOffsetTotal = proto.Float64(-5.01) }},
		{"no-negative-joy", true, false, func(s *o.PawnSnapshot) { s.Pawns[0].Social.Situational = nil }},
		{"unrelated-thought", true, false, func(s *o.PawnSnapshot) {
			s.Pawns[0].Social.Situational[0] = &o.Thought{DefName: proto.String("PsychicDrone"), MoodOffsetTotal: proto.Float64(-30)}
		}},
		{"missing-social", false, true, func(s *o.PawnSnapshot) { s.Pawns[0].Social = nil }},
		{"stale-social", false, true, func(s *o.PawnSnapshot) { s.Pawns[0].Social.SituationalCacheStale = proto.Bool(true) }},
		{"unknown-offset", false, true, func(s *o.PawnSnapshot) { s.Pawns[0].Social.Situational[0].MoodOffsetTotal = nil }},
		{"nan-offset", false, true, func(s *o.PawnSnapshot) { s.Pawns[0].Social.Situational[0].MoodOffsetTotal = proto.Float64(math.NaN()) }},
		{"partial-census", true, true, func(s *o.PawnSnapshot) { s.Completeness.Matched = proto.Uint64(2) }},
		{"empty-census", false, true, func(s *o.PawnSnapshot) { s.Pawns = nil }},
		{"policy-required", true, true, func(s *o.PawnSnapshot) { s.Pawns[0].Settings.DrugPolicyName = proto.String("Anything") }},
		{"policy-unknown", true, true, func(s *o.PawnSnapshot) { s.Pawns[0].Settings = nil }},
		{"before-brewing", false, false, func(s *o.PawnSnapshot) { s.Pawns[0].Settings = nil }},
		{"second-colonist", true, true, func(s *o.PawnSnapshot) {
			p := recreationSnapshot(-10).Pawns[0]
			p.Pawn.Id = proto.String("pawn-2")
			s.Pawns = append(s.Pawns, p)
			s.Completeness.Matched = proto.Uint64(2)
			s.Completeness.Returned = proto.Uint64(2)
		}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			s := recreationSnapshot(-5)
			tc.edit(s)
			if err := checkRecreation(s, tc.brewing); (err != nil) != tc.wantError {
				t.Fatalf("checkRecreation: %v", err)
			}
		})
	}
}

func TestRecreationBrewingRequiresKnownCompletion(t *testing.T) {
	for _, finished := range []*bool{nil, proto.Bool(false), proto.Bool(true)} {
		r := &o.ResearchSnapshot{Completeness: &o.Completeness{Page: &common.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1)},
			Projects: []*o.ResearchProject{{Project: &o.DefinitionRef{DefName: proto.String("Brewing")}, Finished: finished}}}
		got, err := recreationBrewing(r)
		if (err != nil) != (finished == nil) || finished != nil && got != *finished {
			t.Fatalf("completion %v: %v, %v", finished, got, err)
		}
	}
	if _, err := recreationBrewing(nil); err == nil {
		t.Fatal("missing research passed")
	}
}
