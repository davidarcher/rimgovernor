package bridge

import (
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func compactFixture() *o.CellsSnapshot {
	return &o.CellsSnapshot{Context: pbContext(), MapSize: &o.MapSize{Width: proto.Uint32(250), Height: proto.Uint32(250)},
		Region:        &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(7), Z: proto.Int32(8)}, Maximum: &c.Cell{X: proto.Int32(9), Z: proto.Int32(8)}},
		AppliedFields: planningWindowFields(), AsOfTick: proto.Int64(pbContext().GetTick()), Unchanged: proto.Uint32(1),
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(2), Returned: proto.Uint64(2), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)},
		Compact:      &o.CompactCells{Rows: [][]byte{{0xfc, 0x7f, 0, 1, 2, 0, 2, 0, 1, 0}}, Strings: []string{"RoofConstructed", "7", "9"}, Glow: []float64{.123456789}, Fertility: []float64{1.23456789}}}
}

func TestCompactCellsPreservesFactsAndDelta(t *testing.T) {
	s := compactFixture()
	if err := ExpandCompactCells(s); err != nil {
		t.Fatal(err)
	}
	if err := validatePlanningCells(s, s.Context, s.MapSize, 1); err != nil {
		t.Fatal(err)
	}
	want := &o.CellState{Cell: &c.Cell{X: proto.Int32(7), Z: proto.Int32(8)}, Walkable: proto.Bool(true), Passable: proto.Bool(true), Occupied: proto.Bool(true), Doorway: proto.Bool(true), SupportsLight: proto.Bool(true), StorageEmpty: proto.Bool(true), Indoors: proto.Bool(true), Polluted: proto.Bool(true), NaturalRock: proto.Bool(true), Ruin: proto.Bool(false), Roof: proto.String("RoofConstructed"), ZoneId: proto.String("7"), RoomId: proto.String("9"), Glow: proto.Float64(.123456789), Fertility: proto.Float64(1.23456789)}
	if !proto.Equal(s.Cells[0], want) || !s.Cells[1].GetFogged() || s.Cells[1].Cell.GetX() != 8 || len(s.Cells) != 2 || s.Compact != nil {
		t.Fatal(s)
	}
	cells, filtered := PlanningCells(s)
	if len(cells) != 1 || filtered != 1 {
		t.Fatal(cells, filtered)
	}
}

// #709: bit 15 carries the edifice: 0 a ruin, else 1 + the player
// edifice definition's string index.
func TestCompactCellsEdifice(t *testing.T) {
	for edifice, want := range map[byte]*o.CellState{0: {Ruin: proto.Bool(true)}, 2: {Ruin: proto.Bool(false), PlayerEdifice: proto.String("7")}} {
		s := compactFixture()
		s.Compact.Rows[0] = []byte{0xfc, 0xff, 0, 1, 2, edifice, 0, 2, 0, 1, 0}
		if err := ExpandCompactCells(s); err != nil {
			t.Fatal(err)
		}
		got := s.Cells[0]
		if got.GetRuin() != want.GetRuin() || (got.PlayerEdifice == nil) != (want.PlayerEdifice == nil) || got.GetPlayerEdifice() != want.GetPlayerEdifice() || got.GetGlow() != .123456789 {
			t.Fatalf("edifice %d: %v", edifice, got)
		}
	}
}

func TestCompactCellsRejectsMalformedCoverage(t *testing.T) {
	for name, mutate := range map[string]func(*o.CellsSnapshot){
		"mixed":              func(s *o.CellsSnapshot) { s.Cells = []*o.CellState{{}} },
		"missing row":        func(s *o.CellsSnapshot) { s.Compact.Rows = nil },
		"truncated":          func(s *o.CellsSnapshot) { s.Compact.Rows[0] = []byte{4} },
		"bad edifice":        func(s *o.CellsSnapshot) { s.Compact.Rows[0] = []byte{0xfc, 0xff, 0, 1, 2, 4, 0, 2, 0, 1, 0} },
		"fog facts":          func(s *o.CellsSnapshot) { s.Compact.Rows[0][6] = 6 },
		"unchanged facts":    func(s *o.CellsSnapshot) { s.Compact.Rows[0][8] = 5 },
		"bad index":          func(s *o.CellsSnapshot) { s.Compact.Rows[0][2] = 3 },
		"bad varint":         func(s *o.CellsSnapshot) { s.Compact.Rows[0] = []byte{0, 8, 128} },
		"trailing bytes":     func(s *o.CellsSnapshot) { s.Compact.Rows[0] = append(s.Compact.Rows[0], 0) },
		"missing fertility":  func(s *o.CellsSnapshot) { s.Compact.Fertility = nil },
		"excess fertility":   func(s *o.CellsSnapshot) { s.Compact.Fertility = append(s.Compact.Fertility, 1) },
		"zero fertility":     func(s *o.CellsSnapshot) { s.Compact.Fertility[0] = 0 },
		"glow bounds":        func(s *o.CellsSnapshot) { s.Compact.Glow[0] = 2 },
		"unchanged mismatch": func(s *o.CellsSnapshot) { s.Unchanged = proto.Uint32(0) },
		"filtered":           func(s *o.CellsSnapshot) { s.Completeness.Filtered = proto.Uint64(1) },
		"fields":             func(s *o.CellsSnapshot) { s.AppliedFields.Growth = proto.Bool(false) },
	} {
		t.Run(name, func(t *testing.T) {
			s := compactFixture()
			mutate(s)
			if ExpandCompactCells(s) == nil {
				t.Fatal("accepted malformed compact cells")
			}
		})
	}
}
