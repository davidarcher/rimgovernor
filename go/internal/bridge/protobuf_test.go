package bridge

import (
	"context"
	"encoding/json"
	"errors"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"strings"
	"testing"
	"time"
)

const protoSchema = `{"type":"object","properties":{"request":{"type":"object"}},"additionalProperties":false}`

func pbIdentity() *c.Identity {
	return &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}
}
func pbContext() *c.ObservationContext {
	return &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(9223372036854775807), NativeGeneration: proto.Uint64(18446744073709551615)}
}
func pbLoaded() *l.IdentityReply {
	return &l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: pbContext(), Paused: proto.Bool(false)}}}
}
func pbRequest() *p.PlacementRequest {
	return &p.PlacementRequest{Identity: pbIdentity(), Placements: []*p.PlacementCandidate{{DefName: proto.String("Wall"), X: proto.Int32(0), Z: proto.Int32(0), Rotation: p.Rotation_ROTATION_NORTH.Enum()}}}
}
func pbBatch() *p.PlacementReply {
	return &p.PlacementReply{Outcome: &p.PlacementReply_Batch{Batch: &p.PlacementBatch{Context: pbContext(), Results: []*p.CandidateReply{{Outcome: &p.CandidateReply_Evaluated{Evaluated: &p.PlacementEvaluated{CanPlace: proto.Bool(true), MadeFromStuff: proto.Bool(false), Passability: p.Passability_PASSABILITY_IMPASSABLE.Enum(), IsDoor: proto.Bool(false), ResearchFinished: proto.Bool(true), BuildableByPlayer: proto.Bool(true), Materials: &p.PlacementMaterials{Availability: &p.PlacementMaterials_Known{Known: &p.MaterialRows{Rows: []*p.PlacementMaterialStock{{DefName: proto.String("Steel")}, {DefName: proto.String("WoodLog"), Available: proto.Int32(0)}}}}}, Rotations: []*p.PlacementRotation{{Rotation: p.Rotation_ROTATION_NORTH.Enum(), Accepted: proto.Bool(true), OccupiedCells: []*c.Cell{{X: proto.Int32(0), Z: proto.Int32(0)}}}}}}}}}}}
}
func pbResult(message proto.Message) *mcp.CallToolResult {
	inner, err := protojson.Marshal(message)
	if err != nil {
		panic(err)
	}
	outer := encode(struct {
		Payload   string `json:"payload"`
		Operation struct {
			ID string `json:"id"`
		} `json:"operation"`
	}{Payload: string(inner)})
	return &mcp.CallToolResult{StructuredContent: outer}
}
func TestOfficialReadSDKBoundary(t *testing.T) {
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		var outer struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
			return nil, err
		}
		switch arg.Tool {
		case "rimgovernor/lifecycle_read_identity":
			if outer.Request != "{}" {
				t.Error("identity request not actual ProtoJSON")
			}
			return pbResult(pbLoaded()), nil
		case "rimgovernor/observations_read_status":
			q := &o.StatusRequest{}
			if err := protojson.Unmarshal([]byte(outer.Request), q); err != nil {
				return nil, err
			}
			if q.Colonists == nil || q.GetColonists() || q.Threats == nil || q.GetThreats() || !sameIdentity(q.Scope.ExpectedIdentity, pbIdentity()) {
				t.Error("wrong status query")
			}
			return pbResult(&o.StatusReply{Outcome: &o.StatusReply_Observed{Observed: &o.StatusSnapshot{Context: pbContext()}}}), nil
		case "rimgovernor/placement_preview":
			q := &p.PlacementRequest{}
			if err := protojson.Unmarshal([]byte(outer.Request), q); err != nil {
				return nil, err
			}
			return pbResult(pbBatch()), nil
		default:
			t.Error("unapproved tool", arg.Tool)
			return nil, errors.New("bad tool")
		}
	}}
	client := testClient(t, s, time.Second)
	identity, raw, err := client.Identity(context.Background())
	if err != nil || identity.GetLoaded().Context.GetNativeGeneration() != ^uint64(0) || identity.GetLoaded().Paused == nil || identity.GetLoaded().GetPaused() || len(raw.Envelope) == 0 {
		t.Fatalf("identity %v %v", identity, err)
	}
	if _, _, err = client.Status(context.Background(), pbIdentity()); err != nil {
		t.Fatal(err)
	}
	reply, _, err := client.PlacementPreviews(context.Background(), pbRequest())
	if err != nil {
		t.Fatal(err)
	}
	rows := reply.GetBatch().Results[0].GetEvaluated().Materials.GetKnown().Rows
	if rows[0].Available != nil || rows[1].Available == nil {
		t.Fatal("unknown/zero material conflated")
	}
	for _, name := range []string{"home/colony_identity", "home/status", "home/placement_previews", "rimgovernor/operations_execute", "rimgovernor/clock_read_events"} {
		if _, err = client.protoRead(context.Background(), name, &l.IdentityRequest{}, &l.IdentityReply{}); !errors.Is(err, ErrContract) {
			t.Fatalf("unapproved name accepted: %s", name)
		}
	}
	if len(s.calls) != 3 {
		t.Fatal("unexpected invocation")
	}
}
func TestProtoRefusalUnavailableAndWrapperFailures(t *testing.T) {
	failureReply := &l.IdentityReply{Outcome: &l.IdentityReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_UNAVAILABLE.Enum(), Detail: proto.String("not ready")}}}
	for _, sdkError := range []bool{false, true} {
		s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
			r := pbResult(failureReply)
			r.IsError = sdkError
			return r, nil
		}}
		reply, raw, err := testClient(t, s, time.Second).Identity(context.Background())
		var refusal *NativeFailure
		if !errors.As(err, &refusal) || reply.GetFailure() == nil || len(raw.Envelope) == 0 {
			t.Fatalf("typed failure lost %v", err)
		}
	}
	s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(&l.IdentityReply{Outcome: &l.IdentityReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_LOADED.Enum()}}}), nil
	}}
	if _, _, err := testClient(t, s, time.Second).Identity(context.Background()); !errors.Is(err, ErrUnavailable) {
		t.Fatal(err)
	}
	for _, raw := range []string{`{"payload":{}}`, `{"payload":null}`, `{"payload":"{}","payload":"{}"}`, `{"payload":"{}","unknownArguments":["oops"]}`, `{"payload":"{\"unknown\":1}"}`, `{"payload":"{}"}`, `{"payload":"` + strings.Repeat("x", maxProtoBytes+1) + `"}`, `{"payload":"\ud800"}`} {
		t.Run(raw[:min(len(raw), 40)], func(t *testing.T) {
			s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) { return structured(raw), nil }}
			reply, result, err := testClient(t, s, time.Second).Identity(context.Background())
			if err == nil || reply != nil || len(result.Envelope) == 0 {
				t.Fatalf("invalid wrapper accepted/lost receipt %v", err)
			}
		})
	}
}
func TestPlacementValidationBeforeDispatchAndCompleteFacts(t *testing.T) {
	s := &testServer{schema: protoSchema}
	client := testClient(t, s, time.Second)
	for _, change := range []func(*p.PlacementRequest){func(q *p.PlacementRequest) { q.Identity.MapId = nil }, func(q *p.PlacementRequest) { q.Identity.ColonyId = proto.String("a\x00b") }, func(q *p.PlacementRequest) { q.Placements[0].X = nil }, func(q *p.PlacementRequest) { q.Placements[0].Rotation = p.Rotation(99).Enum() }, func(q *p.PlacementRequest) { q.Placements[0].DefName = proto.String(strings.Repeat("界", 86)) }, func(q *p.PlacementRequest) { q.Placements = nil }} {
		q := pbRequest()
		change(q)
		if _, _, err := client.PlacementPreviews(context.Background(), q); !errors.Is(err, ErrContract) {
			t.Fatal("invalid request accepted", err)
		}
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid request dispatched")
	}
	for _, change := range []func(*p.PlacementReply){func(r *p.PlacementReply) { r.GetBatch().Context.Identity.LoadToken = proto.String("replacement") }, func(r *p.PlacementReply) { r.GetBatch().Results[0].GetEvaluated().CanPlace = nil }, func(r *p.PlacementReply) { r.GetBatch().Results[0].GetEvaluated().Materials = &p.PlacementMaterials{} }, func(r *p.PlacementReply) { r.GetBatch().Results[0].GetEvaluated().Rotations[0].OccupiedCells = nil }, func(r *p.PlacementReply) { r.GetBatch().Results = nil }} {
		r := pbBatch()
		change(r)
		if err := validatePlacementBatch(pbRequest(), r.GetBatch()); err == nil {
			t.Fatal("incomplete placement evidence accepted")
		}
	}
	r := pbBatch()
	r.GetBatch().Results[0].GetEvaluated().Materials = &p.PlacementMaterials{Availability: &p.PlacementMaterials_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}
	if err := validatePlacementBatch(pbRequest(), r.GetBatch()); err != nil {
		t.Fatal("truthful unknown materials refused", err)
	}
}

