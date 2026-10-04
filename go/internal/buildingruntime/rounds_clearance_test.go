package buildingruntime

import (
	"context"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

type roundsClearanceNative struct {
	*roundsBlightNative
	rows     []*o.ClearanceTarget
	chunks   []*o.ClearanceChunk
	sites    []*c.Cell
	previews []domain.ZoneCreate
}

func (n *roundsClearanceNative) ReadClearanceTargets(_ context.Context, _ *c.Identity, _ bool) (*o.ClearanceTargetsReply, bridge.Result, error) {
	return &o.ClearanceTargetsReply{Outcome: &o.ClearanceTargetsReply_Observed{Observed: &o.ClearanceTargetsSnapshot{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Targets: n.rows, Chunks: n.chunks, DumpSites: n.sites}}}, bridge.Result{}, nil
}
func (n *roundsClearanceNative) PreviewZone(_ context.Context, _ *c.Identity, target domain.ZoneCreate) (*op.ZonePreviewReply, bridge.Result, error) {
	n.previews = append(n.previews, target)
	return &op.ZonePreviewReply{Outcome: &op.ZonePreviewReply_Evaluated{Evaluated: &op.ZonePreview{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Accepted: proto.Bool(true)}}}, bridge.Result{}, nil
}
func clearanceChunk(id string, x, z int32, forbidden, stored, destination bool) *o.ClearanceChunk {
	return &o.ClearanceChunk{EntityId: proto.String(id), DefName: proto.String("ChunkGranite"), Cell: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}, Forbidden: proto.Bool(forbidden), Stored: proto.Bool(stored), Destination: proto.Bool(destination)}
}
func clearanceTestRow(id string, x int32) *o.ClearanceTarget {
	return &o.ClearanceTarget{EntityId: proto.String(id), DefName: proto.String("Wall"), Occupied: &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(x), Z: proto.Int32(5)}, Maximum: &c.Cell{X: proto.Int32(x), Z: proto.Int32(5)}}, Class: o.ClearanceClass_CLEARANCE_CLASS_ANCIENT_WALL_DOOR, Deconstructible: proto.Bool(true), InHome: proto.Bool(true), AncientDanger: proto.Bool(false), Designated: proto.Bool(false)}
}
func TestRoundsClearanceAdmitsNearestSingleTargetAndJournalsHolds(t *testing.T) {
	reviewer, db, _, _, native := roundsFixture(t)
	v := native.reply.GetObserved()
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	newRow := func(id string) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	}
	native.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{newRow("cutter"), newRow("cutter2")}, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}}}

	source := &roundsClearanceNative{roundsBlightNative: &roundsBlightNative{roundsNative: native}}
	source.rows = []*o.ClearanceTarget{clearanceTestRow("far", 80), clearanceTestRow("near", v.Center.GetX())}
	source.rows[1].Occupied.Minimum.Z = proto.Int32(v.Center.GetZ())
	source.rows[1].Occupied.Maximum.Z = proto.Int32(v.Center.GetZ())
	roof := clearanceTestRow("roof", v.Center.GetX())
	roof.RoofBlocker = proto.String("unsafe")
	source.rows = append(source.rows, roof)
	reviewer.native = source
	reviewer.methods = domain.Known([]policy.ConcernID{policy.ClearHomeObstructions})
	ctx := context.Background()
	review, err := reviewer.Step(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if len(review.Review.ClearanceHolds) != 1 || review.Review.ClearanceHolds[0].Reason != "roof_blocker" {
		t.Fatal(review.Review.ClearanceHolds)
	}
	planner, err := NewRoundsClearancePlanner(reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err, review.Review.Development.Rows)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 1 {
		t.Fatal(plan, err)
	}
	target, ok := plan.Spec.Actions()[0].Deconstruction()
	if !ok || target.Target() != "near" {
		t.Fatal(target, ok)
	}
	root := reviewer.player.session.State().Snapshot
	scope := root
	scope.Plan = plan.Spec.ID()
	scope.Revision = plan.Spec.Revision()
	if err = db.AuthorizeRoundsPlan(ctx, root, scope); err != nil {
		t.Fatal(err)
	}
	if work, _, err := clockSchedulerWork(plan, scope); err != nil || !work {
		t.Fatal(work, err)
	}
	if next, err := planner.Step(ctx); err != nil || next.Verdict != BuildingReasonExistingWork {
		t.Fatal(next, err)
	}
}

