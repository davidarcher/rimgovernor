package buildingruntime

import (
	"context"
	"fmt"
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

type roundsClearanceNative struct {
	*roundsBlightNative
	rows     []*o.ClearanceTarget
	chunks   []*o.ClearanceChunk
	previews []domain.ZoneCreate
}

func (n *roundsClearanceNative) ReadClearanceTargets(_ context.Context, _ *c.Identity, _ bool) (*o.ClearanceTargetsReply, bridge.Result, error) {
	return &o.ClearanceTargetsReply{Outcome: &o.ClearanceTargetsReply_Observed{Observed: &o.ClearanceTargetsSnapshot{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Targets: n.rows, Chunks: n.chunks}}}, bridge.Result{}, nil
}
func (n *roundsClearanceNative) PreviewZone(_ context.Context, _ *c.Identity, target domain.ZoneCreate) (*op.ZonePreviewReply, bridge.Result, error) {
	n.previews = append(n.previews, target)
	return &op.ZonePreviewReply{Outcome: &op.ZonePreviewReply_Evaluated{Evaluated: &op.ZonePreview{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Accepted: proto.Bool(true)}}}, bridge.Result{}, nil
}
func clearanceChunk(id string, x, z int32, forbidden, stored, destination bool) *o.ClearanceChunk {
	return &o.ClearanceChunk{EntityId: proto.String(id), DefName: proto.String("ChunkGranite"), Cell: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}, Forbidden: proto.Bool(forbidden), Stored: proto.Bool(stored), Destination: proto.Bool(destination)}
}
func clearanceTestRow(id string, x int32) *o.ClearanceTarget {
	return &o.ClearanceTarget{EntityId: proto.String(id), DefName: proto.String("Wall"), Occupied: &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(x), Z: proto.Int32(5)}, Maximum: &c.Cell{X: proto.Int32(x), Z: proto.Int32(5)}}, Class: o.ClearanceClass_CLEARANCE_CLASS_ANCIENT_WALL_DOOR, Deconstructible: proto.Bool(true), InHome: proto.Bool(true), AncientDanger: proto.Bool(false), Designated: proto.Bool(false), Salvage: &o.SalvageEvidence{Safe: true}}
}
func TestRoundsReviewQueuesRuinsNearestFirstAndJournalsHolds(t *testing.T) {
	reviewer, _, _, _, native := roundsFixture(t)
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
	source.rows[0].Salvage.PathLength, source.rows[1].Salvage.PathLength = 80, 5
	source.rows = append(source.rows, roof)
	reviewer.native = source
	reviewer.methods = domain.Known([]policy.ConcernID{policy.ClearHomeObstructions})
	ctx := context.Background()
	review, err := reviewer.Step(ctx)
	if err != nil {
		t.Fatal(err)
	}
	queue := review.Review.RecoveryQueue
	if queue == nil || queue.Admitted != "roof" {
		t.Fatal(queue)
	}
	status := map[string]policy.RecoveryEntry{}
	for _, e := range queue.Entries {
		status[e.ID] = e
	}
	if status["far"].Status != policy.RecoveryQueued || status["near"].Status != policy.RecoveryQueued || status["roof"].Status != policy.RecoveryAdmitted {
		t.Fatal(queue.Entries)
	}
	if status["near"].Tier != policy.RecoveryRest || status["near"].Distance != 5 {
		t.Fatal(status["near"])
	}
}

// recoveryRoofCatalog serves the roof definitions the batch plan decides roof
// removal by.
type recoveryRoofCatalog struct {
	RoundsClearanceSource
	empty bool
}

type roundsRecoveryNative struct{ *roundsClearanceNative }

func (n *roundsRecoveryNative) DefinitionCatalog(ctx context.Context, identity *c.Identity) (*bridge.DefinitionCatalog, error) {
	return (recoveryRoofCatalog{}).DefinitionCatalog(ctx, identity)
}

