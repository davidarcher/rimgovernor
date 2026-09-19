package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// routineShrineNative is the routine fixture plus the shrine census and the
// readiness reads: every colonist armed (even rows ranged at 25.9), a trap
// line the caller sets, and open ground on every other cell of the window.
type routineShrineNative struct {
	*routineBlightNative
	shrines []*o.AncientShrine
	traps   []domain.Cell
	owned   map[string]bool
	reads   []string
}

func (n *routineShrineNative) ReadClaimBuildingTarget(_ context.Context, _ *c.Identity, thing string) (bridge.ClaimBuildingTarget, bridge.Result, error) {
	n.reads = append(n.reads, thing)
	return bridge.ClaimBuildingTarget{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Thing: thing, Token: "claim-" + thing, PlayerOwned: n.owned[thing]}, bridge.Result{}, nil
}

func (n *routineShrineNative) ReadAncientShrines(_ context.Context, _ *c.Identity) (*o.AncientShrinesReply, bridge.Result, error) {
	count := uint64(len(n.shrines))
	return &o.AncientShrinesReply{Outcome: &o.AncientShrinesReply_Observed{Observed: &o.AncientShrinesSnapshot{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Shrines: n.shrines, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(count), Returned: proto.Uint64(count), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}, bridge.Result{}, nil
}
func (n *routineShrineNative) ReadCombatPawns(ctx context.Context, identity *c.Identity, ids []string) (*o.ListPawnsReply, bridge.Result, error) {
	return (&shrineTestNative{}).ReadCombatPawns(ctx, identity, ids)
}
func (n *routineShrineNative) ReadDefenseSite(ctx context.Context, _ *c.Identity, region bridge.CellRect) (bridge.DefenseSite, bridge.Result, error) {
	site := bridge.DefenseSite{Region: region}
	trapped := map[domain.Cell]bool{}
	for _, trap := range n.traps {
		trapped[trap] = true
		site.Cells = append(site.Cells, bridge.DefenseCell{Cell: trap, EdificeDefName: "TrapSpike", PlayerOwned: true})
	}
	for x := region.Min.X; x <= region.Max.X; x++ {
		for z := region.Min.Z; z <= region.Max.Z; z++ {
			cell := domain.Cell{X: x, Z: z}
			if !trapped[cell] {
				site.Cells = append(site.Cells, bridge.DefenseCell{Cell: cell, Walkable: true})
			}
		}
	}
	return site, bridge.Result{}, ctx.Err()
}

func shrineTestRow(id string, sealed bool) *o.AncientShrine {
	cell := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	return &o.AncientShrine{ShrineId: proto.String(id), Room: &o.Rectangle{Minimum: cell(30, 30), Maximum: cell(40, 40)}, Sealed: proto.Bool(sealed), InHome: proto.Bool(true), GuardsKnown: proto.Bool(!sealed),
		BreachWalls: []*o.ShrineBreachWall{{EntityId: proto.String("wall"), DefName: proto.String("Wall"), Cell: cell(30, 35), Outside: cell(29, 35)}}}
}

// A sealed shrine touching Home under a ready gate is one method: an owned
// draft and a move behind the trap line for every drafted defender (one
// colonist stays free for the deconstruct job), then the breach
// deconstruction of the chosen wall depending on all of them. Every hold
// the gate names is journalled and answered breach_held; an opened shrine
// with a guard standing holds guards_alive.
func TestRoutineShrineDraftsBehindTrapsAndBreachesTheWall(t *testing.T) {
	reviewer, db, _, _, native := routineFixture(t)
	v := native.reply.GetObserved()
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	v.Threat = &o.ThreatSection{Outcome: &o.ThreatSection_Observed{Observed: &o.ThreatFacts{RaidPoints: proto.Float64(120), Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	newRow := func(id string) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	}
	native.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{newRow("cutter"), newRow("cutter2")}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(2), Returned: proto.Uint64(2), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}

	source := &routineShrineNative{routineBlightNative: &routineBlightNative{routineNative: native}}
	source.shrines = []*o.AncientShrine{shrineTestRow("shrine", true)}
	reviewer.native = source
	reviewer.methods = domain.Known([]policy.GoalID{policy.ClearAncientShrine})
	ctx := context.Background()

	// No trap line: the review journals the hold and the planner holds.
	review, err := reviewer.Step(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if holds := review.Review.ShrineHolds; len(holds) != 1 || holds[0] != (policy.ShrineHold{Shrine: "shrine", Reason: policy.ShrineHoldNoTraps, Wall: "wall"}) {
		t.Fatal(review.Review.ShrineHolds)
	}
	planner, err := NewRoutineShrinePlanner(reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodHeld || result.Hold != policy.ShrineHoldNoTraps || result.Shrine != "shrine" {
		t.Fatal(result, err, review.Review.Development.Rows)
	}

	// Three traps outside the wall: ready. Two colonists, so one defender
	// is drafted eight cells out along the breach line and one stays free.
	source.traps = []domain.Cell{{X: 27, Z: 35}, {X: 27, Z: 36}, {X: 26, Z: 35}}
	if review, err = reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	if holds := review.Review.ShrineHolds; len(holds) != 1 || holds[0].Reason != policy.ShrineReady {
		t.Fatal(review.Review.ShrineHolds)
	}
	result, err = planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted || result.Shrine != "shrine" {
		t.Fatal(result, err, review.Review.Development.Rows)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 3 {
		t.Fatal(plan, err)
	}
	actions := plan.Spec.Actions()
	draft, ok := actions[0].OwnedDraft()
	if !ok || draft.Pawn() != "cutter" {
		t.Fatal(actions[0])
	}
	move, ok := actions[1].Movement()
	if !ok || move.Pawn() != "cutter" || move.Destination() != (domain.Cell{X: 21, Z: 35}) || move.DraftAction() != actions[0].ID() {
		t.Fatalf("%+v %v", move, ok)
	}
	breach, ok := actions[2].Deconstruction()
	if !ok || !breach.Breach() || breach.Target() != "wall" || breach.Definition() != "Wall" || breach.Cell() != (domain.Cell{X: 30, Z: 35}) {
		t.Fatalf("%+v %v", breach, ok)
	}
	deps := plan.Spec.Dependencies()
	if len(deps) != 2 || deps[0].Action != actions[2].ID() || deps[0].Requires != actions[0].ID() || deps[1].Requires != actions[1].ID() {
		t.Fatal(deps)
	}
	root := reviewer.player.session.State().Snapshot
	scope := root
	scope.Plan = plan.Spec.ID()
	scope.Revision = plan.Spec.Revision()
	if err = db.AuthorizeRoutinePlan(ctx, root, scope); err != nil {
		t.Fatal(err)
	}
	if work, _, err := clockSchedulerWork(plan, scope); err != nil || !work {
		t.Fatal(work, err)
	}
	if next, err := planner.Step(ctx); err != nil || next.Reason != BuildingMethodExistingWork {
		t.Fatal(next, err)
	}

	// The wall fell and a guard stands: still a deficit, held guards_alive.
	opened := shrineTestRow("shrine", false)
	opened.Guards = []*o.ShrineGuard{{EntityId: proto.String("mech"), Kind: o.ShrineGuardKind_SHRINE_GUARD_KIND_MECHANOID, Downed: proto.Bool(false), Dead: proto.Bool(false)}}
	source.shrines = []*o.AncientShrine{opened}
	if review, err = reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	if holds := review.Review.ShrineHolds; len(holds) != 1 || holds[0].Reason != policy.ShrineHoldGuardsAlive {
		t.Fatal(review.Review.ShrineHolds)
	}
	// The breach plan closes (here: cancelled) before the claim is owed.
	for _, action := range plan.Spec.Actions() {
		if _, err := db.Cancel(ctx, plan.Spec.ID(), action.ID()); err != nil {
			t.Fatal(err)
		}
	}
	// Guard down, caskets inside: the filled one is left sealed and the
	// empty ones are claimed in one method under fresh CAS tokens; a casket
	// the target read already shows as the player's is skipped.
	opened.Guards[0].Dead = proto.Bool(true)
	casket := func(id string, x int32, contents, claimed bool) *o.ShrineCasket {
		return &o.ShrineCasket{EntityId: proto.String(id), Cell: &c.Cell{X: proto.Int32(x), Z: proto.Int32(35)}, HitPoints: proto.Uint32(250), MaxHitPoints: proto.Uint32(250), HasContents: proto.Bool(contents), PlayerClaimed: proto.Bool(claimed)}
	}
	opened.Caskets = []*o.ShrineCasket{casket("filled", 33, true, false), casket("empty-b", 34, false, false), casket("empty-a", 35, false, false), casket("stale", 36, false, false), casket("mine", 37, false, true)}
	source.owned = map[string]bool{"stale": true}
	if review, err = reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	holds := review.Review.ShrineHolds
	if len(holds) != 6 || holds[0].Reason != policy.ShrineHoldNotSealed || holds[1] != (policy.ShrineHold{Shrine: "shrine", Reason: policy.CasketLeaveSealed, Casket: "filled"}) || holds[2].Reason != policy.CasketClaim || holds[5] != (policy.ShrineHold{Shrine: "shrine", Reason: policy.CasketClaimed, Casket: "mine"}) {
		t.Fatal(holds)
	}
	result, err = planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted || result.Shrine != "shrine" {
		t.Fatal(result, err, review.Review.Development.Rows)
	}
	if plan, err = db.LoadPlan(ctx, result.Plan); err != nil || len(plan.Progress) != 2 {
		t.Fatal(plan, err)
	}
	actions = plan.Spec.Actions()
	first, ok := actions[0].ClaimBuilding()
	second, ok2 := actions[1].ClaimBuilding()
	if !ok || !ok2 || first.Thing() != "empty-a" || first.BeforeToken() != "claim-empty-a" || second.Thing() != "empty-b" {
		t.Fatal(actions)
	}
	if len(source.reads) != 3 || source.reads[2] != "stale" {
		t.Fatal(source.reads)
	}
	scope = root
	scope.Plan, scope.Revision = plan.Spec.ID(), plan.Spec.Revision()
	if err = db.AuthorizeRoutinePlan(ctx, root, scope); err != nil {
		t.Fatal(err)
	}
	if next, err := planner.Step(ctx); err != nil || next.Reason != BuildingMethodExistingWork {
		t.Fatal(next, err)
	}

	// Everything claimed or filled: no target left, the deficit recovers.
	opened.Caskets = []*o.ShrineCasket{casket("filled", 33, true, false), casket("mine", 37, false, true)}
	if review, err = reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	for _, binding := range review.Review.Goals {
		if binding.Need != policy.ClearAncientShrine {
			continue
		}
		if goal, err := db.LoadGoal(ctx, binding.Goal); err != nil || goal.Goal.Need == domain.NeedDeficit {
			t.Fatal("a cleared shrine is no deficit", goal, err)
		}
	}
}
