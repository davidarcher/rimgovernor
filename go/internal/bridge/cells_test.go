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
			cell := policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), PlayerEdifice: domain.Known(""), ClaimableRuin: domain.Known("")}
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
		if !proto.Equal(request.Scope.ExpectedIdentity, pbIdentity()) || requestRect(request) != (policy.Rectangle{X: 2, Z: 3, Width: 1, Height: 1}) || request.Foundation != nil || request.Things != nil {
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
	thing := func() *o.Thing {
		return &o.Thing{Thing: &o.EntityRef{Id: proto.String("Plant_1"), Position: &c.Cell{X: proto.Int32(2), Z: proto.Int32(3)}}}
	}
	for name, edit := range map[string]func(*o.CellsSnapshot){
		"map absent": func(s *o.CellsSnapshot) { s.MapSize = nil }, "zero": func(s *o.CellsSnapshot) { s.MapSize.Width = proto.Uint32(0) }, "overflow": func(s *o.CellsSnapshot) { s.MapSize.Height = proto.Uint32(math.MaxInt32 + 1) },
		"rect off the map": func(s *o.CellsSnapshot) { s.MapSize.Width = proto.Uint32(3) }, "stale": func(s *o.CellsSnapshot) { s.Context.Identity.LoadToken = proto.String("other") }, "missing context": func(s *o.CellsSnapshot) { s.Context = nil },
		"no grid": func(s *o.CellsSnapshot) { s.Grid = nil }, "delta": func(s *o.CellsSnapshot) { s.Grid.Glow = nil }, "other rect": func(s *o.CellsSnapshot) { s.Grid.Rect.X = proto.Int32(1) },
		"unrequested foundation": func(s *o.CellsSnapshot) { s.Foundation = make([]byte, 4) },
		"unrequested things":     func(s *o.CellsSnapshot) { s.Things = []*o.Thing{thing()} },
	} {
		t.Run(name, func(t *testing.T) {
			s := cellsSnapshot(t, rect, nil, nil)
			edit(s)
			if _, err := validateCells(s, pbIdentity(), rect, false, false); !errors.Is(err, ErrContract) && err == nil {
				t.Fatal("accepted")
			}
		})
	}
	for name, edit := range map[string]func(*o.CellsSnapshot){
		"short foundation": func(s *o.CellsSnapshot) { s.Foundation = s.Foundation[1:] },
		"thing off rect":   func(s *o.CellsSnapshot) { s.Things[0].Thing.Position.X = proto.Int32(9) },
		"thing unplaced":   func(s *o.CellsSnapshot) { s.Things[0].Thing.Position = nil },
		"duplicate thing":  func(s *o.CellsSnapshot) { s.Things = append(s.Things, thing()) },
	} {
		t.Run(name, func(t *testing.T) {
			s := cellsSnapshot(t, rect, nil, nil)
			s.Foundation, s.Things = make([]byte, 4), []*o.Thing{thing()}
			if _, err := validateCells(s, pbIdentity(), rect, true, true); err != nil {
				t.Fatal("valid reply refused", err)
			}
			edit(s)
			if _, err := validateCells(s, pbIdentity(), rect, true, true); err == nil {
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

// TestReadMapSurveyDecodesFoundation reads a 20x30 map in bands: the
// grid's planning fields and the foundation byte make each survey cell.
func TestReadMapSurveyDecodesFoundation(t *testing.T) {
	var rects []policy.Rectangle
	server := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		request := decodeCellsRequest(t, arg)
		rect := requestRect(request)
		rects = append(rects, rect)
		if !request.GetFoundation() || request.Things != nil {
			t.Fatal("query differs", request)
		}
		s := cellsSnapshot(t, rect, func(c domain.Cell) bool { return c == (domain.Cell{X: 5, Z: 1}) }, func(cell *policy.SiteCell) {
			switch cell.Cell {
			case domain.Cell{X: 1, Z: 1}:
				cell.NaturalRock, cell.Occupied, cell.Walkable, cell.Roof = domain.Known(true), domain.Known(true), domain.Known(false), domain.Known(thickRoof)
			case domain.Cell{X: 2, Z: 1}:
				cell.Occupied, cell.Ruin = domain.Known(true), domain.Known(false)
			case domain.Cell{X: 3, Z: 1}:
				cell.Fertility = domain.Known(1.4)
			}
		})
		s.Foundation = make([]byte, rect.Width*rect.Height)
		for j := range s.Foundation {
			s.Foundation[j] = foundationHeavy | foundationLight
		}
		at := func(x, z int32) *byte { return &s.Foundation[int(z-rect.Z)*int(rect.Width)+int(x-rect.X)] }
		if rect.Z <= 1 && 1 < rect.Z+rect.Height {
			*at(1, 1) = foundationHeavy | foundationOre
			*at(3, 1) = foundationLight | foundationBridgeable | foundationDries | foundationTree
			*at(4, 1) = foundationBridgeable
			*at(6, 1) = foundationHeavy | foundationHazard
			*at(5, 1) = 0
		}
		return pbResult(&o.GetCellsReply{Outcome: &o.GetCellsReply_Observed{Observed: s}}), nil
	}}
	client := testClient(t, server, testBudget)
	survey, _, err := client.ReadMapSurvey(context.Background(), pbIdentity(), policy.Bounds{Width: 20, Height: 30})
	if err != nil || len(survey.Cells) != 20*30 || len(rects) != 1 {
		t.Fatal(err, len(survey.Cells), rects)
	}
	by := map[domain.Cell]policy.SurveyCell{}
	for _, cell := range survey.Cells {
		by[cell.Cell] = cell
	}
	if got := by[domain.Cell{X: 1, Z: 1}]; !got.Rock || !got.Ore || !got.ThickRoof || got.Walkable || got.Prop || got.Footing != policy.FootingFirm {
		t.Fatalf("rock %+v", got)
	}
	if got := by[domain.Cell{X: 2, Z: 1}]; !got.Prop || !got.Walkable {
		t.Fatalf("prop %+v", got)
	}
	if got := by[domain.Cell{X: 3, Z: 1}]; got.Footing != policy.FootingLight || !got.Bridgeable || !got.Dries || !got.Tree || got.Fertility != 1.4 {
		t.Fatalf("marsh %+v", got)
	}
	if got := by[domain.Cell{X: 4, Z: 1}]; got.Footing != policy.FootingNone || !got.Bridgeable {
		t.Fatalf("water %+v", got)
	}
	if got := by[domain.Cell{X: 6, Z: 1}]; !got.Hazard || by[domain.Cell{X: 4, Z: 1}].Hazard {
		t.Fatalf("hazard bit %+v", got)
	}
	if got := by[domain.Cell{X: 5, Z: 1}]; !got.Rock || got.Walkable || got.Ore {
		t.Fatalf("fogged cell reads as plain rock %+v", got)
	}
}

// TestReadBareCellsKeysOnPlantGrowth is #1567: a cell is bare unless a
// thing row standing on it carries growth (the native plant row), and a
// fogged cell is never bare.
func TestReadBareCellsKeysOnPlantGrowth(t *testing.T) {
	want := []domain.Cell{{X: 2, Z: 3}, {X: 3, Z: 3}, {X: 4, Z: 5}}
	server := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		request := decodeCellsRequest(t, arg)
		rect := requestRect(request)
		if rect != (policy.Rectangle{X: 2, Z: 3, Width: 3, Height: 3}) || !request.GetThings() || request.Foundation != nil {
			t.Fatal("query differs", request)
		}
		s := cellsSnapshot(t, rect, func(c domain.Cell) bool { return c == (domain.Cell{X: 3, Z: 3}) }, nil)
		at := func(id string, x, z int32) *o.EntityRef {
			return &o.EntityRef{Id: proto.String(id), Position: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}}
		}
		s.Things = []*o.Thing{{Thing: at("Plant_1", 2, 3), Growth: proto.Float64(0.4)}, {Thing: at("Steel_2", 4, 5), StackCount: proto.Int64(5)}}
		return pbResult(&o.GetCellsReply{Outcome: &o.GetCellsReply_Observed{Observed: s}}), nil
	}}
	client := testClient(t, server, testBudget)
	bare, _, err := client.ReadBareCells(context.Background(), pbIdentity(), want)
	if err != nil || len(bare) != 1 || !bare[domain.Cell{X: 4, Z: 5}] {
		t.Fatal(bare, err)
	}
}