func TestRoundsClearanceWaitsForDesignatedBatchWithoutSpendingRetries(t *testing.T) {
	reviewer, db, _, _, native := roundsFixture(t)
	v := native.reply.GetObserved()
	v.ColonistCount, v.WorkerCount = proto.Uint32(2), proto.Uint32(2)
	newRow := func(id string) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{{Field: proto.String("pawn.snapshot"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}, {Field: proto.String("mental_state"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}}
	}
	native.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{newRow("cutter"), newRow("cutter2")}, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}}}
	source := &roundsRecoveryNative{&roundsClearanceNative{roundsBlightNative: &roundsBlightNative{roundsNative: native}}}
	for i := 1; i <= policy.DefaultRecoveryBatch; i++ {
		row := clearanceTestRow(fmt.Sprintf("Thing_Wall%d", i), int32(40+i))
		row.Salvage.PathLength = float64(i)
		source.rows = append(source.rows, row)
	}
	native.cells = &bridge.PlanningWindow{Context: v.Context}
	for z := int32(0); z < 15; z++ {
		for x := int32(30); x < 65; x++ {
			cell := openCell(x, z)
			if z == 5 && x >= 41 && x <= 52 {
				cell.Things = []policy.Thing{{Def: "Wall", ID: uint64(x - 40), Category: policy.ThingBuilding, Flags: policy.FlagEdifice}}
			}
			native.cells.Cells = append(native.cells.Cells, cell)
		}
	}
	reviewer.native, reviewer.methods = source, domain.Known([]policy.ConcernID{policy.ClearHomeObstructions})
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
	if err != nil || len(plan.Spec.Actions()) != policy.DefaultRecoveryBatch {
		t.Fatal("expected one full removal batch", plan, err)
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
	for _, row := range source.rows {
		row.Designated = proto.Bool(true)
	}
	if _, err = reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	for i := 0; i < 12; i++ {
		result, err := planner.Step(ctx)
		if err != nil || result.Verdict != BuildingReasonExistingWork || result.Plan != "" || result.NativeWorkTicks == 0 {
			t.Fatal("pending native work must wait without another method", result, err)
		}
	}
	// The buildings are still present, so clearance has not recovered.
	review, err := db.LoadRounds(ctx)
	if err != nil {
		t.Fatal(err)
	}
	goal, workable, err := db.Workable(ctx, review, policy.ClearHomeObstructions)
	if err != nil || !workable || goal.Standard.Finding != domain.FindingUnmet || len(goal.History) != 1 {
		t.Fatal("designation is not completed clearance", goal, workable, err)
	}
}

func (s recoveryRoofCatalog) DefinitionCatalog(context.Context, *c.Identity) (*bridge.DefinitionCatalog, error) {
	if s.empty {
		return &bridge.DefinitionCatalog{}, nil
	}
	return &bridge.DefinitionCatalog{Defs: map[protoreflect.FullName]map[string]proto.Message{
		(&d.RoofDef{}).ProtoReflect().Descriptor().FullName(): {"RoofConstructed": &d.RoofDef{DefName: "RoofConstructed", CanCollapse: true}},
	}}, nil
}

