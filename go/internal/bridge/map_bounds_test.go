package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"math"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func boundsSnapshot() *o.CellsSnapshot {
	return &o.CellsSnapshot{Context: pbContext(), MapSize: &o.MapSize{Width: proto.Uint32(20), Height: proto.Uint32(30)}, Cells: []*o.CellState{{Cell: &c.Cell{X: proto.Int32(2), Z: proto.Int32(3)}}}, AppliedFields: mapBoundsFields(), Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}
}
func TestReadMapBoundsFixedSDKQuery(t *testing.T) {
	calls := 0
	server := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		calls++
		if arg.Tool != "rimgovernor/observations_get_cells" {
			t.Fatal(arg.Tool)
		}
		var outer struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
			t.Fatal(err)
		}
		request := &o.GetCellsRequest{}
		if err := protojson.Unmarshal([]byte(outer.Request), request); err != nil {
			t.Fatal(err)
		}
		if !proto.Equal(request.Scope.ExpectedIdentity, pbIdentity()) || request.Page.GetLimit() != 1 || request.Page.Cursor != nil || !proto.Equal(request.Fields, mapBoundsFields()) || request.GetRectangle() != nil || len(request.GetExactCells().GetCells()) != 1 || !proto.Equal(request.GetExactCells().Cells[0], boundsSnapshot().Cells[0].Cell) {
			t.Fatal("query differs", request)
		}
		return pbResult(&o.GetCellsReply{Outcome: &o.GetCellsReply_Observed{Observed: boundsSnapshot()}}), nil
	}}
	client := testClient(t, server, time.Second)
	bounds, raw, err := client.ReadMapBounds(context.Background(), pbIdentity(), domain.Cell{X: 2, Z: 3})
	if err != nil || bounds.Context == nil || bounds.Bounds.Width != 20 || bounds.Bounds.Height != 30 || len(raw.Envelope) == 0 || calls != 1 {
		t.Fatalf("%v %v calls%d", bounds, err, calls)
	}
}
func TestMapBoundsRequiresExactCompleteKnownFacts(t *testing.T) {
	for name, edit := range map[string]func(*o.CellsSnapshot){
		"map absent": func(s *o.CellsSnapshot) { s.MapSize = nil }, "width absent": func(s *o.CellsSnapshot) { s.MapSize.Width = nil }, "zero": func(s *o.CellsSnapshot) { s.MapSize.Width = proto.Uint32(0) }, "overflow": func(s *o.CellsSnapshot) { s.MapSize.Height = proto.Uint32(math.MaxInt32 + 1) },
		"anchor outside": func(s *o.CellsSnapshot) { s.MapSize.Width = proto.Uint32(2) }, "stale": func(s *o.CellsSnapshot) { s.Context.Identity.LoadToken = proto.String("other") }, "missing context": func(s *o.CellsSnapshot) { s.Context = nil },
		"cell omitted": func(s *o.CellsSnapshot) { s.Cells = nil }, "wrong cell": func(s *o.CellsSnapshot) { s.Cells[0].Cell.X = proto.Int32(4) }, "missing coordinate": func(s *o.CellsSnapshot) { s.Cells[0].Cell.Z = nil }, "duplicate row": func(s *o.CellsSnapshot) { s.Cells = append(s.Cells, s.Cells[0]) }, "unrequested fact": func(s *o.CellsSnapshot) { s.Cells[0].Fogged = proto.Bool(false) },
		"missing applied": func(s *o.CellsSnapshot) { s.AppliedFields = nil }, "unspecified applied": func(s *o.CellsSnapshot) { s.AppliedFields.Roof = nil }, "different applied": func(s *o.CellsSnapshot) { s.AppliedFields.Roof = proto.Bool(true) },
		"incomplete": func(s *o.CellsSnapshot) { s.Completeness.Page.Complete = proto.Bool(false) }, "unknown complete": func(s *o.CellsSnapshot) { s.Completeness.Page.Complete = nil }, "unreadable": func(s *o.CellsSnapshot) { s.Completeness.Unreadable = proto.Uint64(1) }, "filtered": func(s *o.CellsSnapshot) { s.Completeness.Filtered = proto.Uint64(1) }, "wrong count": func(s *o.CellsSnapshot) { s.Completeness.Matched = proto.Uint64(2) }, "unknown count": func(s *o.CellsSnapshot) { s.Completeness.Returned = nil }, "cursor": func(s *o.CellsSnapshot) { s.Completeness.Page.NextCursor = proto.String("next") },
		"region": func(s *o.CellsSnapshot) {
			s.Region = &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(4), Z: proto.Int32(0)}, Maximum: &c.Cell{X: proto.Int32(19), Z: proto.Int32(29)}}
		},
		"stale map snapshot": func(s *o.CellsSnapshot) {
			s.MapSnapshot = &o.SnapshotRef{Context: pbContext(), EntityId: proto.String("map"), Token: proto.String("token")}
			s.MapSnapshot.Context.Tick = proto.Int64(1)
		},
	} {
		t.Run(name, func(t *testing.T) {
			s := boundsSnapshot()
			edit(s)
			value, err := validateMapBounds(s, pbIdentity(), &c.Cell{X: proto.Int32(2), Z: proto.Int32(3)})
			if !errors.Is(err, ErrContract) || value.Context != nil || value.Bounds.Width != 0 {
				t.Fatal(value, err)
			}
		})
	}
}
func TestMapBoundsRegionOptionalAndWithinObservedMap(t *testing.T) {
	s := boundsSnapshot()
	s.MapSize.Width = proto.Uint32(math.MaxInt32)
	s.Region = &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(0), Z: proto.Int32(0)}, Maximum: &c.Cell{X: proto.Int32(math.MaxInt32 - 1), Z: proto.Int32(29)}}
	result, err := validateMapBounds(s, pbIdentity(), &c.Cell{X: proto.Int32(2), Z: proto.Int32(3)})
	if err != nil || result.Bounds.Width != math.MaxInt32 {
		t.Fatal(result, err)
	}
	s.Context.Identity.LoadToken = proto.String("later")
	if result.Context.Identity.GetLoadToken() != "load" {
		t.Fatal("aliased context")
	}
}
func TestReadMapBoundsTypedFailures(t *testing.T) {
	for _, refused := range []bool{false, true} {
		t.Run(map[bool]string{false: "unavailable", true: "refused"}[refused], func(t *testing.T) {
			client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				if refused {
					r := pbResult(&o.GetCellsReply{Outcome: &o.GetCellsReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_STALE_IDENTITY.Enum()}}})
					r.IsError = true
					return r, nil
				}
				return pbResult(&o.GetCellsReply{Outcome: &o.GetCellsReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_LIMIT_EXCEEDED.Enum()}}}), nil
			}}, time.Second)
			result, raw, err := client.ReadMapBounds(context.Background(), pbIdentity(), domain.Cell{X: 2, Z: 3})
			expected := ErrUnavailable
			if refused {
				expected = ErrRefused
			}
			if !errors.Is(err, expected) || result.Context != nil || len(raw.Envelope) == 0 {
				t.Fatal(result, err)
			}
		})
	}
}
func TestReadMapBoundsInvalidAnchorNeverCalls(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		t.Fatal("invalid bounds request called native")
		return nil, nil
	}}, time.Second)
	if _, _, err := client.ReadMapBounds(context.Background(), pbIdentity(), domain.Cell{X: -1}); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
	identity := pbIdentity()
	identity.MapId = nil
	if _, _, err := client.ReadMapBounds(context.Background(), identity, domain.Cell{}); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
}
