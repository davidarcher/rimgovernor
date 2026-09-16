package bridge

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
)

func pbCell(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }

var (
	spatialBlocked = []domain.Cell{{X: 10, Z: 14}, {X: 11, Z: 14}}
	spatialTargets = []domain.Cell{{X: 9, Z: 14}, {X: 9, Z: 0}}
	spatialPawns   = []string{"Human1", "Human2"}
)

func spatialFixture() *o.SpatialAccessSnapshot {
	row := func(id string, x, z int32) *o.PawnAccess {
		return &o.PawnAccess{Pawn: &o.EntityRef{Id: proto.String(id), Position: pbCell(x, z)}, CurrentCells: proto.Uint32(400), ProjectedCells: proto.Uint32(398),
			LostCellCount: proto.Uint32(0), ProjectedOrigin: pbCell(x, z), EgressSteps: proto.Uint32(0), Completeness: defenseComplete(2),
			Targets: []*o.AccessTarget{
				{Cell: pbCell(9, 14), NativeReachable: proto.Bool(true), ProjectedReachable: proto.Bool(true), ProjectedSteps: proto.Uint32(13)},
				{Cell: pbCell(9, 0), NativeReachable: proto.Bool(true), ProjectedReachable: proto.Bool(true), ProjectedSteps: proto.Uint32(27)},
			}}
	}
	return &o.SpatialAccessSnapshot{Context: pbContext(), MapCells: proto.Uint32(62500), ObservedWalkableCells: proto.Uint32(40000),
		Pawns: []*o.PawnAccess{row("Human1", 9, 27), row("Human2", 8, 27)}, Completeness: defenseComplete(2)}
}
func spatialClient(t *testing.T, fixture *o.SpatialAccessSnapshot) *Client {
	t.Helper()
	return testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(&o.SpatialAccessReply{Outcome: &o.SpatialAccessReply_Observed{Observed: fixture}}), nil
	}}, time.Second)
}