// Two holders each safe alone but unsafe together under a removable roof: the
// planner takes the roof off first (remove_roof, nothing else), then deconstructs
// the whole batch in one method.
func TestRoundsClearanceRecoveryStepIsRoofFirstThenBatch(t *testing.T) {
	const n = 40
	holder := func(id uint64, x int32) policy.SiteCell {
		return policy.SiteCell{Cell: domain.Cell{X: x, Z: 20}, Roofed: domain.Known(false),
			Things: []policy.Thing{{Def: "Wall", Category: policy.ThingBuilding, Flags: policy.FlagEdifice | policy.FlagHoldsRoof, ID: id}}}
	}
	colony := observation.ColonyProjection{Bounds: policy.Bounds{Width: n, Height: n}, RoofSupport: 6}
	for z := int32(0); z < n; z++ {
		for x := int32(0); x < n; x++ {
			cell := policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Roofed: domain.Known(false)}
			switch {
			case z == 20 && x == 19:
				cell = holder(2, x)
			case z == 20 && x == 21:
				cell = holder(1, x)
			case z == 20 && x == 20:
				cell.Roofed, cell.Roof = domain.Known(true), domain.Known("RoofConstructed")
			}
			colony.Cells = append(colony.Cells, cell)
		}
	}
	row := func(id string, x int32) policy.ClearanceTarget {
		cell := domain.Cell{X: x, Z: 20}
		return policy.ClearanceTarget{EntityID: id, DefName: "Wall", Minimum: cell, Maximum: cell, Deconstructible: true, Salvage: &policy.SalvageEvidence{Safe: domain.Known(true)}}
	}
	rows := []policy.ClearanceTarget{row("Thing_Wall1", 21), row("Thing_Wall2", 19)}
	queue := &policy.RecoveryQueue{Admitted: "Thing_Wall1", Entries: []policy.RecoveryEntry{
		{ID: "Thing_Wall1", Kind: policy.RemoteSalvage, Status: policy.RecoveryAdmitted},
		{ID: "Thing_Wall2", Kind: policy.RemoteSalvage, Status: policy.RecoveryQueued},
	}}
	planner := &RoundsClearancePlanner{native: recoveryRoofCatalog{}}
	ctx := context.Background()
	step := planner.recoveryStep(ctx, nil, queue, rows, colony)
	if len(step.Targets) != 0 || !slices.Contains(step.Roof, domain.Cell{X: 20, Z: 20}) {
		t.Fatalf("want the roof off first, got %+v", step)
	}
	prefix, actions, err := recoveryStepMethod(domain.MintPlanID(), step)
	if err != nil || len(actions) != 1 || !strings.HasPrefix(prefix, "roof-off-20-20-") {
		t.Fatal(prefix, actions, err)
	}
	for i := range colony.Cells {
		if colony.Cells[i].Cell == (domain.Cell{X: 20, Z: 20}) {
			colony.Cells[i].Roofed = domain.Known(false)
		}
	}
	step = planner.recoveryStep(ctx, nil, queue, rows, colony)
	if len(step.Roof) != 0 || len(step.Targets) != 2 || step.Targets[0].EntityID != "Thing_Wall1" {
		t.Fatalf("want both removals once the roof is down, got %+v", step)
	}
	prefix, actions, err = recoveryStepMethod(domain.MintPlanID(), step)
	if err != nil || len(actions) != 2 || prefix != "deconstruct-Thing_Wall1-x2-" {
		t.Fatal(prefix, actions, err)
	}
	if _, ok := actions[0].Deconstruction(); !ok {
		t.Fatal("not a deconstruction", actions[0])
	}
	// Standing designations still participate in the joint roof check, but
	// they do not need another write once that check admits the full batch.
	rows[0].Designated, rows[1].Designated = true, true
	for i := 0; i < 12; i++ {
		step = planner.recoveryStep(ctx, nil, queue, rows, colony)
		if len(step.Targets) != 2 {
			t.Fatal("pending buildings disappeared from safety planning", step)
		}
		if pending := pendingClearanceStep(step); len(pending.Targets) != 0 {
			t.Fatal("standing designations were reissued", pending)
		}
	}
	rows[1].Designated = false
	step = pendingClearanceStep(planner.recoveryStep(ctx, nil, queue, rows, colony))
	if len(step.Targets) != 1 || step.Targets[0].EntityID != rows[1].EntityID {
		t.Fatal("a missing designation needs a fresh write", step)
	}
	// Roof rules the source cannot serve plan nothing.
	if step = (&RoundsClearancePlanner{native: recoveryRoofCatalog{empty: true}}).recoveryStep(ctx, nil, queue, rows, colony); len(step.Targets) != 0 || len(step.Roof) != 0 {
		t.Fatalf("a failed roof read still planned %+v", step)
	}
}

