package bridge

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
)

func TestHiddenConduitReplacementOnlyAllowsFinishedOrdinaryConduit(t *testing.T) {
	for _, tc := range []struct {
		name, def              string
		blueprint, frame, safe bool
	}{
		{"ordinary", "PowerConduit", false, false, true},
		{"wall", "Wall", false, false, false},
		{"battery", "Battery", false, false, false},
		{"unknown", "", false, false, false},
		{"blueprint", "PowerConduit", true, false, false},
		{"frame", "PowerConduit", false, true, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			b, _ := domain.NewBuilding("HiddenConduit", domain.Cell{}, domain.North, "")
			a, _ := domain.NewBuildingAction("conduit", b)
			snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Native: domain.NativeGeneration(^uint64(0))}
			reply := pbBatch()
			blocker := &p.PlacementBlocker{Category: proto.String("Building"), IsBlueprint: proto.Bool(tc.blueprint), IsFrame: proto.Bool(tc.frame), WouldBeWiped: proto.Bool(true), FrameWouldBeCancelled: proto.Bool(false)}
			if tc.def != "" {
				blocker.DefName = proto.String(tc.def)
			}
			reply.GetBatch().Results[0].GetEvaluated().Rotations[0].BlockingThings = []*p.PlacementBlocker{blocker}
			server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) { return pbResult(reply), nil }}
			preview, _, err := testClient(t, server, testBudget).PreviewBuilding(context.Background(), a, snapshot)
			if err != nil {
				t.Fatal(err)
			}
			safe, known := preview.Preview.SafeToPlace.Value()
			if !known || safe != tc.safe {
				t.Fatal(safe, known)
			}
			if !preview.Preview.Blockers[0].Wiped {
				t.Fatal("replacement must retain destruction evidence")
			}
		})
	}
}
