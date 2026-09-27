package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func emergencyCounts(n uint64) *o.Completeness {
	return &o.Completeness{Filtered: proto.Uint64(0)}
}
func emergencyFixture() *o.StatusSnapshot {
	return &o.StatusSnapshot{Context: pbContext(), Colonists: &o.PawnSnapshot{Context: pbContext(), Completeness: emergencyCounts(0)}, Threats: &o.ThreatsSnapshot{}}
}
func emergencyRow(id string) *o.PawnState {
	return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id)}, Dead: proto.Bool(false), Downed: proto.Bool(false), InBed: proto.Bool(true), Health: &o.PawnHealth{Bleeding: proto.Bool(false), NeedsTend: proto.Bool(false)}}
}
func TestEmergencyReadExactRequestAndOwnedFacts(t *testing.T) {
	original := emergencyFixture()
	original.Context.NativeGeneration = nil
	original.Colonists.Context.NativeGeneration = nil
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		if arg.Tool != "rimgovernor/observations_read_status" {
			t.Fatal(arg.Tool)
		}
		var outer struct {
			Request string `json:"request"`
		}
		if e := json.Unmarshal(arg.Arguments, &outer); e != nil {
			t.Fatal(e)
		}
		q := &o.StatusRequest{}
		if e := protojson.Unmarshal([]byte(outer.Request), q); e != nil || q.Colonists == nil || !q.GetColonists() || q.Threats == nil || !q.GetThreats() || q.ColonistDetail == nil || q.GetColonistDetail() || q.PredatorRadius != nil || !proto.Equal(q.Scope.ExpectedIdentity, pbIdentity()) {
			t.Fatal(q, e)
		}
		return pbResult(&o.StatusReply{Outcome: &o.StatusReply_Observed{Observed: original}}), nil
	}}, time.Second)
	result, _, e := client.ReadEmergency(context.Background(), pbIdentity())
	if e != nil || result.Context.NativeGeneration != nil {
		t.Fatal(result, e)
	}
	if x, k := result.Facts.ColonistsComplete.Value(); !x || !k {
		t.Fatal("empty complete lost")
	}
	original.Context.Identity.LoadToken = proto.String("changed")
	if result.Context.Identity.GetLoadToken() != "load" {
		t.Fatal("context aliased")
	}
}
func TestEmergencyPartialMedicalAndCategories(t *testing.T) {
	v := emergencyFixture()
	v.Colonists.Pawns = []*o.PawnState{emergencyRow("c")}
	v.Colonists.Completeness = emergencyCounts(1)
	v.Colonists.Pawns[0].Health = nil
	v.Colonists.Pawns[0].Issues = []*o.ReadIssue{{Field: proto.String("health"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NATIVE_COMPONENT_MISSING.Enum()}}}
	v.Threats.Hostiles = []*o.ThreatPawn{{Pawn: emergencyRow("h")}}
	v.Threats.HuntingPredators = []*o.ThreatPawn{{Pawn: emergencyRow("p")}}
	v.Threats.IgnoredHunters = []*o.ThreatPawn{{Pawn: emergencyRow("i")}}
	v.Threats.WildPredatorsNear = []*o.ThreatPawn{{Pawn: emergencyRow("same")}}
	v.Threats.DownedNear = []*o.ThreatPawn{{Pawn: emergencyRow("same")}}
	got, e := emergencyStatus(v, pbIdentity())
	if e != nil || len(got.Facts.Threats) != 5 {
		t.Fatal(got, e)
	}
	if _, known := got.Facts.Colonists[0].Bleeding.Value(); known {
		t.Fatal("missing health fabricated")
	}
	if got.Facts.Threats[4].Kind != policy.NearbyDowned {
		t.Fatal("category lost")
	}
	v.Colonists.Completeness = nil
	got, e = emergencyStatus(v, pbIdentity())
	if e != nil {
		t.Fatal(e)
	}
	if _, known := got.Facts.ColonistsComplete.Value(); known {
		t.Fatal("missing completeness fabricated")
	}
}
func TestEmergencyContradictoryMalformedFacts(t *testing.T) {
	for name, edit := range map[string]func(*o.StatusSnapshot){
		"missing": func(v *o.StatusSnapshot) { v.Threats = nil }, "world": func(v *o.StatusSnapshot) { v.Context.Identity.LoadToken = proto.String("other") }, "generation": func(v *o.StatusSnapshot) { v.Context.NativeGeneration = proto.Uint64(0) }, "duplicate": func(v *o.StatusSnapshot) {
			v.Colonists.Pawns = []*o.PawnState{emergencyRow("c"), emergencyRow("c")}
			v.Colonists.Completeness = emergencyCounts(2)
		}, "categoryduplicate": func(v *o.StatusSnapshot) {
			v.Threats.Hostiles = []*o.ThreatPawn{{Pawn: emergencyRow("x")}, {Pawn: emergencyRow("x")}}
		}, "conflicting": func(v *o.StatusSnapshot) {
			a, b := emergencyRow("x"), emergencyRow("x")
			b.Downed = proto.Bool(true)
			v.Threats.Hostiles = []*o.ThreatPawn{{Pawn: a}}
			v.Threats.DownedNear = []*o.ThreatPawn{{Pawn: b}}
		}, "issueContradiction": func(v *o.StatusSnapshot) {
			v.Issues = []*o.ReadIssue{{Field: proto.String("colonists"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_REQUESTED.Enum()}}}
		},
	} {
		t.Run(name, func(t *testing.T) {
			v := emergencyFixture()
			edit(v)
			if _, e := emergencyStatus(v, pbIdentity()); e == nil {
				t.Fatal("invalid accepted")
			}
		})
	}
}
func TestEmergencyUnavailableAndRefusal(t *testing.T) {
	for _, refused := range []bool{false, true} {
		client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*callResult, error) {
			if refused {
				result := pbResult(&o.StatusReply{Outcome: &o.StatusReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_UNAVAILABLE.Enum()}}})
				result.IsError = true
				return result, nil
			}
			return pbResult(&o.StatusReply{Outcome: &o.StatusReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_LIMIT_EXCEEDED.Enum()}}}), nil
		}}, time.Second)
		got, _, e := client.ReadEmergency(context.Background(), pbIdentity())
		if e == nil {
			t.Fatal("unavailable cleared")
		}
		if _, known := got.Facts.ColonistsComplete.Value(); known {
			t.Fatal("unavailable fabricated")
		}
		if refused && !errors.Is(e, ErrRefused) {
			t.Fatal(e)
		}
		if !refused && !errors.Is(e, ErrUnavailable) {
			t.Fatal(e)
		}
	}
}

