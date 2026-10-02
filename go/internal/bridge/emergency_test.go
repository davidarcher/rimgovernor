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

func emergencyCounts(uint64) *o.Completeness {
	return &o.Completeness{Filtered: proto.Uint64(0)}
}
func emergencyFixture() *o.StatusSnapshot {
	return &o.StatusSnapshot{Context: pbContext(), Threats: &o.ThreatsSnapshot{}}
}
func emergencyRow(id string) *o.PawnState {
	return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id)}, Dead: proto.Bool(false), Downed: proto.Bool(false), InBed: proto.Bool(true), Health: &o.PawnHealth{Bleeding: proto.Bool(false), NeedsTend: proto.Bool(false)}}
}

// emergencyRef is a census reference into the pawn table (#1343).
func emergencyRef(id string) *c.Ref { return &c.Ref{Id: proto.String(id)} }

// emergencyThreat is a faction-hostile threat fact row referencing id.
func emergencyThreat(id string) *o.ThreatPawn {
	return &o.ThreatPawn{Pawn: emergencyRef(id), FactionHostile: proto.Bool(true), Faction: NewRef("Faction_1")}
}

// emergencyTable is a pawn table holding rows.
func emergencyTable(rows ...*o.PawnState) Pawns {
	out := Pawns{}
	for _, row := range rows {
		out[row.Pawn.GetId()] = row
	}
	return out
}

