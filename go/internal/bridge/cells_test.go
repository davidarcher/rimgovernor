package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"math"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge/cellgrid"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func decodeCellsRequest(t *testing.T, arg nativeArgument) *o.GetCellsRequest {
	t.Helper()
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
	return request
}

// requestRect is a get_cells request's rectangle as a policy rect.
func requestRect(r *o.GetCellsRequest) policy.Rectangle {
	lo, hi := r.GetRectangle().GetMinimum(), r.GetRectangle().GetMaximum()
	return policy.Rectangle{X: lo.GetX(), Z: lo.GetZ(), Width: hi.GetX() - lo.GetX() + 1, Height: hi.GetZ() - lo.GetZ() + 1}
}

// cellsSnapshot is native's reply over rect of a 20x30 map: every cell
// held and walkable except the fogged ones, each cell's fields from site.
func cellsSnapshot(t *testing.T, rect policy.Rectangle, fogged func(domain.Cell) bool, site func(*policy.SiteCell)) *o.CellsSnapshot {
	t.Helper()
	cells := map[domain.Cell]policy.SiteCell{}
	for z := rect.Z; z < rect.Z+rect.Height; z++ {
		for x := rect.X; x < rect.X+rect.Width; x++ {
			cell := policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true)}
			if fogged != nil && fogged(cell.Cell) {
				continue
			}
			if site != nil {
				site(&cell)
			}
			cells[cell.Cell] = cell
		}
	}
	// A fixture never fogs a corner, so the held cells span rect.
	grid, err := cellgrid.FromCells(cells, cellgrid.MaxCells)
	if err != nil || grid.Rect != rect {
		t.Fatal(grid, err)
	}
	wire := grid.Wire(nil)
	return &o.CellsSnapshot{Context: pbContext(), MapSize: &o.MapSize{Width: proto.Uint32(20), Height: proto.Uint32(30)}, Grid: wire}
}

func TestReadMapBoundsReadsTheAnchorCell(t *testing.T) {
	calls := 0
	server := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		calls++
		request := decodeCellsRequest(t, arg)
		if !proto.Equal(request.Scope.ExpectedIdentity, pbIdentity()) || requestRect(request) != (policy.Rectangle{X: 2, Z: 3, Width: 1, Height: 1}) {
			t.Fatal("query differs", request)
		}
		return pbResult(&o.GetCellsReply{Outcome: &o.GetCellsReply_Observed{Observed: cellsSnapshot(t, requestRect(request), nil, nil)}}), nil
	}}
	client := testClient(t, server, testBudget)
	bounds, raw, err := client.ReadMapBounds(context.Background(), pbIdentity(), domain.Cell{X: 2, Z: 3})
	if err != nil || bounds.Context == nil || bounds.Bounds.Width != 20 || bounds.Bounds.Height != 30 || len(raw.Envelope) == 0 || calls != 1 {
		t.Fatalf("%v %v calls%d", bounds, err, calls)
	}
}

func TestCellsReplyMustAnswerTheRequest(t *testing.T) {
	rect := policy.Rectangle{X: 2, Z: 3, Width: 2, Height: 2}
	for name, edit := range map[string]func(*o.CellsSnapshot){
		"map absent": func(s *o.CellsSnapshot) { s.MapSize = nil }, "zero": func(s *o.CellsSnapshot) { s.MapSize.Width = proto.Uint32(0) }, "overflow": func(s *o.CellsSnapshot) { s.MapSize.Height = proto.Uint32(math.MaxInt32 + 1) },
		"rect off the map": func(s *o.CellsSnapshot) { s.MapSize.Width = proto.Uint32(3) }, "stale": func(s *o.CellsSnapshot) { s.Context.Identity.LoadToken = proto.String("other") }, "missing context": func(s *o.CellsSnapshot) { s.Context = nil },
		"no grid": func(s *o.CellsSnapshot) { s.Grid = nil }, "delta": func(s *o.CellsSnapshot) { s.Grid.Glow = nil }, "other rect": func(s *o.CellsSnapshot) { s.Grid.Rect.X = proto.Int32(1) },
	} {
		t.Run(name, func(t *testing.T) {
			s := cellsSnapshot(t, rect, nil, nil)
			edit(s)
			if _, err := validateCells(s, pbIdentity(), rect); !errors.Is(err, ErrContract) && err == nil {
				t.Fatal("accepted")
			}
		})
	}
}

func TestReadMapBoundsTypedFailures(t *testing.T) {
	for _, refused := range []bool{false, true} {
		t.Run(map[bool]string{false: "unavailable", true: "refused"}[refused], func(t *testing.T) {
			client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*callResult, error) {
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
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*callResult, error) {
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
