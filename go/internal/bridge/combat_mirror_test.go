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

// Each propose role (#871) validates its anchor; named plus proposed
// cells share the cells cap.
func TestCombatGeometryProposeRequest(t *testing.T) {
	id := pbIdentity()
	at := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	line := func(n int) []*c.Cell {
		out := make([]*c.Cell, n)
		for i := range out {
			out[i] = at(int32(i), 1)
		}
		return out
	}
	cover := func(cells []*c.Cell) *mp.CombatGeometryPropose {
		return &mp.CombatGeometryPropose{Role: &mp.CombatGeometryPropose_CoverBehindLine{CoverBehindLine: &mp.CombatCoverBehindLine{Line: cells}}}
	}
	choke := func(choke, side *c.Cell) *mp.CombatGeometryPropose {
		return &mp.CombatGeometryPropose{Role: &mp.CombatGeometryPropose_AdjacentToChoke{AdjacentToChoke: &mp.CombatAdjacentToChoke{Choke: choke, OurSide: side}}}
	}
	firing := func(targets []*c.Cell, from *c.Cell, radius *int32) *mp.CombatGeometryPropose {
		return &mp.CombatGeometryPropose{Role: &mp.CombatGeometryPropose_FiringCells{FiringCells: &mp.CombatFiringCells{Targets: targets, From: from, Radius: radius}}}
	}
	hostiles := []string{"Thing_Human1"}
	for name, p := range map[string]*mp.CombatGeometryPropose{
		"cover at cap":  cover(line(CombatGeometryMaxCells)),
		"choke":         choke(at(5, 5), at(5, 0)),
		"firing at cap": firing(line(CombatGeometryMaxHostiles), at(3, 3), proto.Int32(CombatGeometryMaxRadius)),
	} {
		if err := ValidateCombatGeometryRequest(CombatGeometryProposeAsk(id, p, hostiles, "")); err != nil {
			t.Errorf("%s refused: %v", name, err)
		}
	}
	withNamed := CombatGeometryAsk(id, line(CombatGeometryMaxCells-1), hostiles, "")
	withNamed.Propose = cover(line(1))
	if err := ValidateCombatGeometryRequest(withNamed); err != nil {
		t.Errorf("63 named cells plus a proposal refused: %v", err)
	}
	full := CombatGeometryAsk(id, line(CombatGeometryMaxCells), hostiles, "")
	full.Propose = cover(line(1))
	repeated := line(2)
	repeated[1] = repeated[0]
	for name, request := range map[string]*mp.CombatGeometryRequest{
		"no role":            CombatGeometryProposeAsk(id, &mp.CombatGeometryPropose{}, hostiles, ""),
		"empty line":         CombatGeometryProposeAsk(id, cover(nil), hostiles, ""),
		"line over cap":      CombatGeometryProposeAsk(id, cover(line(CombatGeometryMaxCells+1)), hostiles, ""),
		"repeated line cell": CombatGeometryProposeAsk(id, cover(repeated), hostiles, ""),
		"no choke":           CombatGeometryProposeAsk(id, choke(nil, at(1, 1)), hostiles, ""),
		"no side":            CombatGeometryProposeAsk(id, choke(at(1, 1), nil), hostiles, ""),
		"side on choke":      CombatGeometryProposeAsk(id, choke(at(1, 1), at(1, 1)), hostiles, ""),
		"no targets":         CombatGeometryProposeAsk(id, firing(nil, at(1, 1), proto.Int32(4)), hostiles, ""),
		"targets over":       CombatGeometryProposeAsk(id, firing(line(CombatGeometryMaxHostiles+1), at(1, 1), proto.Int32(4)), hostiles, ""),
		"no from":            CombatGeometryProposeAsk(id, firing(line(1), nil, proto.Int32(4)), hostiles, ""),
		"no radius":          CombatGeometryProposeAsk(id, firing(line(1), at(1, 1), nil), hostiles, ""),
		"radius over":        CombatGeometryProposeAsk(id, firing(line(1), at(1, 1), proto.Int32(CombatGeometryMaxRadius+1)), hostiles, ""),
		"no hostiles":        CombatGeometryProposeAsk(id, cover(line(1)), nil, ""),
		"no room":            full,
	} {
		if ValidateCombatGeometryRequest(request) == nil {
			t.Errorf("%s: accepted", name)
		}
	}
}

// Proposals are standable, distinct from the named cells, within the cap,
// and scored like named cells; a request without propose gets none.
func TestCombatGeometryProposedReply(t *testing.T) {
	at := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	propose := &mp.CombatGeometryPropose{Role: &mp.CombatGeometryPropose_CoverBehindLine{CoverBehindLine: &mp.CombatCoverBehindLine{Line: []*c.Cell{at(5, 5)}}}}
	request := CombatGeometryAsk(pbIdentity(), []*c.Cell{at(1, 2)}, []string{"h1"}, "")
	request.Propose = propose
	row := func(cell *c.Cell) *mp.CombatGeometryCell {
		return &mp.CombatGeometryCell{Cell: cell, Standable: proto.Bool(true), Lines: []*mp.CombatSightLine{{HostileId: proto.String("h1"), Cover: proto.Float64(0.5)}}}
	}
	reply := func() *mp.CombatGeometry {
		return &mp.CombatGeometry{Context: pbContext(), Cells: []*mp.CombatGeometryCell{row(at(1, 2))}, Proposed: []*mp.CombatGeometryCell{row(at(5, 4)), row(at(4, 4))}}
	}
	if err := ValidateCombatGeometry(reply(), request); err != nil {
		t.Fatalf("good reply refused: %v", err)
	}
	for name, mutate := range map[string]func(*mp.CombatGeometry){
		"not standable":  func(g *mp.CombatGeometry) { g.Proposed[0].Standable = proto.Bool(false) },
		"repeats named":  func(g *mp.CombatGeometry) { g.Proposed[0].Cell = at(1, 2) },
		"repeats itself": func(g *mp.CombatGeometry) { g.Proposed[1].Cell = at(5, 4) },
		"lines missing":  func(g *mp.CombatGeometry) { g.Proposed[0].Lines = nil },
		"path sans pawn": func(g *mp.CombatGeometry) { g.Proposed[0].PathTicks = proto.Int32(3) },
		"over the cap": func(g *mp.CombatGeometry) {
			for i := 0; len(g.Proposed) < CombatGeometryMaxCells; i++ {
				g.Proposed = append(g.Proposed, row(at(int32(10+i), 9)))
			}
		},
	} {
		g := reply()
		mutate(g)
		if ValidateCombatGeometry(g, request) == nil {
			t.Errorf("%s: accepted", name)
		}
	}
	request.Propose = nil
	if ValidateCombatGeometry(reply(), request) == nil {
		t.Error("proposals without propose accepted")
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
