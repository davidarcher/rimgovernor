package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"math"
	"strings"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func TestPresentationReadsExactAndPartial(t *testing.T) {
	calls := 0
	server := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		calls++
		var outer struct {
			Request string `json:"request"`
		}
		if e := json.Unmarshal(arg.Arguments, &outer); e != nil {
			t.Fatal(e)
		}
		switch arg.Tool {
		case "rimgovernor/presentation_camera":
			q := &p.ReadRequest{}
			if e := protojson.Unmarshal([]byte(outer.Request), q); e != nil || !proto.Equal(q.Identity, pbIdentity()) {
				t.Fatal(q, e)
			}
			return pbResult(&p.CameraReply{Outcome: &p.CameraReply_Camera{Camera: &p.CameraState{Context: pbContext(), MapPosition: &p.MapPoint{X: proto.Float64(-1)}, ViewRect: &p.MapRect{MinX: proto.Int32(-3), MaxX: proto.Int32(2)}}}}), nil
		case "rimgovernor/presentation_selection":
			return pbResult(&p.SelectionReply{Outcome: &p.SelectionReply_Selection{Selection: &p.SelectionSnapshot{Context: pbContext(), SelectedObjects: []*p.SelectedObject{{Label: proto.String("unknown object")}}, Listing: &p.Listing{ReturnedCount: proto.Uint32(1), Complete: proto.Bool(false)}}}}), nil
		case "rimgovernor/presentation_colonists":
			q := &p.ColonistRosterRequest{}
			if e := protojson.Unmarshal([]byte(outer.Request), q); e != nil || q.CurrentMapOnly == nil || q.GetCurrentMapOnly() {
				t.Fatal(q, e)
			}
			return pbResult(&p.ColonistRosterReply{Outcome: &p.ColonistRosterReply_Roster{Roster: &p.ColonistRoster{Context: pbContext(), Colonists: []*p.ColonistReference{{Spawned: proto.Bool(false)}}}}}), nil
		default:
			t.Fatal(arg.Tool)
			return nil, errors.New("unexpected")
		}
	}}
	client := testClient(t, server, testBudget)
	camera, _, e := client.ReadCamera(context.Background(), &p.ReadRequest{Identity: pbIdentity()})
	if e != nil || camera.GetCamera().RootSize != nil || camera.GetCamera().MapPosition.Z != nil {
		t.Fatal(camera, e)
	}
	selection, _, e := client.ReadSelection(context.Background(), &p.ReadRequest{Identity: pbIdentity()})
	if e != nil || selection.GetSelection().SelectedObjects[0].Id != nil {
		t.Fatal(selection, e)
	}
	roster, _, e := client.ReadColonistRoster(context.Background(), &p.ColonistRosterRequest{Identity: pbIdentity(), CurrentMapOnly: proto.Bool(false)})
	if e != nil || roster.GetRoster().Colonists[0].PawnId != nil || roster.GetRoster().Colonists[0].Spawned == nil {
		t.Fatal(roster, e)
	}
	if _, _, e = client.ReadColonistRoster(context.Background(), &p.ColonistRosterRequest{Identity: pbIdentity()}); e == nil || calls != 3 {
		t.Fatal("missing bool dispatched", calls, e)
	}
}
func TestPresentationReadInvalidAndRefused(t *testing.T) {
	for _, tc := range []struct {
		name    string
		reply   proto.Message
		refused bool
	}{
		{"empty", &p.CameraReply{}, false},
		{"missingContext", &p.CameraReply{Outcome: &p.CameraReply_Camera{Camera: &p.CameraState{}}}, false},
		{"nan", &p.CameraReply{Outcome: &p.CameraReply_Camera{Camera: &p.CameraState{Context: pbContext(), RootSize: proto.Float64(math.NaN())}}}, false},
		{"negativeSize", &p.CameraReply{Outcome: &p.CameraReply_Camera{Camera: &p.CameraState{Context: pbContext(), RootSize: proto.Float64(-1)}}}, false},
		{"inverted", &p.CameraReply{Outcome: &p.CameraReply_Camera{Camera: &p.CameraState{Context: pbContext(), ViewRect: &p.MapRect{MinX: proto.Int32(3), MaxX: proto.Int32(1)}}}}, false},
		{"refused", &p.CameraReply{Outcome: &p.CameraReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_UNAVAILABLE.Enum()}}}, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) { return pbResult(tc.reply), nil }}, time.Second)
			v, raw, e := client.ReadCamera(context.Background(), &p.ReadRequest{Identity: pbIdentity()})
			if e == nil || len(raw.Envelope) == 0 {
				t.Fatal(v, e)
			}
			if tc.refused && (!errors.Is(e, ErrRefused) || v == nil) {
				t.Fatal(v, e)
			}
		})
	}
}
func TestPresentationFactValidation(t *testing.T) {
	id := pbIdentity()
	q := &p.ColonistRosterRequest{Identity: id, CurrentMapOnly: proto.Bool(true)}
	for _, change := range []func(*p.SelectionSnapshot){func(v *p.SelectionSnapshot) { v.Context.Identity.LoadToken = proto.String("other") }, func(v *p.SelectionSnapshot) {
		v.SelectedObjects = []*p.SelectedObject{{Id: proto.String("x")}, {Id: proto.String("x")}}
	}, func(v *p.SelectionSnapshot) { v.Listing = &p.Listing{ReturnedCount: proto.Uint32(math.MaxUint32)} }, func(v *p.SelectionSnapshot) {
		v.Listing = &p.Listing{TotalCount: proto.Uint32(1), Complete: proto.Bool(true)}
	}, func(v *p.SelectionSnapshot) { v.Fingerprint = proto.String(strings.Repeat("x", 4097)) }, func(v *p.SelectionSnapshot) {
		v.SelectedObjects = []*p.SelectedObject{{Position: &c.Cell{X: proto.Int32(-1)}}}
	}} {
		v := &p.SelectionSnapshot{Context: pbContext()}
		change(v)
		if e := validateSelection(v, id); e == nil {
			t.Fatal(v)
		}
	}
	for _, v := range []*p.ColonistRoster{{Context: pbContext(), Colonists: []*p.ColonistReference{{PawnId: proto.String("p")}, {PawnId: proto.String("p")}}}, {Context: pbContext(), Colonists: []*p.ColonistReference{{MapId: proto.Int32(1)}}}, {Context: pbContext(), Listing: &p.Listing{Complete: proto.Bool(true), Truncated: proto.Bool(true)}}} {
		if e := validateRoster(v, q); e == nil {
			t.Fatal(v)
		}
	}
	if e := validateRoster(&p.ColonistRoster{Context: pbContext()}, q); e != nil {
		t.Fatal(e)
	}
	dossier := func(change func(*o.PawnState)) *p.ColonistRoster {
		d := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("p")}, Colonist: proto.Bool(true)}
		change(d)
		return &p.ColonistRoster{Context: pbContext(), Colonists: []*p.ColonistReference{{PawnId: proto.String("p"), Dossier: d}}}
	}
	withDossier := &p.ColonistRosterRequest{Identity: id, CurrentMapOnly: proto.Bool(true), IncludeDossier: proto.Bool(true)}
	if e := validateRoster(dossier(func(*o.PawnState) {}), withDossier); e != nil {
		t.Fatal(e)
	}
	if e := validateRoster(dossier(func(*o.PawnState) {}), q); e == nil {
		t.Fatal("unrequested dossier accepted")
	}
	if e := validateRoster(&p.ColonistRoster{Context: pbContext(), Colonists: []*p.ColonistReference{{PawnId: proto.String("p")}}}, withDossier); e == nil {
		t.Fatal("missing dossier accepted")
	}
	for _, change := range []func(*o.PawnState){func(d *o.PawnState) { d.Pawn.Id = proto.String("other") }, func(d *o.PawnState) { d.Settings = &o.PawnSettings{} },
		func(d *o.PawnState) { d.AnimalState = &o.AnimalState{} }, func(d *o.PawnState) { d.Dead = proto.Bool(true) }, func(d *o.PawnState) { d.Colonist = nil }} {
		if e := validateRoster(dossier(change), withDossier); e == nil {
			t.Fatal("invalid dossier accepted")
		}
	}
	if e := validateSelection(&p.SelectionSnapshot{Context: pbContext(), Listing: &p.Listing{TotalCount: proto.Uint32(0), ReturnedCount: proto.Uint32(0), Complete: proto.Bool(true), Truncated: proto.Bool(false)}}, id); e != nil {
		t.Fatal(e)
	}
}

