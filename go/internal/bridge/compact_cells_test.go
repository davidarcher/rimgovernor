package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func compactFixture() *o.CellsSnapshot {
	return &o.CellsSnapshot{Context: pbContext(), MapSize: &o.MapSize{Width: proto.Uint32(250), Height: proto.Uint32(250)},
		Region:        &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(7), Z: proto.Int32(8)}, Maximum: &c.Cell{X: proto.Int32(9), Z: proto.Int32(8)}},
		AppliedFields: planningWindowFields(),
		Compact:       &o.CompactCells{Rows: [][]byte{{0xfc, 0x7f, 0, 1, 2, 0, 2, 0, 2, 0}}, Strings: []string{"RoofConstructed", "7", "9"}, Glow: []float64{.123456789}, Fertility: []float64{1.23456789}}}
}

func TestCompactCellsPreservesFacts(t *testing.T) {
	s := compactFixture()
	if err := ExpandCompactCells(s); err != nil {
		t.Fatal(err)
	}
	if err := validatePlanningCells(s, s.Context, s.MapSize); err != nil {
		t.Fatal(err)
	}
	want := &o.CellState{Cell: &c.Cell{X: proto.Int32(7), Z: proto.Int32(8)}, Walkable: proto.Bool(true), Passable: proto.Bool(true), Occupied: proto.Bool(true), Doorway: proto.Bool(true), SupportsLight: proto.Bool(true), StorageEmpty: proto.Bool(true), Indoors: proto.Bool(true), Polluted: proto.Bool(true), NaturalRock: proto.Bool(true), Ruin: proto.Bool(false), Roof: proto.String("RoofConstructed"), ZoneId: proto.String("7"), RoomId: proto.String("9"), Glow: proto.Float64(.123456789), Fertility: proto.Float64(1.23456789)}
	if !proto.Equal(s.Cells[0], want) || !s.Cells[1].GetFogged() || s.Cells[1].Cell.GetX() != 8 || len(s.Cells) != 3 || s.Compact != nil {
		t.Fatal(s)
	}
	cells, filtered := PlanningCells(s)
	if len(cells) != 1 || filtered != 2 {
		t.Fatal(cells, filtered)
	}
}

// #709, #718: bit 15 carries the edifice: 0 a ruin, odd the player
// edifice definition's string index, even a claimable ruin's.
func TestCompactCellsEdifice(t *testing.T) {
	for edifice, want := range map[byte]*o.CellState{0: {Ruin: proto.Bool(true)}, 3: {Ruin: proto.Bool(false), PlayerEdifice: proto.String("7")}, 4: {Ruin: proto.Bool(true), ClaimableRuin: proto.String("7")}} {
		s := compactFixture()
		s.Compact.Rows[0] = []byte{0xfc, 0xff, 0, 1, 2, edifice, 0, 2, 0, 2, 0}
		if err := ExpandCompactCells(s); err != nil {
			t.Fatal(err)
		}
		got := s.Cells[0]
		if got.GetRuin() != want.GetRuin() || (got.PlayerEdifice == nil) != (want.PlayerEdifice == nil) || got.GetPlayerEdifice() != want.GetPlayerEdifice() || (got.ClaimableRuin == nil) != (want.ClaimableRuin == nil) || got.GetClaimableRuin() != want.GetClaimableRuin() || got.GetGlow() != .123456789 {
			t.Fatalf("edifice %d: %v", edifice, got)
		}
	}
}

func TestCompactCellsRejectsMalformedCoverage(t *testing.T) {
	for name, mutate := range map[string]func(*o.CellsSnapshot){
		"mixed":             func(s *o.CellsSnapshot) { s.Cells = []*o.CellState{{}} },
		"missing row":       func(s *o.CellsSnapshot) { s.Compact.Rows = nil },
		"truncated":         func(s *o.CellsSnapshot) { s.Compact.Rows[0] = []byte{4} },
		"bad edifice":       func(s *o.CellsSnapshot) { s.Compact.Rows[0] = []byte{0xfc, 0xff, 0, 1, 2, 7, 0, 2, 0, 1, 0} },
		"fog facts":         func(s *o.CellsSnapshot) { s.Compact.Rows[0][6] = 6 },
		"unchanged cell":    func(s *o.CellsSnapshot) { s.Compact.Rows[0][8] = 1 },
		"bad index":         func(s *o.CellsSnapshot) { s.Compact.Rows[0][2] = 3 },
		"bad varint":        func(s *o.CellsSnapshot) { s.Compact.Rows[0] = []byte{0, 8, 128} },
		"trailing bytes":    func(s *o.CellsSnapshot) { s.Compact.Rows[0] = append(s.Compact.Rows[0], 0) },
		"missing fertility": func(s *o.CellsSnapshot) { s.Compact.Fertility = nil },
		"excess fertility":  func(s *o.CellsSnapshot) { s.Compact.Fertility = append(s.Compact.Fertility, 1) },
		"zero fertility":    func(s *o.CellsSnapshot) { s.Compact.Fertility[0] = 0 },
		"glow bounds":       func(s *o.CellsSnapshot) { s.Compact.Glow[0] = 2 },
		"fields":            func(s *o.CellsSnapshot) { s.AppliedFields.Growth = proto.Bool(false) },
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

// #727/#949: with foundation applied each visible cell ends with one byte:
// bit 0 supports_heavy, 1 resource_rock, 2 tree, 3 bridgeable, 4 dries. A
// cell without heavy support is soft; this row also supports light.
func TestCompactCellsFoundation(t *testing.T) {
	for _, b := range []byte{0, 1, 7, 24, 31} {
		s := compactFixture()
		s.AppliedFields = mapSurveyFields()
		s.Compact.Rows[0] = []byte{0xfc, 0x3f, 0, 1, 2, 0, b, 2, 0, 2, 0}
		s.Compact.Strings[0] = thickRoof
		if err := ExpandCompactCells(s); err != nil {
			t.Fatal(err)
		}
		if err := validatePlanningCells(s, s.Context, s.MapSize); err != nil {
			t.Fatal(err)
		}
		footing := policy.FootingLight
		if b&1 != 0 {
			footing = policy.FootingFirm
		}
		got := SurveyCells(s)
		if len(got) != 1 || got[0].Footing != footing || !got[0].Walkable || got[0].Rock || !got[0].ThickRoof || got[0].Fertility != 1.23456789 ||
			got[0].Ore != (b&2 != 0) || got[0].Tree != (b&4 != 0) || got[0].Bridgeable != (b&8 != 0) || got[0].Dries != (b&16 != 0) {
			t.Fatalf("foundation %d: %+v", b, got)
		}
	}
	s := compactFixture()
	s.AppliedFields = mapSurveyFields()
	s.Compact.Rows[0] = []byte{0xfc, 0x3f, 0, 1, 2, 0, 32, 2, 0, 1, 0}
	if ExpandCompactCells(s) == nil {
		t.Fatal("accepted a foundation byte past bit 4")
	}
	s = compactFixture()
	s.AppliedFields = mapSurveyFields()
	if ExpandCompactCells(s) == nil {
		t.Fatal("accepted a survey row without its foundation byte")
	}
}
