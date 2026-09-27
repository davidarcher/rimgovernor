package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/mirror"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type pawnListNative struct {
	tick  int64
	pawns []*o.PawnState
}

func (n *pawnListNative) ReadRoutinePawns(_ context.Context, id *c.Identity, _ []string) (*o.ListPawnsReply, bridge.Result, error) {
	at := &c.ObservationContext{Identity: id, Tick: proto.Int64(n.tick)}
	return &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: at, Pawns: n.pawns}}}, bridge.Result{}, nil
}

func pawnRow(id, kind string) *o.PawnState {
	return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id)}, KindDefName: proto.String(kind)}
}

// TestPawnSectionPublishesAndServesTable: the review's pawn detail read is
// a mirror section keyed by pawn id; the reply is the published table in
// the native's order, and a later read drops rows the roster dropped.
func TestPawnSectionPublishesAndServesTable(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	m := mirror.New()
	scope := mirror.Scope{Load: "l", Map: 1, Generation: 2}
	id := &c.Identity{ColonyId: proto.String("c"), LoadToken: proto.String("l"), MapId: proto.Int32(1)}
	native := &pawnListNative{tick: 100, pawns: []*o.PawnState{pawnRow("Pawn_2", "a"), pawnRow("Pawn_1", "b")}}
	reply, _, err := readPawns(ctx, m, scope, native, id, []string{"Pawn_1", "Pawn_2"})
	if err != nil {
		t.Fatal(err)
	}
	got := reply.GetObserved().GetPawns()
	if len(got) != 2 || got[0].GetPawn().GetId() != "Pawn_2" || got[1].GetPawn().GetId() != "Pawn_1" || reply.GetObserved().GetContext().GetTick() != 100 {
		t.Fatalf("reply = %v", reply)
	}
	table, ok := mirror.Get[string, *o.PawnState](m, scope, pawnSectionName)
	if !ok || len(table.Rows) != 2 || table.AsOf != mirror.At(100) {
		t.Fatalf("table = %+v ok=%v", table, ok)
	}
	native.tick, native.pawns = 130, []*o.PawnState{pawnRow("Pawn_1", "c")}
	if reply, _, err = readPawns(ctx, m, scope, native, id, []string{"Pawn_1"}); err != nil || len(reply.GetObserved().GetPawns()) != 1 {
		t.Fatalf("second read = %v %v", reply, err)
	}
	table, _ = mirror.Get[string, *o.PawnState](m, scope, pawnSectionName)
	if len(table.Rows) != 1 || table.Rows["Pawn_1"].GetKindDefName() != "c" || table.AsOf != mirror.At(130) {
		t.Fatalf("table after roster change = %+v", table)
	}
}

type benchCountingNative struct {
	benches []bridge.GearBenchRead
	reads   int
}

func (n *benchCountingNative) ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error) {
	n.reads++
	return n.benches, bridge.Result{}, nil
}

// TestBenchSectionServesPlannersOfTheCensus: the review refreshes the bench
// section; a planner of the same census serves the table it published at
// a later tick of a running clock, and reads again once the census is
// invalidated.
func TestBenchSectionServesPlannersOfTheCensus(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	identity := observation.Identity{Colony: "c", Load: "l", Map: 1, Tick: 100, NativeGeneration: domain.Known(domain.NativeGeneration(3))}
	r := &RoutineReviewer{mirror: mirror.New()}
	reading := observation.RoutineReading{}
	reading.Projection.Identity = identity
	r.census.retain(reading, false, domain.Unknown[[]policy.ConstructionClaim]())
	native := &benchCountingNative{benches: []bridge.GearBenchRead{{Token: "t", Bench: policy.GearBench{ID: "Bench_1"}}}}
	id := &c.Identity{ColonyId: proto.String("c"), LoadToken: proto.String("l"), MapId: proto.Int32(1)}
	if rows, _, err := r.benchSource(native, identity, true).ReadGearBenches(ctx, id); err != nil || len(rows) != 1 || native.reads != 1 {
		t.Fatalf("review read = %v %v reads=%d", rows, err, native.reads)
	}
	later := identity
	later.Tick += 40
	if rows, _, err := r.benchSource(native, later, false).ReadGearBenches(ctx, id); err != nil || len(rows) != 1 || rows[0].Bench.ID != "Bench_1" || native.reads != 1 {
		t.Fatalf("planner read = %v %v reads=%d", rows, err, native.reads)
	}
	r.census.invalidate()
	if _, _, err := r.benchSource(native, later, false).ReadGearBenches(ctx, id); err != nil || native.reads != 2 {
		t.Fatalf("invalidated census served benches: reads=%d err=%v", native.reads, err)
	}
}
