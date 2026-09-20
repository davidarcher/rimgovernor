package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func TestPlanningWindowRectClipsToMap(t *testing.T) {
	bounds := policy.Bounds{Width: 100, Height: 60}
	if got := PlanningWindowRect(domain.Cell{X: 50, Z: 30}, bounds); got != (policy.Rectangle{X: 28, Z: 8, Width: 45, Height: 45}) {
		t.Fatal(got)
	}
	if got := PlanningWindowRect(domain.Cell{X: 3, Z: 55}, bounds); got != (policy.Rectangle{X: 0, Z: 33, Width: 26, Height: 27}) {
		t.Fatal(got)
	}
}

func TestPlanningCellsPollutionAndGlowPresence(t *testing.T) {
	s := &o.CellsSnapshot{Cells: []*o.CellState{
		{Cell: &c.Cell{X: proto.Int32(1), Z: proto.Int32(1)}, Polluted: proto.Bool(false), Glow: proto.Float64(0)},
		{Cell: &c.Cell{X: proto.Int32(2), Z: proto.Int32(1)}, Polluted: proto.Bool(true), Glow: proto.Float64(.5)},
		{Cell: &c.Cell{X: proto.Int32(3), Z: proto.Int32(1)}},
	}}
	rows, _ := PlanningCells(s)
	if rows[0].Polluted != domain.Known(false) || rows[0].Glow != domain.Known(0.0) || rows[1].Polluted != domain.Known(true) || rows[1].Glow != domain.Known(.5) {
		t.Fatal(rows)
	}
	if _, known := rows[2].Polluted.Value(); known {
		t.Fatal("missing pollution became clean soil")
	}
	if _, known := rows[2].Glow.Value(); known {
		t.Fatal("missing glow became darkness")
	}
}

// windowSnapshot answers a planning window band request the way the native
// get_cells serves it: one row per cell of the band, fogged rows filtered
// into the completeness count.
func windowSnapshot(request *o.GetCellsRequest, fogged func(x, z int32) bool) *o.CellsSnapshot {
	rect := request.GetRectangle()
	s := &o.CellsSnapshot{Context: pbContext(), MapSize: &o.MapSize{Width: proto.Uint32(1000), Height: proto.Uint32(1000)}, Region: proto.Clone(rect).(*o.Rectangle), AppliedFields: planningWindowFields()}
	filtered := uint64(0)
	for z := rect.Minimum.GetZ(); z <= rect.Maximum.GetZ(); z++ {
		for x := rect.Minimum.GetX(); x <= rect.Maximum.GetX(); x++ {
			if fogged != nil && fogged(x, z) {
				filtered++
				continue
			}
			row := &o.CellState{Cell: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}, Walkable: proto.Bool(true), Occupied: proto.Bool(false), SupportsLight: proto.Bool(true), Indoors: proto.Bool(false), Doorway: proto.Bool(false), StorageEmpty: proto.Bool(true)}
			if x%2 == 0 {
				row.Fertility = proto.Float64(1)
			}
			if z == rect.Minimum.GetZ() {
				row.Roof = proto.String("RoofConstructed")
				row.ZoneId = proto.String("7")
			}
			s.Cells = append(s.Cells, row)
		}
	}
	s.Completeness = &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(uint64(len(s.Cells))), Returned: proto.Uint64(uint64(len(s.Cells))), Filtered: proto.Uint64(filtered), Unreadable: proto.Uint64(0)}
	return s
}

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