// A threat row's race and nearest-colonist distance reach the policy so a
// distant animal can be watched rather than held; a row without them stays
// unknown, which the policy holds (#66).
func TestEmergencyThreatCarriesRaceAndDistance(t *testing.T) {
	v := emergencyFixture()
	far := emergencyRow("far")
	far.Animal = proto.Bool(true)
	far.NearestColonistDistance = proto.Float64(120)
	v.Threats.Hostiles = []*o.ThreatPawn{{Pawn: far}, {Pawn: emergencyRow("bare")}}
	got, e := emergencyStatus(v, pbIdentity())
	if e != nil || len(got.Facts.Threats) != 2 {
		t.Fatal(got, e)
	}
	if animal, known := got.Facts.Threats[0].Animal.Value(); !known || !animal {
		t.Fatal(got.Facts.Threats[0])
	}
	if distance, known := got.Facts.Threats[0].Distance.Value(); !known || distance != 120 || !got.Facts.Threats[0].DistantThreat() {
		t.Fatal(got.Facts.Threats[0])
	}
	if _, known := got.Facts.Threats[1].Animal.Value(); known {
		t.Fatal("race fabricated")
	}
	if _, known := got.Facts.Threats[1].Distance.Value(); known || got.Facts.Threats[1].DistantThreat() {
		t.Fatal("distance fabricated")
	}
}