func TestClearancePendingWritesKeepTheSelectedPhase(t *testing.T) {
	step := policy.GroundStep{Phase: policy.GroundFurniture,
		Targets: []policy.ClearanceTarget{{EntityID: "pending", Designated: true}, {EntityID: "new"}},
		Floors:  []policy.ClearanceFloor{{Cell: domain.Cell{X: 1}, Designated: true}, {Cell: domain.Cell{X: 2}}},
		Cleared: []policy.Rectangle{{Width: 5, Height: 5}},
	}
	got := pendingClearanceStep(step)
	if got.Phase != step.Phase || !slices.Equal(got.Cleared, step.Cleared) || len(got.Targets) != 1 || got.Targets[0].EntityID != "new" || len(got.Floors) != 1 || got.Floors[0].Cell.X != 2 {
		t.Fatal(got)
	}
	if len(step.Targets) != 2 || len(step.Floors) != 2 {
		t.Fatal("filter mutated the census")
	}
}

// Chunks are hauls: with no deconstructible target and a pending chunk (in
// Home, allowed, unstored, no store will take it), the planner makes no zone
// (the materials yard is the store) and refuses no_space; a chunk ordinary
// hauling already has a destination for is no deficit at all.
func TestRoundsClearanceRefusesPendingChunksWithoutAStore(t *testing.T) {
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
	if review, err = reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoundsClearancePlanner(reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict != noSpace("chunk_dump_site") || len(source.previews) != 0 {
		t.Fatal(result, err, source.previews)
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
	_, err := reviewer.Step(ctx)
	if err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoundsClearancePlanner(reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
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
	prefix, actions, err := groundStepMethod("plan", policy.GroundStep{Ground: ground[0], Phase: policy.GroundWalls, Targets: []policy.ClearanceTarget{wall}, Roof: []domain.Cell{{X: 12, Z: 12}}, Cleared: ground})
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
	prefix, actions, err = groundStepMethod("plan", policy.GroundStep{Ground: ground[0], Phase: policy.GroundFurniture, Targets: []policy.ClearanceTarget{bed}})
	if err != nil || prefix != "deconstruct-Bed1-" || len(actions) != 1 {
		t.Fatal(prefix, actions, err)
	}
	if cut, ok := actions[0].Deconstruction(); !ok || cut.ClearedGround() != nil {
		t.Fatalf("furniture is a plain deconstruction: %+v", cut)
	}

	table := policy.ClearanceTarget{EntityID: "Table1", DefName: "Table2x2c", Minimum: domain.Cell{X: 11, Z: 10}, Player: true, Packable: true}
	prefix, actions, err = groundStepMethod("plan", policy.GroundStep{Ground: ground[0], Phase: policy.GroundPack, Targets: []policy.ClearanceTarget{table, bed}})
	if err != nil || prefix != "pack-Table1-x2-" || len(actions) != 2 {
		t.Fatal(prefix, actions, err)
	}
	if pack, ok := actions[1].UninstallBuilding(); !ok || pack.Thing() != "Bed1" {
		t.Fatalf("packable furniture is one batched uninstall: %v", actions[1].Kind())
	}

	door := policy.ClearanceTarget{EntityID: "Door1", DefName: "Door", Minimum: domain.Cell{X: 9, Z: 10}, Maximum: domain.Cell{X: 9, Z: 10}, Player: true, EnclosesRoom: true}
	prefix, actions, err = groundStepMethod("plan", policy.GroundStep{Ground: ground[0], Phase: policy.GroundDoors, Targets: []policy.ClearanceTarget{door}})
	if err != nil || prefix != "swap-door-Door1-" || len(actions) != 1 {
		t.Fatal(prefix, actions, err)
	}
	if cut, ok := actions[0].Deconstruction(); !ok || !cut.ReplacesWithWall() || cut.ClearedGround() != nil {
		t.Fatalf("ring door is a wall swap without cleared ground: %+v", cut)
	}

	prefix, actions, err = groundStepMethod("plan", policy.GroundStep{Ground: ground[0], Phase: policy.GroundFloors, Floors: []policy.ClearanceFloor{{Cell: domain.Cell{X: 10, Z: 10}, DefName: "WoodPlankFloor"}}})
	if err != nil || prefix != "ground-floors-10-10-x1-" || len(actions) != 1 {
		t.Fatal(prefix, actions, err)
	}
	if floor, ok := actions[0].FloorRemoval(); !ok || floor.Cell() != (domain.Cell{X: 10, Z: 10}) {
		t.Fatalf("floor removal: %v", actions[0].Kind())
	}
}
