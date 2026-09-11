package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func TestBuildingPreviewProjection(t *testing.T) {
	b, _ := domain.NewBuilding("Wall", domain.Cell{}, domain.North, "WoodLog")
	a, _ := domain.NewBuildingAction("wall", b)
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Native: domain.NativeGeneration(^uint64(0))}
	for _, test := range []struct {
		name                       string
		change                     func(*p.PlacementBatch)
		knownSafe, safe, wantError bool
	}{
		{"complete", func(*p.PlacementBatch) {}, true, true, false},
		{"destructive", func(v *p.PlacementBatch) {
			v.Results[0].GetEvaluated().Rotations[0].BlockingThings = []*p.PlacementBlocker{{Category: proto.String("Building"), IsBlueprint: proto.Bool(false), IsFrame: proto.Bool(false), WouldBeWiped: proto.Bool(true), FrameWouldBeCancelled: proto.Bool(false)}}
		}, true, false, false},
		{"unavailable materials", func(v *p.PlacementBatch) {
			v.Results[0].GetEvaluated().Materials = &p.PlacementMaterials{Availability: &p.PlacementMaterials_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_OBSERVED.Enum()}}}
		}, false, false, false},
		{"new generation", func(v *p.PlacementBatch) { v.Context.NativeGeneration = proto.Uint64(3) }, false, false, true},
		{"unknown generation", func(v *p.PlacementBatch) { v.Context.NativeGeneration = nil }, false, false, true},
		{"changed world", func(v *p.PlacementBatch) { v.Context.Identity.LoadToken = proto.String("replacement") }, false, false, true},
	} {
		t.Run(test.name, func(t *testing.T) {
			reply := pbBatch()
			test.change(reply.GetBatch())
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
				candidate := request.Placements[0]
				if arg.Tool != "rimgovernor/placement_preview" || len(request.Placements) != 1 || candidate.GetDefName() != "Wall" || candidate.GetStuff() != "WoodLog" || candidate.GetRotation() != p.Rotation_ROTATION_NORTH {
					t.Error("request changed action")
				}
				return pbResult(reply), nil
			}}
			client := testClient(t, s, time.Second)
			result, raw, err := client.PreviewBuilding(context.Background(), a, snapshot)
			if test.wantError {
				if !errors.Is(err, ErrContract) {
					t.Fatalf("error=%v", err)
				}
				return
			}
			if err != nil || len(raw.Envelope) == 0 {
				t.Fatalf("preview: %v", err)
			}
			safe, known := result.Preview.SafeToPlace.Value()
			if safe != test.safe || known != test.knownSafe {
				t.Fatalf("safe=%v known=%v", safe, known)
			}
			if result.Preview.Action != a || result.Preview.Snapshot != snapshot || result.Stock.Tick != result.Preview.Tick {
				t.Fatal("projection lost exact identity/tick")
			}
			costs, known := result.Preview.Costs.Value()
			if !known || len(costs) != 0 {
				t.Fatal("complete empty cost scan lost")
			}
			cells, known := result.Preview.Footprint.Value()
			if !known || len(cells) != 1 || cells[0] != (domain.Cell{}) {
				t.Fatal("map zero footprint lost")
			}
			if test.knownSafe {
				if _, known = result.Stock.Values[0].Available.Value(); known {
					t.Fatal("unknown stock became zero")
				}
				if value, known := result.Stock.Values[1].Available.Value(); !known || value != 0 {
					t.Fatal("known zero stock lost")
				}
			}
		})
	}
}