func TestEveryCanonicalUnavailableReason(t *testing.T) {
	for number, name := range c.UnavailableReason_name {
		if number == 0 {
			continue
		}
		t.Run(name, func(t *testing.T) {
			reason := c.UnavailableReason(number)
			s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(&l.IdentityReply{Outcome: &l.IdentityReply_Unavailable{Unavailable: &c.Unavailable{Reason: reason.Enum()}}}), nil
			}}
			reply, raw, err := testClient(t, s, time.Second).Identity(context.Background())
			var unavailable *NativeUnavailable
			if !errors.As(err, &unavailable) || reply.GetUnavailable().GetReason() != reason || len(raw.Envelope) == 0 {
				t.Fatalf("reason %v not preserved: %v", reason, err)
			}
		})
	}
	for _, value := range []*c.Unavailable{nil, {}, {Reason: c.UnavailableReason(0).Enum()}, {Reason: c.UnavailableReason(-1).Enum()}, {Reason: c.UnavailableReason(11).Enum()}, {Reason: c.UnavailableReason(999).Enum()}} {
		if err := validateUnavailable(value); !errors.Is(err, ErrContract) {
			t.Fatalf("missing/unknown reason accepted: %v", value)
		}
	}
}

func TestSDKRefusalPreservedWithoutCanonicalFailure(t *testing.T) {
	for _, atDetail := range []bool{false, true} {
		for _, raw := range []string{`{"refused":true}`, `{"payload":"invalid"}`, `{"unknownArguments":["request"]}`} {
			result := structured(raw)
			result.IsError = true
			s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) { return result, nil }}
			if atDetail {
				s.detailResult = result
			}
			reply, receipt, err := testClient(t, s, time.Second).Identity(context.Background())
			var refusal *Refusal
			if !errors.As(err, &refusal) || errors.Is(err, ErrContract) || reply != nil || len(receipt.Envelope) == 0 {
				t.Fatalf("detail=%v raw=%s err=%v", atDetail, raw, err)
			}
			if atDetail && len(s.calls) != 0 {
				t.Fatal("called after description refusal")
			}
		}
	}
}
func TestStatusRejectsUnknownBinaryIdentityBeforeDispatch(t *testing.T) {
	identity := pbIdentity()
	identity.ProtoReflect().SetUnknown([]byte{0x20, 0x01})
	s := &testServer{schema: protoSchema}
	if reply, _, err := testClient(t, s, time.Second).Status(context.Background(), identity); !errors.Is(err, ErrContract) || reply != nil {
		t.Fatalf("unknown fields discarded: %v", err)
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid identity dispatched")
	}
}