// Chunks are hauls: with no deconstructible target and a pending chunk (in
// Home, allowed, unstored, no store will take it), the planner admits one
// low-priority dumping stockpile on the native outdoor footprint sized for
// the pending stacks; a chunk ordinary hauling already has a destination for
// is no deficit at all.
func TestRoundsClearanceAdmitsChunkDumpForPendingChunks(t *testing.T) {
	reviewer, db, _, _, native := roundsFixture(t)
	v := native.reply.GetObserved()
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	newRow := func(id string) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	}
	native.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{newRow("cutter"), newRow("cutter2")}, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}}}
	zonesAvailable(v)

	source := &roundsClearanceNative{roundsBlightNative: &roundsBlightNative{roundsNative: native}}
	source.chunks = []*o.ClearanceChunk{clearanceChunk("forbidden", 40, 40, true, false, true), clearanceChunk("stored", 41, 40, false, true, false)}
	reviewer.native = source
	reviewer.methods = domain.Known([]policy.ConcernID{policy.ClearHomeObstructions})
	ctx := context.Background()
	review, err := reviewer.Step(ctx)
	if err != nil {
		t.Fatal(err)
	}
	for _, binding := range review.Review.Standards {
		if binding.Concern != policy.ClearHomeObstructions {
			continue
		}
		if goal, err := db.LoadStandard(ctx, binding.Standard); err != nil || goal.Standard.Finding == domain.FindingUnmet {
			t.Fatal("forbidden and stored chunks are no clearance deficit", goal, err)
		}
	}

	source.chunks = append(source.chunks, clearanceChunk("loose", 42, 40, false, false, false), clearanceChunk("banned", 43, 40, true, false, false))
	for x := int32(50); x < 60; x++ {
		source.sites = append(source.sites, &c.Cell{X: proto.Int32(x), Z: proto.Int32(60)})
	}
	if review, err = reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoundsClearancePlanner(reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err, review.Review.Development.Rows)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 1 {
		t.Fatal(plan, err)
	}
	zone, ok := plan.Spec.Actions()[0].ZoneCreate()
	if !ok || zone.Priority() != domain.LowPriority || zone.Label() != "Dumping" || len(zone.Cells()) != 4 || zone.Cells()[0] != (domain.Cell{X: 50, Z: 60}) {
		t.Fatal(zone, ok)
	}
	if allow := allowOf(zone); len(allow) != 2 || allow[0] != "ChunkGranite" || allow[1] != "ChunkSlagSteel" {
		t.Fatal(allow)
	}
	if len(source.previews) != 1 {
		t.Fatal(source.previews)
	}
	if next, err := planner.Step(ctx); err != nil || next.Verdict != BuildingReasonExistingWork {
		t.Fatal(next, err)
	}
}