func TestReadPlanningWindowDecodesOnePageWindow(t *testing.T) {
	calls := 0
	rect := policy.Rectangle{X: 10, Z: 20, Width: 4, Height: 3}
	server := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		calls++
		request := decodeCellsRequest(t, arg)
		want := &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(10), Z: proto.Int32(20)}, Maximum: &c.Cell{X: proto.Int32(13), Z: proto.Int32(22)}}
		if !proto.Equal(request.Scope.ExpectedIdentity, pbIdentity()) || !proto.Equal(request.GetRectangle(), want) || request.Page.GetLimit() != 12 || !proto.Equal(request.Fields, planningWindowFields()) || request.ChangedSinceTick != nil {
			t.Fatal("query differs", request)
		}
		return pbResult(&o.GetCellsReply{Outcome: &o.GetCellsReply_Observed{Observed: windowSnapshot(request, func(x, z int32) bool { return x == 13 && z == 22 })}}), nil
	}}
	client := testClient(t, server, testBudget)
	window, raw, err := client.ReadPlanningWindow(context.Background(), pbIdentity(), rect, 0)
	if err != nil || calls != 1 || len(raw.Envelope) == 0 || window.Region != rect || window.Filtered != 1 || len(window.Cells) != 11 || window.Context.GetTick() != pbContext().GetTick() || window.Delta || window.Unchanged != 0 || len(window.Fogged) != 0 {
		t.Fatalf("%+v %v calls=%d", window, err, calls)
	}
	first := window.Cells[0]
	roofed, _ := first.Roofed.Value()
	zone, _ := first.Zone.Value()
	fertility, fertilityKnown := first.Fertility.Value()
	if first.Cell != (domain.Cell{X: 10, Z: 20}) || !roofed || !zone || !fertilityKnown || fertility != 1 {
		t.Fatalf("first row %+v", first)
	}
	// An absent roof or zone under the applied field is a known absence, and
	// fertility is unknown where the emitter left it out.
	last := window.Cells[len(window.Cells)-1]
	roofed, roofedKnown := last.Roofed.Value()
	zone, zoneKnown := last.Zone.Value()
	if last.Cell != (domain.Cell{X: 12, Z: 22}) || !roofedKnown || roofed || !zoneKnown || zone {
		t.Fatalf("last row %+v", last)
	}
	if _, known := window.Cells[1].Fertility.Value(); known {
		t.Fatal("fertility known where the emitter omitted it")
	}
}

func TestReadPlanningWindowBandsARectBeyondOnePage(t *testing.T) {
	var bands []*o.Rectangle
	server := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		request := decodeCellsRequest(t, arg)
		bands = append(bands, request.GetRectangle())
		s := windowSnapshot(request, nil)
		s.Cells = nil
		s.Compact = &o.CompactCells{Glow: []float64{0}}
		for z := request.GetRectangle().Minimum.GetZ(); z <= request.GetRectangle().Maximum.GetZ(); z++ {
			s.Compact.Rows = append(s.Compact.Rows, make([]byte, 3000))
		}
		return pbResult(&o.GetCellsReply{Outcome: &o.GetCellsReply_Observed{Observed: s}}), nil
	}}
	client := testClient(t, server, testBudget)
	rect := policy.Rectangle{X: 0, Z: 0, Width: 1000, Height: 90}
	window, _, err := client.ReadPlanningWindow(context.Background(), pbIdentity(), rect, 0)
	if err != nil || len(bands) != 2 || len(window.Cells) != 90000 || window.Region != rect {
		t.Fatalf("%v bands=%d cells=%d", err, len(bands), len(window.Cells))
	}
	if bands[0].Maximum.GetZ() != 64 || bands[1].Minimum.GetZ() != 65 || bands[1].Maximum.GetZ() != 89 {
		t.Fatal(bands)
	}
	if window.Cells[1000].Cell != (domain.Cell{X: 0, Z: 1}) || window.Cells[89999].Cell != (domain.Cell{X: 999, Z: 89}) {
		t.Fatal("rows out of order")
	}
}

