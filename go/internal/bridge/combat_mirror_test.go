package bridge

import (
	"fmt"
	"math"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	"google.golang.org/protobuf/proto"
)

func combatWM(tick int64, seq uint32) *mp.Watermark {
	return &mp.Watermark{Tick: proto.Int64(tick), Seq: proto.Uint32(seq)}
}

// The combat sections' pages (#851): pawn rows need an id, side and cell
// and sane numbers; event rows a watermark and kind, inside their delta;
// events are never tombstoned.
func TestCombatMirrorPageValidation(t *testing.T) {
	id := pbIdentity()
	epoch := &mp.Epoch{Process: proto.String("p"), Identity: id}
	pawns, events := mp.Section_SECTION_COMBAT_PAWNS.Enum(), mp.Section_SECTION_COMBAT_EVENTS.Enum()
	request := &mp.MirrorPollRequest{Identity: id, Epoch: epoch, ByteBudget: proto.Uint32(MirrorPollMaxBytes), Asks: []*mp.SectionAsk{
		{Section: pawns, Since: combatWM(10, 1)}, {Section: events, Since: combatWM(10, 1)},
	}}
	if err := validateMirrorPollRequest(request); err != nil {
		t.Fatalf("combat asks refused: %v", err)
	}
	pawn := func() *mp.CombatPawn {
		return &mp.CombatPawn{Id: proto.String("Thing_Human1"), Side: mp.CombatSide_COMBAT_SIDE_HOSTILE.Enum(), Cell: &c.Cell{X: proto.Int32(3), Z: proto.Int32(4)}, Health: proto.Float64(0.5)}
	}
	event := func(tick int64, seq uint32) *mp.CombatEventRow {
		return &mp.CombatEventRow{At: combatWM(tick, seq), Kind: mp.CombatLogKind_COMBAT_LOG_KIND_SHOT_FIRED.Enum()}
	}
	page := func(pawnDelta *mp.Delta, eventDelta *mp.Delta) *mp.MirrorPage {
		return &mp.MirrorPage{Epoch: epoch, CompleteThroughTick: proto.Int64(12), Sections: []*mp.SectionPage{
			{Section: pawns, Body: &mp.SectionPage_Delta{Delta: pawnDelta}},
			{Section: events, Body: &mp.SectionPage_Delta{Delta: eventDelta}},
		}}
	}
	delta := func() *mp.Delta { return &mp.Delta{From: combatWM(10, 1), To: combatWM(12, 5)} }
	good := func() (*mp.Delta, *mp.Delta) {
		p, e := delta(), delta()
		p.CombatPawns, p.Tombstones = []*mp.CombatPawn{pawn()}, []string{"Thing_Human2"}
		e.CombatEvents = []*mp.CombatEventRow{event(11, 1), event(12, 5)}
		return p, e
	}
	if err := ValidateMirrorPage(page(good()), request); err != nil {
		t.Fatalf("good page refused: %v", err)
	}
	cases := map[string]func(p, e *mp.Delta){
		"pawn without side":     func(p, e *mp.Delta) { p.CombatPawns[0].Side = nil },
		"pawn without cell":     func(p, e *mp.Delta) { p.CombatPawns[0].Cell = nil },
		"pawn health over one":  func(p, e *mp.Delta) { p.CombatPawns[0].Health = proto.Float64(1.5) },
		"pawn NaN pain":         func(p, e *mp.Delta) { p.CombatPawns[0].Pain = proto.Float64(math.NaN()) },
		"event before from":     func(p, e *mp.Delta) { e.CombatEvents[0].At = combatWM(10, 1) },
		"event after to":        func(p, e *mp.Delta) { e.CombatEvents[1].At = combatWM(12, 6) },
		"event without kind":    func(p, e *mp.Delta) { e.CombatEvents[0].Kind = nil },
		"event tombstone":       func(p, e *mp.Delta) { e.Tombstones = []string{"11.1"} },
		"pawn rows on events":   func(p, e *mp.Delta) { e.CombatPawns = []*mp.CombatPawn{pawn()} },
		"event rows on pawns":   func(p, e *mp.Delta) { p.CombatEvents = []*mp.CombatEventRow{event(11, 2)} },
		"building rows on pawn": func(p, e *mp.Delta) { p.Buildings = nil; p.Bills = nil; p.Cells = &mp.CellGrid{} },
	}
	for name, mutate := range cases {
		p, e := good()
		mutate(p, e)
		if ValidateMirrorPage(page(p, e), request) == nil {
			t.Errorf("%s: accepted", name)
		}
	}
	if got := CombatEventID(event(11, 2)); got != "11.2" {
		t.Fatalf("event id %q", got)
	}
}

