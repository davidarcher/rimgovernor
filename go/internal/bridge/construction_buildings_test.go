package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func constructionTestSnapshot() *o.BuildingsSnapshot {
	return &o.BuildingsSnapshot{Context: authorityTestContext(7), Buildings: []*o.BuildingState{{Building: &o.EntityRef{Id: proto.String("wall"), DefName: proto.String("Wall"), MapId: proto.Int32(0), Position: &c.Cell{X: proto.Int32(3), Z: proto.Int32(7)}}, Status: proto.String("built"), Rotation: proto.String("North"), Stuff: proto.String("WoodLog")}}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(20), Unreadable: proto.Uint64(0)}}
}

func TestConstructionBuildingsExactQueryAndPartialMissingResult(t *testing.T) {
	id, ids := pbIdentity(), []string{"wall", "missing"}
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		if arg.Tool != "rimgovernor/observations_list_buildings" {
			t.Fatal(arg.Tool)
		}
		var outer struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
			t.Fatal(err)
		}
		q := &o.ListBuildingsRequest{}
		if err := protojson.Unmarshal([]byte(outer.Request), q); err != nil {
			t.Fatal(err)
		}
		want := &o.ListBuildingsRequest{Scope: &o.ReadScope{ExpectedIdentity: pbIdentity()}, Ids: []string{"wall", "missing"}, Statuses: []string{"built"}, PlayerOnly: proto.Bool(true), Category: proto.String("artificial"), Page: &c.PageRequest{Limit: proto.Uint32(2)}}
		if !proto.Equal(q, want) {
			t.Fatal(q)
		}
		id.LoadToken = proto.String("changed")
		ids[0] = "changed"
		return pbResult(&o.ListBuildingsReply{Outcome: &o.ListBuildingsReply_Observed{Observed: constructionTestSnapshot()}}), nil
	}}, time.Second)
	got, _, err := client.ReadConstructionBuildings(context.Background(), id, ids)
	if err != nil || len(got.GetObserved().GetBuildings()) != 1 {
		t.Fatal(got, err)
	}
}

func TestConstructionBuildingsRejectMalformedEvidence(t *testing.T) {
	changes := map[string]func(*o.BuildingsSnapshot){
		"world":       func(v *o.BuildingsSnapshot) { v.Context.Identity.LoadToken = proto.String("other") },
		"tick":        func(v *o.BuildingsSnapshot) { v.Context.Tick = nil },
		"unrequested": func(v *o.BuildingsSnapshot) { v.Buildings[0].Building.Id = proto.String("other") },
		"duplicate": func(v *o.BuildingsSnapshot) {
			v.Buildings = append(v.Buildings, v.Buildings[0])
			v.Completeness.Matched = proto.Uint64(2)
			v.Completeness.Returned = proto.Uint64(2)
		},
		"blueprint":  func(v *o.BuildingsSnapshot) { v.Buildings[0].Status = proto.String("blueprint") },
		"map":        func(v *o.BuildingsSnapshot) { v.Buildings[0].Building.MapId = proto.Int32(8) },
		"def":        func(v *o.BuildingsSnapshot) { v.Buildings[0].Building.DefName = nil },
		"position":   func(v *o.BuildingsSnapshot) { v.Buildings[0].Building.Position = nil },
		"rotation":   func(v *o.BuildingsSnapshot) { v.Buildings[0].Rotation = proto.String("up") },
		"lowercase":  func(v *o.BuildingsSnapshot) { v.Buildings[0].Rotation = proto.String("north") },
		"stuff":      func(v *o.BuildingsSnapshot) { v.Buildings[0].Stuff = proto.String("") },
		"unreadable": func(v *o.BuildingsSnapshot) { v.Completeness.Unreadable = proto.Uint64(1) },
		"page":       func(v *o.BuildingsSnapshot) { v.Completeness.Page.Complete = proto.Bool(false) },
		"cursor":     func(v *o.BuildingsSnapshot) { v.Completeness.Page.NextCursor = proto.String("next") },
		"count":      func(v *o.BuildingsSnapshot) { v.Completeness.Matched = proto.Uint64(2) },
	}
	for name, change := range changes {
		t.Run(name, func(t *testing.T) {
			v := constructionTestSnapshot()
			change(v)
			if err := ValidateConstructionBuildings(v, pbIdentity(), []string{"wall", "missing"}); err == nil {
				t.Fatal(v)
			}
		})
	}
}

func TestConstructionBuildingsPreservesTypedRefusal(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		result := pbResult(&o.ListBuildingsReply{Outcome: &o.ListBuildingsReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_INVALID_REQUEST.Enum()}}})
		result.IsError = true
		return result, nil
	}}, time.Second)
	reply, _, err := client.ReadConstructionBuildings(context.Background(), pbIdentity(), []string{"wall"})
	if !errors.Is(err, ErrRefused) || reply == nil || reply.GetFailure() == nil {
		t.Fatal(reply, err)
	}
}

func TestConstructionBuildingsColonyQueryFiltersPlayerBuilt(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		var outer struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
			t.Fatal(err)
		}
		q := &o.ListBuildingsRequest{}
		if err := protojson.Unmarshal([]byte(outer.Request), q); err != nil {
			t.Fatal(err)
		}
		if len(q.Ids) != 0 || !q.GetPlayerOnly() || q.GetCategory() != "artificial" || len(q.Statuses) != 1 || q.Statuses[0] != "built" || q.Page.GetLimit() != 256 {
			t.Fatal(q)
		}
		return pbResult(&o.ListBuildingsReply{Outcome: &o.ListBuildingsReply_Observed{Observed: constructionTestSnapshot()}}), nil
	}}, time.Second)
	if _, _, err := client.ReadConstructionBuildings(context.Background(), pbIdentity(), nil); err != nil {
		t.Fatal(err)
	}
	snapshot := constructionTestSnapshot()
	snapshot.Completeness.Page.Complete = proto.Bool(false)
	if err := ValidateConstructionBuildings(snapshot, pbIdentity(), nil); err == nil {
		t.Fatal("incomplete colony accepted")
	}
}
