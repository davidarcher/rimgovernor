package main

import (
	"bytes"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/encoding/protowire"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestPresenceAndVariants(t *testing.T) {
	absent := &p.PlacementCandidate{}
	zero := &p.PlacementCandidate{}
	if err := protojson.Unmarshal([]byte(`{"x":0}`), zero); err != nil {
		t.Fatal(err)
	}
	if absent.X != nil || zero.X == nil || zero.GetX() != 0 {
		t.Fatal("presence lost")
	}
	for _, raw := range []string{`{"setMode":{},"revoke":{}}`, `{"setMode":{},"setMode":{}}`, `{"acquire":{}}`} {
		if err := protojson.Unmarshal([]byte(raw), &a.ControlRequest{}); err == nil {
			t.Fatalf("accepted %s", raw)
		}
	}
	if err := protojson.Unmarshal([]byte(`{}`), &a.ControlRequest{}); err != nil {
		t.Fatal(err)
	} // Required branch is semantic validation.
}
func TestStandardParserLimits(t *testing.T) {
	for _, raw := range []string{`{"unknown":1}`, `{"x":2147483648}`, `{"x":1.5}`, `{"x":0,"x":1}`, `{"defName":"\ud800"}`, `{"x":0,}`} {
		if err := protojson.Unmarshal([]byte(raw), &p.PlacementCandidate{}); err == nil {
			t.Fatalf("accepted %s", raw)
		}
	}
	for _, raw := range []string{`{"x":1.0}`, `{"x":"1e2"}`, `{"rotation":99}`, `{"defName":"a\u0000b"}`, `{"x":null}`} {
		if err := protojson.Unmarshal([]byte(raw), &p.PlacementCandidate{}); err != nil {
			t.Fatalf("standard input %s: %v", raw, err)
		}
	}
}
func TestBinaryUnknownFieldsRetained(t *testing.T) {
	unknown := protowire.AppendTag(nil, 999, protowire.VarintType)
	unknown = protowire.AppendVarint(unknown, 7)
	m := &p.PlacementRequest{}
	if err := proto.Unmarshal(unknown, m); err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(m.ProtoReflect().GetUnknown(), unknown) {
		t.Fatal("unknown bytes lost")
	}
}
func TestFoundationRoundtrips(t *testing.T) {
	if err := run(t.TempDir()+"/fresh", "", false); err != nil {
		t.Fatal(err)
	}
}
func TestPlacementAvailabilityAndContext(t *testing.T) {
	for _, message := range []proto.Message{
		&p.PlacementMaterials{Availability: &p.PlacementMaterials_Known{Known: &p.MaterialRows{}}},
		&p.PlacementMaterials{Availability: &p.PlacementMaterials_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum(), Detail: proto.String("unreadable")}}},
		&p.PlacementReply{Outcome: &p.PlacementReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_INVALID_REQUEST.Enum(), Detail: proto.String("refused before admission")}}},
	} {
		raw, err := protojson.Marshal(message)
		if err != nil {
			t.Fatal(err)
		}
		decoded := message.ProtoReflect().Type().New().Interface()
		if err = protojson.Unmarshal(raw, decoded); err != nil || !proto.Equal(message, decoded) {
			t.Fatalf("variant lost: %s %v", raw, err)
		}
	}
	request := fixtures()["request"].(*p.PlacementRequest)
	reply := fixtures()["reply"].(*p.PlacementReply)
	if request.Identity == nil || request.Identity.MapId == nil || reply.GetBatch().Context.Identity.MapId == nil {
		t.Fatal("required zero identity fixture missing")
	}
	var invalid p.PlacementMaterials
	if err := protojson.Unmarshal([]byte(`{"known":{},"unavailable":{}}`), &invalid); err == nil {
		t.Fatal("multiple availability variants accepted")
	}
	if err := protojson.Unmarshal([]byte(`{"tick":0,"mapId":0}`), &p.PlacementBatch{}); err == nil {
		t.Fatal("old experimental batch shape accepted")
	}
}