func TestConnectRefusesForeignOwnerAndExplicitTakeover(t *testing.T) {
	s := &testServer{connectResult: structured(`{"foreignOwner":true,"ownerPID":24}`)}
	client := testClient(t, s, time.Second)
	if raw, err := client.ConnectGame(context.Background()); !errors.Is(err, ErrRefused) || len(raw.Envelope) == 0 {
		t.Fatalf("foreign ownership accepted: %v", err)
	}
	if strings.Contains(string(s.connectArgs), "forceTakeover") {
		t.Fatal("implicit takeover")
	}
	s.connectResult = structured(`{"success":true}`)
	if _, err := client.ConnectGameWithTakeover(context.Background()); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(s.connectArgs), `"forceTakeover":true`) {
		t.Fatalf("explicit takeover missing: %s", s.connectArgs)
	}
}

func TestTypedAdapterTransportRemainsClosed(t *testing.T) {
	s := &testServer{schema: protoSchema}
	client := testClient(t, s, time.Second)
	for _, name := range []string{"rimgovernor/authority_control", "rimgovernor/operations_execute", "rimgovernor/clock_control"} {
		if _, err := client.protoRead(context.Background(), name, &l.IdentityRequest{}, &l.IdentityReply{}); !errors.Is(err, ErrContract) {
			t.Fatalf("mutation admitted through read: %s", name)
		}
	}
	if _, err := client.protoCall(context.Background(), "rimgovernor/clock_control", &l.IdentityRequest{}, &l.IdentityReply{}); !errors.Is(err, ErrContract) {
		t.Fatal("unreviewed method admitted")
	}
	if len(s.calls) != 0 {
		t.Fatal("unreviewed call reached SDK")
	}
}
func TestAdditionalTypedSDKFailures(t *testing.T) {
	f := &c.Failure{Code: c.FailureCode_FAILURE_CODE_UNAVAILABLE.Enum()}
	cases := []struct {
		name                     string
		request, reply, received proto.Message
		read                     bool
	}{
		{"rimgovernor/authority_read_status", &a.StatusRequest{}, &a.StatusReply{Outcome: &a.StatusReply_Failure{Failure: f}}, &a.StatusReply{}, true},
		{"rimgovernor/authority_control", &a.ControlRequest{}, &a.ControlReply{Outcome: &a.ControlReply_Failure{Failure: f}}, &a.ControlReply{}, false},
		{"rimgovernor/operations_execute", &op.ExecuteRequest{}, &op.ExecuteReply{Outcome: &op.ExecuteReply_Failure{Failure: f}}, &op.ExecuteReply{}, false},
		{"rimgovernor/receipts_lookup", &r.LookupRequest{}, &r.LookupReply{Outcome: &r.LookupReply_Failure{Failure: f}}, &r.LookupReply{}, true},
		{"rimgovernor/receipts_observe_progress", &r.ProgressRequest{}, &r.ProgressReply{Outcome: &r.ProgressReply_Failure{Failure: f}}, &r.ProgressReply{}, true},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				result := pbResult(tc.reply)
				result.IsError = true
				return result, nil
			}}
			client := testClient(t, s, time.Second)
			call := client.protoCall
			if tc.read {
				call = client.protoRead
			}
			raw, err := call(context.Background(), tc.name, tc.request, tc.received)
			if err != nil || !proto.Equal(tc.reply, tc.received) || len(raw.Envelope) == 0 {
				t.Fatalf("typed failure not delivered to semantic adapter: %v", err)
			}
		})
	}
}
