package bridge

import (
	"context"
	"errors"
	"math"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
)

func defenseRegion() CellRect {
	return CellRect{Min: domain.Cell{X: 4, Z: 6}, Max: domain.Cell{X: 5, Z: 6}}
}
func defenseComplete(n int) *o.Completeness {
	return &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(uint64(n)), Returned: proto.Uint64(uint64(n)), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
}
func defenseSiteFixture() *o.DefenseSiteSnapshot {
	open := &o.DefenseCell{Cell: &c.Cell{X: proto.Int32(4), Z: proto.Int32(6)}, Fogged: proto.Bool(false), Terrain: proto.String("Soil"), Walkable: proto.Bool(true), Passable: proto.Bool(true),
		CoverFill: proto.Float64(0), BlocksSight: proto.Bool(false), PlayerOwned: proto.Bool(false), NaturalRock: proto.Bool(false), Door: proto.Bool(false), EdgeReachable: proto.Bool(true), HomeArea: proto.Bool(false)}
	sandbag := &o.DefenseCell{Cell: &c.Cell{X: proto.Int32(5), Z: proto.Int32(6)}, Fogged: proto.Bool(false), Terrain: proto.String("Soil"), Walkable: proto.Bool(false), Passable: proto.Bool(true),
		CoverFill: proto.Float64(0.57), BlocksSight: proto.Bool(false), EdificeDefName: proto.String("Sandbags"), PlayerOwned: proto.Bool(true), NaturalRock: proto.Bool(false), Door: proto.Bool(false), EdgeReachable: proto.Bool(true), HomeArea: proto.Bool(true)}
	return &o.DefenseSiteSnapshot{Context: pbContext(), MapSize: &o.MapSize{Width: proto.Uint32(250), Height: proto.Uint32(250)},
		Region: &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(4), Z: proto.Int32(6)}, Maximum: &c.Cell{X: proto.Int32(5), Z: proto.Int32(6)}},
		Cells:  []*o.DefenseCell{open, sandbag}, Completeness: defenseComplete(2)}
}
func defenseServer(t *testing.T, tool string, want proto.Message, reply proto.Message) *Client {
	t.Helper()
	return testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		if arg.Tool != tool {
			t.Fatal(arg.Tool)
		}
		draftTestRequest(t, arg, want)
		return pbResult(reply), nil
	}}, time.Second)
}