// A chunk-haul batch retires once its designations land, while ordinary
// hauling may leave the chunks standing: the next step reads the batch as
// ordered and waits, rather than re-committing the same method (a journal
// conflict on every step).
func TestRoundsClearanceChunkHaulBatchIsOrderedOnce(t *testing.T) {
	reviewer, db, _, _, native := roundsFixture(t)
	v := native.reply.GetObserved()
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	newRow := func(id string) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	}
	native.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{newRow("cutter"), newRow("cutter2")}, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}}}

	source := &roundsClearanceNative{roundsBlightNative: &roundsBlightNative{roundsNative: native}}
	source.chunks = []*o.ClearanceChunk{clearanceChunk("haulable", 42, 40, false, false, true)}
	reviewer.native = source
	reviewer.methods = domain.Known([]policy.ConcernID{policy.ClearHomeObstructions})
	ctx := context.Background()
	review, err := reviewer.Step(ctx)
	if err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoundsClearancePlanner(reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err, review.Review.Development.Rows)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	snapshot := reviewer.player.session.State().Snapshot
	snapshot.Plan, snapshot.Revision = plan.Spec.ID(), plan.Spec.Revision()
	for _, action := range plan.Spec.Actions() {
		if _, err = db.Prepare(ctx, plan.Spec.ID(), action.ID(), snapshot, 7); err != nil {
			t.Fatal(err)
		}
		if _, err = db.Dispatch(ctx, plan.Spec.ID(), action.ID(), snapshot, 7); err != nil {
			t.Fatal(err)
		}
		if _, err = db.RecordReceipt(ctx, plan.Spec.ID(), action.ID(), 1, domain.ReceiptAccepted); err != nil {
			t.Fatal(err)
		}
	}
	if _, err = reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	if plan, err = db.LoadPlan(ctx, result.Plan); err != nil || !plan.Retired {
		t.Fatal("the ordered batch should retire", plan.Retired, err)
	}
	next, err := planner.Step(ctx)
	if err != nil || !next.Verdict.Is(WaitMethodUsed) || next.NativeWorkTicks != chunkHaulWorkTicks {
		t.Fatal(next, err)
	}
}

// A chunk the first batch ordered that ordinary hauling never moves does not
// hold the rest of the census (#1234): the next batch takes the chunks no
// batch has ordered.
func TestRoundsClearanceNextChunkBatchSkipsOrderedChunks(t *testing.T) {
	reviewer, db, _, _, native := roundsFixture(t)
	v := native.reply.GetObserved()
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	newRow := func(id string) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	}
	native.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{newRow("cutter"), newRow("cutter2")}, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}}}

	source := &roundsClearanceNative{roundsBlightNative: &roundsBlightNative{roundsNative: native}}
	for i := int32(0); i <= maxChunkHaulBatch; i++ {
		source.chunks = append(source.chunks, clearanceChunk(fmt.Sprintf("chunk%d", i), 40+i, 40, false, false, true))
	}
	reviewer.native = source
	reviewer.methods = domain.Known([]policy.ConcernID{policy.ClearHomeObstructions})
	ctx := context.Background()
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoundsClearancePlanner(reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	first, err := planner.Step(ctx)
	if err != nil || first.Verdict != BuildingReasonAdmitted {
		t.Fatal(first, err)
	}
	plan, err := db.LoadPlan(ctx, first.Plan)
	if err != nil || len(plan.Spec.Actions()) != maxChunkHaulBatch {
		t.Fatal(plan, err)
	}
	snapshot := reviewer.player.session.State().Snapshot
	snapshot.Plan, snapshot.Revision = plan.Spec.ID(), plan.Spec.Revision()
	for _, action := range plan.Spec.Actions() {
		if _, err = db.Prepare(ctx, plan.Spec.ID(), action.ID(), snapshot, 7); err != nil {
			t.Fatal(err)
		}
		if _, err = db.Dispatch(ctx, plan.Spec.ID(), action.ID(), snapshot, 7); err != nil {
			t.Fatal(err)
		}
		if _, err = db.RecordReceipt(ctx, plan.Spec.ID(), action.ID(), 1, domain.ReceiptAccepted); err != nil {
			t.Fatal(err)
		}
	}
	if _, err = reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	next, err := planner.Step(ctx)
	if err != nil || next.Verdict != BuildingReasonAdmitted {
		t.Fatal(next, err)
	}
	if plan, err = db.LoadPlan(ctx, next.Plan); err != nil || len(plan.Spec.Actions()) != 1 {
		t.Fatal(plan, err)
	}
	if c, ok := plan.Spec.Actions()[0].CoverClearance(); !ok || c.Thing() != fmt.Sprintf("chunk%d", maxChunkHaulBatch) {
		t.Fatal(c, ok)
	}
}