func TestReadPlanningWindowRefusalsAndContractFaults(t *testing.T) {
	rect := policy.Rectangle{X: 10, Z: 20, Width: 4, Height: 3}
	for name, tc := range map[string]struct {
		reply func(*o.GetCellsRequest) *o.GetCellsReply
		want  error
	}{
		"unsupported fields (older native)": {func(*o.GetCellsRequest) *o.GetCellsReply {
			return &o.GetCellsReply{Outcome: &o.GetCellsReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_UNSUPPORTED.Enum(), Detail: proto.String("Only terrain, roof, visibility, traversal and things cell fields are implemented.")}}}
		}, ErrRefused},
		"unavailable": {func(*o.GetCellsRequest) *o.GetCellsReply {
			return &o.GetCellsReply{Outcome: &o.GetCellsReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_LIMIT_EXCEEDED.Enum()}}}
		}, ErrUnavailable},
		"region differs": {func(r *o.GetCellsRequest) *o.GetCellsReply {
			s := windowSnapshot(r, nil)
			s.Region.Maximum.X = proto.Int32(12)
			s.Cells = s.Cells[:9]
			s.Completeness.Matched, s.Completeness.Returned = proto.Uint64(9), proto.Uint64(9)
			return &o.GetCellsReply{Outcome: &o.GetCellsReply_Observed{Observed: s}}
		}, ErrContract},
		"applied fields differ": {func(r *o.GetCellsRequest) *o.GetCellsReply {
			s := windowSnapshot(r, nil)
			s.AppliedFields.Zone = proto.Bool(false)
			return &o.GetCellsReply{Outcome: &o.GetCellsReply_Observed{Observed: s}}
		}, ErrContract},
		"coverage short": {func(r *o.GetCellsRequest) *o.GetCellsReply {
			s := windowSnapshot(r, nil)
			s.Cells = s.Cells[:11]
			s.Completeness.Matched, s.Completeness.Returned = proto.Uint64(11), proto.Uint64(11)
			return &o.GetCellsReply{Outcome: &o.GetCellsReply_Observed{Observed: s}}
		}, ErrContract},
		"unrequested detail": {func(r *o.GetCellsRequest) *o.GetCellsReply {
			s := windowSnapshot(r, nil)
			s.Cells[0].Things = []*o.CellThing{{}}
			return &o.GetCellsReply{Outcome: &o.GetCellsReply_Observed{Observed: s}}
		}, ErrContract},
		"other identity": {func(r *o.GetCellsRequest) *o.GetCellsReply {
			s := windowSnapshot(r, nil)
			s.Context.Identity.LoadToken = proto.String("other")
			return &o.GetCellsReply{Outcome: &o.GetCellsReply_Observed{Observed: s}}
		}, ErrContract},
	} {
		t.Run(name, func(t *testing.T) {
			server := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(tc.reply(decodeCellsRequest(t, arg))), nil
			}}
			client := testClient(t, server, testBudget)
			window, _, err := client.ReadPlanningWindow(context.Background(), pbIdentity(), rect, 0)
			if !errors.Is(err, tc.want) || window.Cells != nil {
				t.Fatalf("%+v %v", window, err)
			}
		})
	}
}

// deltaSnapshot answers a changed_since ask the way a native with the
// per-cell change grid does (#357): only the rows changed at or after the
// tick, the rest counted as unchanged, as_of_tick stamped.
func deltaSnapshot(request *o.GetCellsRequest, changed func(x, z int32) bool, fogged func(x, z int32) bool) *o.CellsSnapshot {
	s := windowSnapshot(request, fogged)
	kept := s.Cells[:0]
	unchanged := uint32(0)
	for _, row := range s.Cells {
		if changed(row.Cell.GetX(), row.Cell.GetZ()) {
			kept = append(kept, row)
		} else {
			unchanged++
		}
	}
	s.Cells = kept
	s.Completeness.Matched, s.Completeness.Returned = proto.Uint64(uint64(len(kept))), proto.Uint64(uint64(len(kept)))
	s.Unchanged, s.AsOfTick = proto.Uint32(unchanged), proto.Int64(pbContext().GetTick())
	return s
}

