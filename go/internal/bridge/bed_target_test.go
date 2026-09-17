package bridge

import (
	"context"
	"testing"
	"time"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
)

// The live building census carries the CAS ref on the BuildingState row,
// never on the EntityRef inside it (the #98 sleeping acceptance run failed
// every bed_assign dispatch with "bed target CAS token unavailable" while the
// reader looked at the EntityRef); the token is the row-level one the native
// AssignBed preview compares against.
func TestReadBedTargetTakesTheRowLevelSnapshot(t *testing.T) {
	snapshot := func() *o.BuildingsSnapshot {
		v := constructionTestSnapshot()
		row := v.Buildings[0]
		row.Snapshot = &o.SnapshotRef{Context: v.Context, EntityId: proto.String("wall"), Token: proto.String("building-c7ee")}
		return v
	}
	serve := func(v *o.BuildingsSnapshot) *Client {
		return testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
			return pbResult(&o.ListBuildingsReply{Outcome: &o.ListBuildingsReply_Observed{Observed: v}}), nil
		}}, time.Second)
	}
	got, _, err := serve(snapshot()).ReadBedTarget(context.Background(), pbIdentity(), "wall")
	if err != nil || got.Token != "building-c7ee" || got.Bed != "wall" || got.Context == nil {
		t.Fatal(got, err)
	}
	for name, change := range map[string]func(*o.BuildingsSnapshot){
		"no snapshot":  func(v *o.BuildingsSnapshot) { v.Buildings[0].Snapshot = nil },
		"other entity": func(v *o.BuildingsSnapshot) { v.Buildings[0].Snapshot.EntityId = proto.String("door") },
		"empty token":  func(v *o.BuildingsSnapshot) { v.Buildings[0].Snapshot.Token = proto.String("") },
		"entity ref only": func(v *o.BuildingsSnapshot) {
			v.Buildings[0].Building.Snapshot, v.Buildings[0].Snapshot = v.Buildings[0].Snapshot, nil
		},
	} {
		v := snapshot()
		change(v)
		if _, _, err := serve(v).ReadBedTarget(context.Background(), pbIdentity(), "wall"); err == nil {
			t.Fatalf("%s: accepted", name)
		}
	}
}