func TestPresentationMalformedProtoBoundary(t *testing.T) {
	for _, payload := range []string{`{"camera":{"context":null}}`, `{"camera":{"context":{"identity":{"colonyId":"colony","loadToken":"load","mapId":0},"tick":"1"},"rootSize":1,"rootSize":2}}`, `{"camera":{"context":{"identity":{"colonyId":"colony","loadToken":"load","mapId":2147483648},"tick":"1"}}}`} {
		client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
			return &mcp.CallToolResult{StructuredContent: encode(struct {
				Payload string `json:"payload"`
			}{payload})}, nil
		}}, time.Second)
		if _, _, err := client.ReadCamera(context.Background(), &p.ReadRequest{Identity: pbIdentity()}); err == nil {
			t.Fatal("malformed response accepted")
		}
	}
}

func TestPresentationMCPErrorPreservesOnlyTypedFailure(t *testing.T) {
	failureValue := &c.Failure{Code: c.FailureCode_FAILURE_CODE_UNAVAILABLE.Enum()}
	cases := []struct {
		name            string
		failed, success proto.Message
		call            func(*Client) (proto.Message, Result, error)
	}{
		{"camera", &p.CameraReply{Outcome: &p.CameraReply_Failure{Failure: failureValue}}, &p.CameraReply{Outcome: &p.CameraReply_Camera{Camera: &p.CameraState{Context: pbContext()}}}, func(client *Client) (proto.Message, Result, error) {
			return client.ReadCamera(context.Background(), &p.ReadRequest{Identity: pbIdentity()})
		}},
		{"selection", &p.SelectionReply{Outcome: &p.SelectionReply_Failure{Failure: failureValue}}, &p.SelectionReply{Outcome: &p.SelectionReply_Selection{Selection: &p.SelectionSnapshot{Context: pbContext()}}}, func(client *Client) (proto.Message, Result, error) {
			return client.ReadSelection(context.Background(), &p.ReadRequest{Identity: pbIdentity()})
		}},
		{"roster", &p.ColonistRosterReply{Outcome: &p.ColonistRosterReply_Failure{Failure: failureValue}}, &p.ColonistRosterReply{Outcome: &p.ColonistRosterReply_Roster{Roster: &p.ColonistRoster{Context: pbContext()}}}, func(client *Client) (proto.Message, Result, error) {
			return client.ReadColonistRoster(context.Background(), &p.ColonistRosterRequest{Identity: pbIdentity(), CurrentMapOnly: proto.Bool(true)})
		}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			for _, variant := range []string{"typed", "malformed", "success"} {
				t.Run(variant, func(t *testing.T) {
					server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
						reply := pbResult(tc.failed)
						if variant == "success" {
							reply = pbResult(tc.success)
						}
						if variant == "malformed" {
							reply = &mcp.CallToolResult{StructuredContent: encode(struct {
								Payload string `json:"payload"`
							}{`{"failure":`})}
						}
						reply.IsError = true
						return reply, nil
					}}
					client := testClient(t, server, testBudget)
					reply, raw, err := tc.call(client)
					if !errors.Is(err, ErrRefused) || len(raw.Envelope) == 0 {
						t.Fatal("error flag lost", reply, err)
					}
					var native *NativeFailure
					if variant == "typed" {
						if !errors.As(err, &native) || !proto.Equal(reply, tc.failed) || !proto.Equal(native.Value, failureValue) {
							t.Fatal("typed failure lost", reply, err)
						}
					} else {
						var generic *Refusal
						if errors.As(err, &native) || !errors.As(err, &generic) {
							t.Fatal("error payload became native result", reply, err)
						}
					}
				})
			}
		})
	}
}