// TestGroundStepMethodOrdersRoofBeforeWalls pins the planned-ground intents
// (#1245): a room's walls go behind remove_roof on the whole cleared ground,
// furniture one building per method, a ring door as a wall swap, floors as
// floor removals.
func TestGroundStepMethodOrdersRoofBeforeWalls(t *testing.T) {
	ground := []policy.Rectangle{{X: 9, Z: 9, Width: 5, Height: 5}}
	wall := policy.ClearanceTarget{EntityID: "Wall1", DefName: "Wall", Minimum: domain.Cell{X: 11, Z: 11}, Maximum: domain.Cell{X: 11, Z: 11}, Player: true, EnclosesRoom: true}
	prefix, actions, err := groundStepMethod("plan", policy.GroundStep{Ground: ground[0], Phase: policy.GroundWalls, Targets: []policy.ClearanceTarget{wall}, Roof: []domain.Cell{{X: 12, Z: 12}}}, ground)
	if err != nil || prefix != "ground-walls-9-9-" || len(actions) != 2 {
		t.Fatal(prefix, actions, err)
	}
	if roof, ok := actions[0].RemoveRoof(); !ok || len(roof.Cells()) != 1 {
		t.Fatalf("remove_roof first: %v", actions[0].Kind())
	}
	cut, ok := actions[1].Deconstruction()
	if !ok || cut.Target() != "Wall1" || len(cut.ClearedGround()) != 1 || cut.ClearedGround()[0] != (domain.GroundRect{Origin: domain.Cell{X: 9, Z: 9}, Width: 5, Height: 5}) {
		t.Fatalf("wall with cleared ground: %+v", cut)
	}

	bed := policy.ClearanceTarget{EntityID: "Bed1", DefName: "Bed", Minimum: domain.Cell{X: 10, Z: 10}, Player: true}
	prefix, actions, err = groundStepMethod("plan", policy.GroundStep{Ground: ground[0], Phase: policy.GroundFurniture, Targets: []policy.ClearanceTarget{bed, wall}}, ground)
	if err != nil || prefix != "deconstruct-Bed1-" || len(actions) != 1 {
		t.Fatal(prefix, actions, err)
	}
	if cut, ok := actions[0].Deconstruction(); !ok || cut.ClearedGround() != nil {
		t.Fatalf("furniture is a plain deconstruction: %+v", cut)
	}

	door := policy.ClearanceTarget{EntityID: "Door1", DefName: "Door", Minimum: domain.Cell{X: 9, Z: 10}, Maximum: domain.Cell{X: 9, Z: 10}, Player: true, EnclosesRoom: true}
	prefix, actions, err = groundStepMethod("plan", policy.GroundStep{Ground: ground[0], Phase: policy.GroundDoors, Targets: []policy.ClearanceTarget{door}}, ground)
	if err != nil || prefix != "swap-door-Door1-" || len(actions) != 1 {
		t.Fatal(prefix, actions, err)
	}
	if cut, ok := actions[0].Deconstruction(); !ok || !cut.ReplacesWithWall() || cut.ClearedGround() != nil {
		t.Fatalf("ring door is a wall swap without cleared ground: %+v", cut)
	}

	prefix, actions, err = groundStepMethod("plan", policy.GroundStep{Ground: ground[0], Phase: policy.GroundFloors, Floors: []policy.ClearanceFloor{{Cell: domain.Cell{X: 10, Z: 10}, DefName: "WoodPlankFloor"}}}, ground)
	if err != nil || prefix != "ground-floors-9-9-" || len(actions) != 1 {
		t.Fatal(prefix, actions, err)
	}
	if floor, ok := actions[0].FloorRemoval(); !ok || floor.Cell() != (domain.Cell{X: 10, Z: 10}) {
		t.Fatalf("floor removal: %v", actions[0].Kind())
	}
}