func TestSpatialAccessReadsAudit(t *testing.T) {
	want := &o.SpatialAccessRequest{Scope: &o.ReadScope{ExpectedIdentity: pbIdentity()}, BlockedCells: []*c.Cell{pbCell(10, 14), pbCell(11, 14)}, TargetCells: []*c.Cell{pbCell(9, 14), pbCell(9, 0)}, PawnIds: spatialPawns}
	client := defenseServer(t, "rimgovernor/observations_read_spatial_access", want, &o.SpatialAccessReply{Outcome: &o.SpatialAccessReply_Observed{Observed: spatialFixture()}})
	access, _, err := client.ReadSpatialAccess(context.Background(), pbIdentity(), spatialBlocked, spatialTargets, spatialPawns)
	if err != nil {
		t.Fatal(err)
	}
	if len(access.Pawns) != 2 || access.MapCells != 62500 || !access.Accepted() {
		t.Fatalf("%+v", access)
	}
	p := access.Pawns[0]
	if p.ID != "Human1" || p.Position != (domain.Cell{X: 9, Z: 27}) || !p.OriginKnown || p.After != 398 || len(p.Targets) != 2 || p.Targets[1].ProjectedSteps != 27 {
		t.Fatalf("%+v", p)
	}
}
func TestSpatialAccessBlockedPawnNeedsEgress(t *testing.T) {
	fixture := spatialFixture()
	fixture.Pawns[0].Pawn.Position = pbCell(10, 14)
	fixture.Pawns[0].ProjectedOrigin = pbCell(9, 14)
	fixture.Pawns[0].EgressSteps = proto.Uint32(1)
	blocked := spatialBlocked
	access, _, err := spatialClient(t, fixture).ReadSpatialAccess(context.Background(), pbIdentity(), blocked, spatialTargets, spatialPawns)
	if err != nil || access.Pawns[0].EgressSteps != 1 || !access.Accepted() {
		t.Fatal(access, err)
	}
	fixture.Pawns[0].ProjectedOrigin = nil
	fixture.Pawns[0].ProjectedCells = proto.Uint32(0)
	fixture.Pawns[0].LostCellCount = proto.Uint32(398)
	fixture.Pawns[0].LostCells = []*c.Cell{pbCell(9, 27)}
	for _, target := range fixture.Pawns[0].Targets {
		target.ProjectedReachable, target.ProjectedSteps = proto.Bool(false), nil
	}
	access, _, err = spatialClient(t, fixture).ReadSpatialAccess(context.Background(), pbIdentity(), blocked, spatialTargets, spatialPawns)
	if err != nil || access.Pawns[0].OriginKnown || access.Accepted() {
		t.Fatal(access, err)
	}
}
func TestSpatialAccessRejectsMalformed(t *testing.T) {
	edits := map[string]func(*o.SpatialAccessSnapshot){
		"missing pawn":             func(s *o.SpatialAccessSnapshot) { s.Pawns = s.Pawns[:1] },
		"unrequested pawn":         func(s *o.SpatialAccessSnapshot) { s.Pawns[1].Pawn.Id = proto.String("Human9") },
		"duplicate pawn":           func(s *o.SpatialAccessSnapshot) { s.Pawns[1].Pawn.Id = proto.String("Human1") },
		"incomplete":               func(s *o.SpatialAccessSnapshot) { s.Completeness.Unreadable = proto.Uint64(1) },
		"target missing":           func(s *o.SpatialAccessSnapshot) { s.Pawns[0].Targets = s.Pawns[0].Targets[:1] },
		"target reordered":         func(s *o.SpatialAccessSnapshot) { s.Pawns[0].Targets[0].Cell = pbCell(9, 0) },
		"steps without reach":      func(s *o.SpatialAccessSnapshot) { s.Pawns[0].Targets[0].ProjectedReachable = proto.Bool(false) },
		"reach without steps":      func(s *o.SpatialAccessSnapshot) { s.Pawns[0].Targets[0].ProjectedSteps = nil },
		"native unknown":           func(s *o.SpatialAccessSnapshot) { s.Pawns[0].Targets[0].NativeReachable = nil },
		"lost count without cells": func(s *o.SpatialAccessSnapshot) { s.Pawns[0].LostCellCount = proto.Uint32(3) },
		"more cells than lost":     func(s *o.SpatialAccessSnapshot) { s.Pawns[0].LostCells = []*c.Cell{pbCell(1, 1)} },
		"projected over current":   func(s *o.SpatialAccessSnapshot) { s.Pawns[0].ProjectedCells = proto.Uint32(401) },
		"origin on blocked":        func(s *o.SpatialAccessSnapshot) { s.Pawns[0].ProjectedOrigin = pbCell(10, 14) },
		"egress without block":     func(s *o.SpatialAccessSnapshot) { s.Pawns[0].EgressSteps = proto.Uint32(2) },
		"moved origin unblocked": func(s *o.SpatialAccessSnapshot) {
			s.Pawns[0].ProjectedOrigin = pbCell(9, 26)
			s.Pawns[0].EgressSteps = proto.Uint32(1)
		},
		"missing origin":   func(s *o.SpatialAccessSnapshot) { s.Pawns[0].ProjectedOrigin = nil },
		"position missing": func(s *o.SpatialAccessSnapshot) { s.Pawns[0].Pawn.Position = nil },
		"map cells":        func(s *o.SpatialAccessSnapshot) { s.MapCells = nil },
		"identity":         func(s *o.SpatialAccessSnapshot) { s.Context.Identity.MapId = proto.Int32(9) },
	}
	for name, edit := range edits {
		fixture := spatialFixture()
		edit(fixture)
		if _, _, err := spatialClient(t, fixture).ReadSpatialAccess(context.Background(), pbIdentity(), spatialBlocked, spatialTargets, spatialPawns); !errors.Is(err, ErrContract) {
			t.Fatalf("%s accepted: %v", name, err)
		}
	}
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		t.Fatal("native read issued for an invalid request")
		return nil, nil
	}}, time.Second)
	many := make([]domain.Cell, 129)
	for i := range many {
		many[i] = domain.Cell{X: int32(i), Z: 0}
	}
	if _, _, err := client.ReadSpatialAccess(context.Background(), pbIdentity(), spatialBlocked, many, nil); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
	if _, _, err := client.ReadSpatialAccess(context.Background(), pbIdentity(), []domain.Cell{{X: 1, Z: 1}, {X: 1, Z: 1}}, nil, nil); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
	if _, _, err := client.ReadSpatialAccess(context.Background(), pbIdentity(), nil, nil, []string{"a", "a"}); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
}
func TestSpatialAccessUnavailable(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(&o.SpatialAccessReply{Outcome: &o.SpatialAccessReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}), nil
	}}, time.Second)
	_, _, err := client.ReadSpatialAccess(context.Background(), pbIdentity(), spatialBlocked, spatialTargets, nil)
	var native *NativeUnavailable
	if !errors.As(err, &native) {
		t.Fatal(err)
	}
}
