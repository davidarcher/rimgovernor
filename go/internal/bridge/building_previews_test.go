package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// A shell sweep of PlacementBatchLimit+6 cells is two native hops, the
// previews come back in action order each bound to its own cell, and one
// failed row fails the whole read (#599).
func TestBuildingPreviewsBatchesOneHopPerLimit(t *testing.T) {
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Native: domain.NativeGeneration(^uint64(0))}
	const cells = PlacementBatchLimit + 6
	actions := make([]domain.Action, 0, cells)
	for i := 0; i < cells; i++ {
		b, _ := domain.NewBuilding("Wall", domain.Cell{X: int32(i), Z: 1}, domain.North, "WoodLog")
		a, _ := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("wall-%d", i)), b)
		actions = append(actions, a)
	}
	for _, failRow := range []int{-1, 3} {
		t.Run(fmt.Sprint("fail row ", failRow), func(t *testing.T) {
			var sizes []int
			s := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
				var outer struct {
					Request string `json:"request"`
				}
				if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
					return nil, err
				}
				request := &p.PlacementRequest{}
				if err := protojson.Unmarshal([]byte(outer.Request), request); err != nil {
					return nil, err
				}
				sizes = append(sizes, len(request.Placements))
				batch := &p.PlacementBatch{Context: pbContext()}
				for index, candidate := range request.Placements {
					if len(sizes) == 1 && index == failRow {
						batch.Results = append(batch.Results, &p.CandidateReply{Outcome: &p.CandidateReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_NOT_FOUND.Enum(), Detail: proto.String("no such def")}}})
						continue
					}
					row := proto.Clone(pbBatch().GetBatch().Results[0]).(*p.CandidateReply)
					row.GetEvaluated().Rotations[0].OccupiedCells = []*c.Cell{{X: candidate.X, Z: candidate.Z}}
					batch.Results = append(batch.Results, row)
				}
				return pbResult(&p.PlacementReply{Outcome: &p.PlacementReply_Batch{Batch: batch}}), nil
			}}
			client := testClient(t, s, testBudget)
			previews, _, err := client.PreviewBuildings(context.Background(), actions, snapshot)
			if failRow >= 0 {
				if !errors.As(err, new(*NativeFailure)) || previews != nil {
					t.Fatalf("row failure: %v", err)
				}
				return
			}
			if err != nil || len(previews) != cells || len(sizes) != 2 || sizes[0] != PlacementBatchLimit || sizes[1] != 6 {
				t.Fatal(err, len(previews), sizes)
			}
			for i, preview := range previews {
				footprint, known := preview.Preview.Footprint.Value()
				if preview.Preview.Action != actions[i] || preview.Preview.Snapshot != snapshot || !known || len(footprint) != 1 || footprint[0] != (domain.Cell{X: int32(i), Z: 1}) {
					t.Fatal("preview", i, preview.Preview.Action.ID(), footprint)
				}
			}
		})
	}
}