func TestDefenseSiteReadsCompleteCensus(t *testing.T) {
	want := &o.DefenseSiteRequest{Scope: &o.ReadScope{ExpectedIdentity: pbIdentity()}, Region: &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(4), Z: proto.Int32(6)}, Maximum: &c.Cell{X: proto.Int32(5), Z: proto.Int32(6)}}}
	client := defenseServer(t, "rimgovernor/observations_read_defense_site", want, &o.DefenseSiteReply{Outcome: &o.DefenseSiteReply_Observed{Observed: defenseSiteFixture()}})
	site, _, err := client.ReadDefenseSite(context.Background(), pbIdentity(), defenseRegion())
	if err != nil {
		t.Fatal(err)
	}
	if site.Width != 250 || site.Region != defenseRegion() || len(site.Cells) != 2 {
		t.Fatalf("%+v", site)
	}
	if got := site.Cells[1]; got.EdificeDefName != "Sandbags" || !got.PlayerOwned || got.CoverFill != 0.57 || got.Walkable || !got.Passable || !got.EdgeReachable || !got.HomeArea {
		t.Fatalf("%+v", got)
	}
	if got := site.Cells[0]; got.EdificeDefName != "" || got.PlayerOwned || got.CoverFill != 0 || !got.Walkable {
		t.Fatalf("%+v", got)
	}
}
func TestDefenseSiteFoggedCellCarriesNoFacts(t *testing.T) {
	fixture := defenseSiteFixture()
	fixture.Cells[0] = &o.DefenseCell{Cell: fixture.Cells[0].Cell, Fogged: proto.Bool(true), Issues: []*o.ReadIssue{{Field: proto.String("terrain"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}}
	client := defenseServer(t, "rimgovernor/observations_read_defense_site", nil, &o.DefenseSiteReply{Outcome: &o.DefenseSiteReply_Observed{Observed: fixture}})
	client = testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(&o.DefenseSiteReply{Outcome: &o.DefenseSiteReply_Observed{Observed: fixture}}), nil
	}}, time.Second)
	site, _, err := client.ReadDefenseSite(context.Background(), pbIdentity(), defenseRegion())
	if err != nil || !site.Cells[0].Fogged || site.Cells[0].Walkable || site.Cells[0].EdgeReachable {
		t.Fatal(site, err)
	}
	fixture.Cells[0].Walkable = proto.Bool(true)
	if _, _, err = client.ReadDefenseSite(context.Background(), pbIdentity(), defenseRegion()); !errors.Is(err, ErrContract) {
		t.Fatal("fogged cell with invented traversal accepted", err)
	}
}
func TestDefenseSiteRejectsMalformed(t *testing.T) {
	edits := map[string]func(*o.DefenseSiteSnapshot){
		"missing cell":       func(s *o.DefenseSiteSnapshot) { s.Cells = s.Cells[:1] },
		"count only":         func(s *o.DefenseSiteSnapshot) { s.Cells = append(s.Cells, s.Cells[1]) },
		"duplicate cell":     func(s *o.DefenseSiteSnapshot) { s.Cells[0].Cell = s.Cells[1].Cell },
		"outside region":     func(s *o.DefenseSiteSnapshot) { s.Cells[0].Cell.X = proto.Int32(9) },
		"region changed":     func(s *o.DefenseSiteSnapshot) { s.Region.Maximum.X = proto.Int32(6) },
		"incomplete":         func(s *o.DefenseSiteSnapshot) { s.Completeness.Unreadable = proto.Uint64(1) },
		"cover nan":          func(s *o.DefenseSiteSnapshot) { s.Cells[1].CoverFill = proto.Float64(math.NaN()) },
		"cover over one":     func(s *o.DefenseSiteSnapshot) { s.Cells[1].CoverFill = proto.Float64(1.5) },
		"terrain missing":    func(s *o.DefenseSiteSnapshot) { s.Cells[0].Terrain = nil },
		"reachable missing":  func(s *o.DefenseSiteSnapshot) { s.Cells[0].EdgeReachable = nil },
		"owned without edif": func(s *o.DefenseSiteSnapshot) { s.Cells[0].PlayerOwned = proto.Bool(true) },
		"walkable impassabl": func(s *o.DefenseSiteSnapshot) { s.Cells[0].Passable = proto.Bool(false) },
		"identity":           func(s *o.DefenseSiteSnapshot) { s.Context.Identity.LoadToken = proto.String("other") },
		"map size":           func(s *o.DefenseSiteSnapshot) { s.MapSize = nil },
	}
	for name, edit := range edits {
		fixture := defenseSiteFixture()
		edit(fixture)
		client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
			return pbResult(&o.DefenseSiteReply{Outcome: &o.DefenseSiteReply_Observed{Observed: fixture}}), nil
		}}, time.Second)
		if _, _, err := client.ReadDefenseSite(context.Background(), pbIdentity(), defenseRegion()); !errors.Is(err, ErrContract) {
			t.Fatalf("%s accepted: %v", name, err)
		}
	}
}
func TestDefenseSiteRegionBounds(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		t.Fatal("native read issued for an invalid region")
		return nil, nil
	}}, time.Second)
	for _, region := range []CellRect{
		{Min: domain.Cell{X: 5, Z: 0}, Max: domain.Cell{X: 4, Z: 0}},
		{Min: domain.Cell{X: -1, Z: 0}, Max: domain.Cell{X: 4, Z: 0}},
		{Min: domain.Cell{X: 0, Z: 0}, Max: domain.Cell{X: 63, Z: 32}},
	} {
		if _, _, err := client.ReadDefenseSite(context.Background(), pbIdentity(), region); !errors.Is(err, ErrContract) {
			t.Fatal(region, err)
		}
	}
	if (CellRect{Min: domain.Cell{X: 0, Z: 0}, Max: domain.Cell{X: 63, Z: 31}}).Cells() != 2048 {
		t.Fatal("cell count")
	}
}
func TestDefenseSiteUnavailableAndFailure(t *testing.T) {
	for _, reply := range []*o.DefenseSiteReply{
		{Outcome: &o.DefenseSiteReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_LIMIT_EXCEEDED.Enum()}}},
		{Outcome: &o.DefenseSiteReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_INVALID_REQUEST.Enum()}}},
	} {
		client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
			return pbResult(reply), nil
		}}, time.Second)
		_, _, err := client.ReadDefenseSite(context.Background(), pbIdentity(), defenseRegion())
		var native *NativeUnavailable
		if reply.GetUnavailable() != nil && !errors.As(err, &native) || reply.GetFailure() != nil && err == nil {
			t.Fatal(reply, err)
		}
	}
}