// The status read references its pawns; ReadEmergency joins them against
// the pawn table, a list read without a stream.
func TestEmergencyReadExactRequestAndOwnedFacts(t *testing.T) {
	original := emergencyFixture()
	original.Context.NativeGeneration = nil
	original.Colonists = []*c.Ref{emergencyRef("c")}
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		if arg.Tool == "rimgovernor/observations_list_pawns" {
			return pbResult(&o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: pbContext(), Completeness: &o.Completeness{}, Pawns: []*o.PawnState{emergencyRow("c")}}}}), nil
		}
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
		if e := protojson.Unmarshal([]byte(outer.Request), q); e != nil || q.Colonists == nil || !q.GetColonists() || q.Threats == nil || !q.GetThreats() || q.PredatorRadius != nil || !proto.Equal(q.Scope.ExpectedIdentity, pbIdentity()) {
			t.Fatal(q, e)
		}
		return pbResult(&o.StatusReply{Outcome: &o.StatusReply_Observed{Observed: original}}), nil
	}}, time.Second)
	result, _, e := client.ReadEmergency(context.Background(), pbIdentity())
	if e != nil || result.Context.NativeGeneration != nil {
		t.Fatal(result, e)
	}
	if x, k := result.Facts.ColonistsComplete.Value(); !x || !k {
		t.Fatal("complete census lost")
	}
	if inBed, known := result.Facts.Colonists[0].InBed.Value(); !known || !inBed {
		t.Fatal("table row not joined", result.Facts.Colonists)
	}
	original.Context.Identity.LoadToken = proto.String("changed")
	if result.Context.Identity.GetLoadToken() != "load" {
		t.Fatal("context aliased")
	}
}
func TestEmergencyPartialMedicalAndCategories(t *testing.T) {
	v := emergencyFixture()
	colonist := emergencyRow("c")
	colonist.Health = nil
	colonist.Issues = []*o.ReadIssue{{Field: proto.String("health"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NATIVE_COMPONENT_MISSING.Enum()}}}
	pawns := emergencyTable(colonist, emergencyRow("h"), emergencyRow("p"), emergencyRow("i"), emergencyRow("same"))
	v.Colonists = []*c.Ref{emergencyRef("c")}
	v.Threats.Pawns = []*o.ThreatPawn{
		{Pawn: emergencyRef("same"), Downed: proto.Bool(true), Predator: proto.Bool(true), NearestColonistDistance: proto.Float64(5)},
		{Pawn: emergencyRef("i"), Ours: proto.Bool(true), PredatorHunt: proto.Bool(true)},
		{Pawn: emergencyRef("p"), PredatorHunt: proto.Bool(true)},
		emergencyThreat("h"),
	}
	got, e := emergencyStatus(v, pawns, pbIdentity())
	if e != nil || len(got.Facts.Threats) != 5 {
		t.Fatal(got, e)
	}
	if _, known := got.Facts.Colonists[0].Bleeding.Value(); known {
		t.Fatal("missing health fabricated")
	}
	// Threats are listed kind by kind whatever the row order.
	for i, want := range []struct {
		id   policy.PawnID
		kind policy.ThreatKind
	}{{"h", policy.Hostile}, {"p", policy.HuntingPredator}, {"i", policy.IgnoredHunter}, {"same", policy.NearbyPredator}, {"same", policy.NearbyDowned}} {
		if got.Facts.Threats[i].ID != want.id || got.Facts.Threats[i].Kind != want.kind {
			t.Fatal("category lost", i, got.Facts.Threats[i])
		}
	}
	// A colonist census issue leaves completeness unknown.
	v.Colonists = nil
	v.Issues = []*o.ReadIssue{{Field: proto.String("colonists"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}
	got, e = emergencyStatus(v, pawns, pbIdentity())
	if e != nil {
		t.Fatal(e)
	}
	if _, known := got.Facts.ColonistsComplete.Value(); known {
		t.Fatal("missing completeness fabricated")
	}
}

// A census reference the pawn table lacks is a known member with unknown
// facts until a later frame (#1343).
func TestEmergencyUnresolvedReferenceIsUnknown(t *testing.T) {
	v := emergencyFixture()
	v.Colonists = []*c.Ref{emergencyRef("gone")}
	hostile := emergencyThreat("raider")
	hostile.NearestColonistDistance = proto.Float64(12)
	v.Threats.Pawns = []*o.ThreatPawn{hostile}
	got, e := emergencyStatus(v, Pawns{}, pbIdentity())
	if e != nil || len(got.Facts.Colonists) != 1 || len(got.Facts.Threats) != 1 {
		t.Fatal(got, e)
	}
	if got.Facts.Colonists[0].ID != "gone" {
		t.Fatal(got.Facts.Colonists)
	}
	if _, known := got.Facts.Colonists[0].Dead.Value(); known {
		t.Fatal("unresolved colonist status fabricated")
	}
	threat := got.Facts.Threats[0]
	if _, known := threat.Downed.Value(); known {
		t.Fatal("unresolved threat status fabricated")
	}
	if _, known := threat.Position.Value(); known {
		t.Fatal("unresolved threat position fabricated")
	}
	if distance, known := threat.Distance.Value(); !known || distance != 12 {
		t.Fatal("the classification's own distance lost", threat)
	}
}
func TestEmergencyContradictoryMalformedFacts(t *testing.T) {
	for name, edit := range map[string]func(*o.StatusSnapshot){
		"missing": func(v *o.StatusSnapshot) { v.Threats = nil }, "world": func(v *o.StatusSnapshot) { v.Context.Identity.LoadToken = proto.String("other") }, "generation": func(v *o.StatusSnapshot) { v.Context.NativeGeneration = proto.Uint64(0) }, "duplicate": func(v *o.StatusSnapshot) {
			v.Colonists = []*c.Ref{emergencyRef("c"), emergencyRef("c")}
		}, "categoryduplicate": func(v *o.StatusSnapshot) {
			v.Threats.Pawns = []*o.ThreatPawn{emergencyThreat("x"), emergencyThreat("x")}
		}, "missingref": func(v *o.StatusSnapshot) {
			v.Threats.Pawns = []*o.ThreatPawn{{FactionHostile: proto.Bool(true)}}
		}, "distance": func(v *o.StatusSnapshot) {
			threat := emergencyThreat("x")
			threat.NearestColonistDistance = proto.Float64(-1)
			v.Threats.Pawns = []*o.ThreatPawn{threat}
		}, "issueContradiction": func(v *o.StatusSnapshot) {
			v.Colonists = []*c.Ref{emergencyRef("c")}
			v.Issues = []*o.ReadIssue{{Field: proto.String("colonists"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_REQUESTED.Enum()}}}
		}, "threatsIssue": func(v *o.StatusSnapshot) {
			v.Issues = []*o.ReadIssue{{Field: proto.String("threats"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}
		},
	} {
		t.Run(name, func(t *testing.T) {
			v := emergencyFixture()
			edit(v)
			if _, e := emergencyStatus(v, emergencyTable(emergencyRow("c"), emergencyRow("x")), pbIdentity()); e == nil {
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

// A threat's race comes from its pawn table row and its nearest-colonist
// distance from the threat row, so a distant animal can be watched rather
// than held; a pawn without them stays unknown, which the policy holds (#66).
func TestEmergencyThreatCarriesRaceAndDistance(t *testing.T) {
	v := emergencyFixture()
	far := emergencyRow("far")
	far.Animal = proto.Bool(true)
	threat := emergencyThreat("far")
	threat.NearestColonistDistance = proto.Float64(120)
	threat.FactionHostile, threat.MentalState = nil, proto.String("Manhunter")
	v.Threats.Pawns = []*o.ThreatPawn{threat, emergencyThreat("bare")}
	got, e := emergencyStatus(v, emergencyTable(far, emergencyRow("bare")), pbIdentity())
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