func TestReadPlanningWindowDelta(t *testing.T) {
	rect := policy.Rectangle{X: 10, Z: 20, Width: 4, Height: 3}
	var request *o.GetCellsRequest
	server := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		request = decodeCellsRequest(t, arg)
		s := deltaSnapshot(request, func(x, z int32) bool { return z == 21 }, nil)
		// A cell fogged since the ask is listed as a fogged row.
		s.Cells[3] = &o.CellState{Cell: s.Cells[3].Cell, Fogged: proto.Bool(true)}
		return pbResult(&o.GetCellsReply{Outcome: &o.GetCellsReply_Observed{Observed: s}}), nil
	}}
	client := testClient(t, server, testBudget)
	window, _, err := client.ReadPlanningWindow(context.Background(), pbIdentity(), rect, 500)
	if err != nil || request.GetChangedSinceTick() != 500 || !window.Delta || window.Unchanged != 8 || len(window.Cells) != 3 || window.Filtered != 1 || len(window.Fogged) != 1 || window.Fogged[0] != (domain.Cell{X: 13, Z: 21}) {
		t.Fatalf("%+v %v", window, err)
	}
	// A native without the grid answers the ask with a full read.
	server.handler = func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(&o.GetCellsReply{Outcome: &o.GetCellsReply_Observed{Observed: windowSnapshot(decodeCellsRequest(t, arg), nil)}}), nil
	}
	if window, _, err = client.ReadPlanningWindow(context.Background(), pbIdentity(), rect, 500); err != nil || window.Delta || window.Unchanged != 0 || len(window.Cells) != 12 {
		t.Fatalf("%+v %v", window, err)
	}
	for name, tc := range map[string]struct {
		since int64
		reply func(*o.GetCellsRequest) *o.CellsSnapshot
	}{
		"unchanged without an ask": {0, func(r *o.GetCellsRequest) *o.CellsSnapshot {
			return deltaSnapshot(r, func(x, z int32) bool { return z == 21 }, nil)
		}},
		"unchanged without as_of_tick": {500, func(r *o.GetCellsRequest) *o.CellsSnapshot {
			s := deltaSnapshot(r, func(x, z int32) bool { return z == 21 }, nil)
			s.AsOfTick = nil
			return s
		}},
		"as_of_tick off the context": {500, func(r *o.GetCellsRequest) *o.CellsSnapshot {
			s := deltaSnapshot(r, func(x, z int32) bool { return z == 21 }, nil)
			s.AsOfTick = proto.Int64(pbContext().GetTick() - 1)
			return s
		}},
		"coverage short": {500, func(r *o.GetCellsRequest) *o.CellsSnapshot {
			s := deltaSnapshot(r, func(x, z int32) bool { return z == 21 }, nil)
			s.Unchanged = proto.Uint32(7)
			return s
		}},
	} {
		t.Run(name, func(t *testing.T) {
			server.handler = func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(&o.GetCellsReply{Outcome: &o.GetCellsReply_Observed{Observed: tc.reply(decodeCellsRequest(t, arg))}}), nil
			}
			if _, _, err := client.ReadPlanningWindow(context.Background(), pbIdentity(), rect, tc.since); !errors.Is(err, ErrContract) {
				t.Fatal(err)
			}
		})
	}
	if _, _, err := client.ReadPlanningWindow(context.Background(), pbIdentity(), rect, -1); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
}

func TestPlanningCellsSkipsFoggedRowsAndCountsThem(t *testing.T) {
	s := windowSnapshot(&o.GetCellsRequest{Selection: &o.GetCellsRequest_Rectangle{Rectangle: &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(0), Z: proto.Int32(0)}, Maximum: &c.Cell{X: proto.Int32(1), Z: proto.Int32(0)}}}}, nil)
	s.Cells[1].Fogged = proto.Bool(true)
	cells, filtered := PlanningCells(s)
	if len(cells) != 1 || filtered != 1 || cells[0].Cell != (domain.Cell{X: 0, Z: 0}) {
		t.Fatal(cells, filtered)
	}
	// A row whose visibility the emitter did not report is listed: the
	// emitter filters fogged cells, so a listed cell is visible.
	s.Cells[1].Fogged = nil
	if cells, filtered = PlanningCells(s); len(cells) != 2 || filtered != 0 {
		t.Fatal(cells, filtered)
	}
}