func linesFixture() *o.LinesOfFireSnapshot {
	line := func(fx, tx int32, los bool, target, shooter float64) *o.LineOfFire {
		return &o.LineOfFire{From: &c.Cell{X: proto.Int32(fx), Z: proto.Int32(0)}, To: &c.Cell{X: proto.Int32(tx), Z: proto.Int32(3)}, LineOfSight: proto.Bool(los), TargetCover: proto.Float64(target), ShooterCover: proto.Float64(shooter), Distance: proto.Float64(3.2)}
	}
	return &o.LinesOfFireSnapshot{Context: pbContext(), Lines: []*o.LineOfFire{line(0, 10, true, 0, 0.57), line(0, 11, false, 0.75, 0.57), line(1, 10, true, 0, 0), line(1, 11, true, 0.75, 0)}, Completeness: defenseComplete(4)}
}
func TestLinesOfFireReadsEveryPair(t *testing.T) {
	firing := []domain.Cell{{X: 0, Z: 0}, {X: 1, Z: 0}}
	approach := []domain.Cell{{X: 10, Z: 3}, {X: 11, Z: 3}}
	want := &o.LinesOfFireRequest{Scope: &o.ReadScope{ExpectedIdentity: pbIdentity()},
		FiringCells:   []*c.Cell{{X: proto.Int32(0), Z: proto.Int32(0)}, {X: proto.Int32(1), Z: proto.Int32(0)}},
		ApproachCells: []*c.Cell{{X: proto.Int32(10), Z: proto.Int32(3)}, {X: proto.Int32(11), Z: proto.Int32(3)}}}
	client := defenseServer(t, "rimgovernor/observations_read_lines_of_fire", want, &o.LinesOfFireReply{Outcome: &o.LinesOfFireReply_Observed{Observed: linesFixture()}})
	lines, _, err := client.ReadLinesOfFire(context.Background(), pbIdentity(), firing, approach)
	if err != nil || len(lines.Lines) != 4 {
		t.Fatal(lines, err)
	}
	if got := lines.Lines[1]; !got.Known || got.LineOfSight || got.TargetCover != 0.75 || got.ShooterCover != 0.57 || got.Distance != 3.2 {
		t.Fatalf("%+v", got)
	}
}
func TestLinesOfFireUnknownEndpointNeedsIssue(t *testing.T) {
	fixture := linesFixture()
	fixture.Lines[0].LineOfSight, fixture.Lines[0].TargetCover, fixture.Lines[0].ShooterCover = nil, nil, nil
	firing := []domain.Cell{{X: 0, Z: 0}, {X: 1, Z: 0}}
	approach := []domain.Cell{{X: 10, Z: 3}, {X: 11, Z: 3}}
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(&o.LinesOfFireReply{Outcome: &o.LinesOfFireReply_Observed{Observed: fixture}}), nil
	}}, time.Second)
	if _, _, err := client.ReadLinesOfFire(context.Background(), pbIdentity(), firing, approach); !errors.Is(err, ErrContract) {
		t.Fatal("silent unknown accepted", err)
	}
	fixture.Lines[0].Issues = []*o.ReadIssue{{Field: proto.String("line_of_sight"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}
	lines, _, err := client.ReadLinesOfFire(context.Background(), pbIdentity(), firing, approach)
	if err != nil || lines.Lines[0].Known || !lines.Lines[1].Known {
		t.Fatal(lines, err)
	}
	fixture.Lines[0].LineOfSight = proto.Bool(true)
	if _, _, err = client.ReadLinesOfFire(context.Background(), pbIdentity(), firing, approach); !errors.Is(err, ErrContract) {
		t.Fatal("partial facts accepted", err)
	}
}
func TestLinesOfFireRejectsMalformed(t *testing.T) {
	firing := []domain.Cell{{X: 0, Z: 0}, {X: 1, Z: 0}}
	approach := []domain.Cell{{X: 10, Z: 3}, {X: 11, Z: 3}}
	edits := map[string]func(*o.LinesOfFireSnapshot){
		"missing pair":   func(s *o.LinesOfFireSnapshot) { s.Lines = s.Lines[:3]; s.Completeness = defenseComplete(3) },
		"duplicate pair": func(s *o.LinesOfFireSnapshot) { s.Lines[3] = s.Lines[2] },
		"foreign pair":   func(s *o.LinesOfFireSnapshot) { s.Lines[3].To.X = proto.Int32(12) },
		"cover nan":      func(s *o.LinesOfFireSnapshot) { s.Lines[0].TargetCover = proto.Float64(math.NaN()) },
		"cover over one": func(s *o.LinesOfFireSnapshot) { s.Lines[0].ShooterCover = proto.Float64(2) },
		"distance":       func(s *o.LinesOfFireSnapshot) { s.Lines[0].Distance = proto.Float64(-1) },
		"incomplete":     func(s *o.LinesOfFireSnapshot) { s.Completeness.Page.Complete = proto.Bool(false) },
		"identity":       func(s *o.LinesOfFireSnapshot) { s.Context.Identity.MapId = proto.Int32(3) },
	}
	for name, edit := range edits {
		fixture := linesFixture()
		edit(fixture)
		client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
			return pbResult(&o.LinesOfFireReply{Outcome: &o.LinesOfFireReply_Observed{Observed: fixture}}), nil
		}}, time.Second)
		if _, _, err := client.ReadLinesOfFire(context.Background(), pbIdentity(), firing, approach); !errors.Is(err, ErrContract) {
			t.Fatalf("%s accepted: %v", name, err)
		}
	}
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		t.Fatal("native read issued for invalid cells")
		return nil, nil
	}}, time.Second)
	many := make([]domain.Cell, 65)
	for i := range many {
		many[i] = domain.Cell{X: int32(i), Z: 0}
	}
	for _, bad := range [][]domain.Cell{nil, many, {{X: 0, Z: 0}, {X: 0, Z: 0}}, {{X: -1, Z: 0}}} {
		if _, _, err := client.ReadLinesOfFire(context.Background(), pbIdentity(), bad, approach); !errors.Is(err, ErrContract) {
			t.Fatal(bad, err)
		}
	}
}