// The geometry read's caps and shape (#851).
func TestCombatGeometryRequestCaps(t *testing.T) {
	id := pbIdentity()
	cells := func(n int) []*c.Cell {
		out := make([]*c.Cell, n)
		for i := range out {
			out[i] = &c.Cell{X: proto.Int32(int32(i)), Z: proto.Int32(1)}
		}
		return out
	}
	hostiles := func(n int) []string {
		out := make([]string, n)
		for i := range out {
			out[i] = fmt.Sprintf("Thing_Human%d", i)
		}
		return out
	}
	if err := ValidateCombatGeometryRequest(CombatGeometryAsk(id, cells(CombatGeometryMaxCells), hostiles(CombatGeometryMaxHostiles), "Thing_Human99")); err != nil {
		t.Fatalf("request at the caps refused: %v", err)
	}
	repeated := cells(2)
	repeated[1] = repeated[0]
	for name, request := range map[string]*mp.CombatGeometryRequest{
		"no cells":        CombatGeometryAsk(id, nil, hostiles(1), ""),
		"cells over cap":  CombatGeometryAsk(id, cells(CombatGeometryMaxCells+1), hostiles(1), ""),
		"no hostiles":     CombatGeometryAsk(id, cells(1), nil, ""),
		"hostiles over":   CombatGeometryAsk(id, cells(1), hostiles(CombatGeometryMaxHostiles+1), ""),
		"repeated cell":   CombatGeometryAsk(id, repeated, hostiles(1), ""),
		"repeated target": CombatGeometryAsk(id, cells(1), []string{"a", "a"}, ""),
		"bad pawn":        {Identity: id, Cells: cells(1), HostileIds: hostiles(1), PawnId: proto.String("")},
		"no identity":     {Cells: cells(1), HostileIds: hostiles(1)},
	} {
		if ValidateCombatGeometryRequest(request) == nil {
			t.Errorf("%s: accepted", name)
		}
	}
}

// A geometry reply must answer its request cell by cell, line by line.
func TestCombatGeometryReplyValidation(t *testing.T) {
	request := CombatGeometryAsk(pbIdentity(), []*c.Cell{{X: proto.Int32(1), Z: proto.Int32(2)}}, []string{"h1", "h2"}, "")
	reply := func() *mp.CombatGeometry {
		line := func(id string) *mp.CombatSightLine {
			return &mp.CombatSightLine{HostileId: proto.String(id), Cover: proto.Float64(0.4), LineOfFire: proto.Bool(true), ColonistInPath: proto.Bool(false)}
		}
		return &mp.CombatGeometry{Context: pbContext(), Cells: []*mp.CombatGeometryCell{{Cell: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}, Lines: []*mp.CombatSightLine{line("h1"), line("h2")}}}}
	}
	if err := ValidateCombatGeometry(reply(), request); err != nil {
		t.Fatalf("good reply refused: %v", err)
	}
	for name, mutate := range map[string]func(*mp.CombatGeometry){
		"cell off request": func(g *mp.CombatGeometry) { g.Cells[0].Cell.X = proto.Int32(9) },
		"lines reordered": func(g *mp.CombatGeometry) {
			g.Cells[0].Lines[0], g.Cells[0].Lines[1] = g.Cells[0].Lines[1], g.Cells[0].Lines[0]
		},
		"cover over one": func(g *mp.CombatGeometry) { g.Cells[0].Lines[0].Cover = proto.Float64(1.2) },
		"path sans pawn": func(g *mp.CombatGeometry) { g.Cells[0].PathTicks = proto.Int32(40) },
		"missing cell":   func(g *mp.CombatGeometry) { g.Cells = nil },
	} {
		g := reply()
		mutate(g)
		if ValidateCombatGeometry(g, request) == nil {
			t.Errorf("%s: accepted", name)
		}
	}
}