func TestCombatPawnsGearRangeAndLord(t *testing.T) {
	snapshot := combatPawnsFixture()
	rifle := &o.GearItem{Thing: &o.EntityRef{Id: proto.String("rifle")}, Weapon: proto.Bool(true), Ranged: proto.Bool(true), Melee: proto.Bool(false), Range: proto.Float64(30.9)}
	snapshot.Pawns[0].Equipment.Equipped = append(snapshot.Pawns[0].Equipment.Equipped, rifle)
	snapshot.Pawns[0].LordJobClass, snapshot.Pawns[0].LordToilClass = proto.String("LordJob_AssaultColony"), proto.String("LordToil_AssaultColonySappers")
	identity := pbIdentity()
	serve := func() *Client {
		return testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
			return pbResult(&o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: snapshot}}), nil
		}}, time.Second)
	}
	reply, _, err := serve().ReadCombatPawns(context.Background(), identity, []string{"pawn-1", "missing"})
	if err != nil || reply.GetObserved().Pawns[0].GetLordToilClass() != "LordToil_AssaultColonySappers" || reply.GetObserved().Pawns[0].Equipment.Equipped[1].GetRange() != 30.9 {
		t.Fatal(reply, err)
	}
	for name, edit := range map[string]func(){
		"melee range":    func() { snapshot.Pawns[0].Equipment.Equipped[0].Range = proto.Float64(1) },
		"zero range":     func() { rifle.Range = proto.Float64(0) },
		"nan range":      func() { rifle.Range = proto.Float64(math.NaN()) },
		"blank lord job": func() { snapshot.Pawns[0].LordJobClass = proto.String(" ") },
	} {
		snapshot = combatPawnsFixture()
		rifle = &o.GearItem{Thing: &o.EntityRef{Id: proto.String("rifle")}, Weapon: proto.Bool(true), Ranged: proto.Bool(true), Melee: proto.Bool(false), Range: proto.Float64(30.9)}
		snapshot.Pawns[0].Equipment.Equipped = append(snapshot.Pawns[0].Equipment.Equipped, rifle)
		edit()
		if _, _, err := serve().ReadCombatPawns(context.Background(), identity, []string{"pawn-1", "missing"}); !errors.Is(err, ErrContract) {
			t.Fatalf("%s accepted: %v", name, err)
		}
	}
}
