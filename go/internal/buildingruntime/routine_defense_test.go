package buildingruntime

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestRoutineDefenseRequiresConsistentCompletePawnDetails(t *testing.T) {
	if testing.Short() {
		t.Skip("slow: runs under cmd/test -full and nightly")
	}
	t.Parallel()
	for _, change := range []string{"armed", "unarmed", "unknown-equipment", "missing-pawn", "colony-count", "conflicting-downed", "stale-native"} {
		t.Run(change, func(t *testing.T) {
			r, db, _, _, n := routineFixture(t)
			r.native = &routineMedicalNative{routineNative: n}
			n.reply.GetObserved().ColonistCount = proto.Uint32(1)
			n.reply.GetObserved().WorkerCount = proto.Uint32(1)
			row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("patient"), MapId: proto.Int32(n.reply.GetObserved().Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Issues: []*o.ReadIssue{{Field: proto.String("pawn.snapshot"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_UNSUPPORTED.Enum()}}}}
			snapshot := &o.PawnSnapshot{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Pawns: []*o.PawnState{row}, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}
			n.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: snapshot}}
			want := domain.NeedUnknown
			switch change {
			case "armed":
				want = domain.NeedRecovered
			case "unarmed":
				row.Equipment.Armed = proto.Bool(false)
				want = domain.NeedDeficit
			case "unknown-equipment":
				row.Equipment = nil
			case "missing-pawn":
				snapshot.Pawns = nil
			case "colony-count":
				n.reply.GetObserved().ColonistCount = proto.Uint32(2)
			case "conflicting-downed":
				row.Downed = proto.Bool(true)
			case "stale-native":
				snapshot.Context.NativeGeneration = proto.Uint64(snapshot.Context.GetNativeGeneration() + 1)
			}
			out, err := r.Step(context.Background())
			if change == "stale-native" {
				if err == nil {
					t.Fatal("mixed observation committed")
				}
				stored, e := db.LoadRounds(context.Background())
				if e != nil || stored.Revision != 0 {
					t.Fatal(stored, e)
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			for _, assessment := range out.Needs.Assessments {
				if assessment.ID == policy.EnsureBasicDefense && assessment.Need != want {
					t.Fatal(change, assessment, want)
				}
			}
		})
	}
}

func TestHoldTheLineActionKindsAreRoutineExecutable(t *testing.T) {
	t.Parallel()
	// The hold plan drafts, moves to the firing cell and then fires; the
	// worker must be able to dispatch each kind or the plan sits pending
	// forever (M4, #5; movement restored under #68).
	for _, kind := range []domain.ActionKind{domain.OwnedDraftAction, domain.MovementAction, domain.SubdueAction} {
		if !routineExecutableKind(kind) {
			t.Fatalf("%s is not routine-executable", kind)
		}
	}
}

// A hold plan whose owned drafts were released by a Manual cycle keeps its
// unissued moves and attacks open forever otherwise (#5 scenario 2): the
// helper names exactly those, never an action still dispatched natively.
func TestOrphanedDraftDependentsAfterDraftRelease(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	journal, err := store.Open(ctx, filepath.Join(t.TempDir(), "orphans.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	var actions []domain.Action
	var deps []domain.ActionDependency
	for _, pawn := range []domain.PawnID{"a", "b"} {
		draft, _ := domain.NewOwnedDraft(pawn)
		draftAction, _ := domain.NewOwnedDraftAction(domain.ActionID("draft-"+pawn), draft)
		move, _ := domain.NewMovement(pawn, domain.Cell{X: 1, Z: 1}, draftAction.ID())
		moveAction, _ := domain.NewMovementAction(domain.ActionID("move-"+pawn), move)
		attack, _ := domain.NewSubdue(pawn, "raider", draftAction.ID())
		attackAction, _ := domain.NewSubdueAction(domain.ActionID("attack-"+pawn), attack)
		actions = append(actions, draftAction, moveAction, attackAction)
		deps = append(deps, domain.ActionDependency{Action: attackAction.ID(), Requires: moveAction.ID()})
	}
	plan, err := domain.NewPlan("hold", 1, actions, deps...)
	if err != nil {
		t.Fatal(err)
	}
	if err = journal.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	state, err := journal.LoadPlan(ctx, plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	if got := orphanedDraftDependents(state.Spec, state.Progress); len(got) != 0 {
		t.Fatalf("intact drafts orphaned: %v", got)
	}
	// Pawn a: draft dispatched and refused.
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Plan: plan.ID(), Revision: 1, Native: 2}
	if _, err = journal.Prepare(ctx, plan.ID(), "draft-a", snapshot, 10); err != nil {
		t.Fatal(err)
	}
	if _, err = journal.Dispatch(ctx, plan.ID(), "draft-a", snapshot, 10); err != nil {
		t.Fatal(err)
	}
	if _, err = journal.RecordReceipt(ctx, plan.ID(), "draft-a", 1, domain.ReceiptRefused); err != nil {
		t.Fatal(err)
	}
	// Pawn b: draft intact, but its move was cancelled, so only the attack is orphaned.
	if _, err = journal.Cancel(ctx, plan.ID(), "move-b"); err != nil {
		t.Fatal(err)
	}
	if state, err = journal.LoadPlan(ctx, plan.ID()); err != nil {
		t.Fatal(err)
	}
	got := orphanedDraftDependents(state.Spec, state.Progress)
	want := []domain.ActionID{"move-a", "attack-a", "attack-b"}
	if len(got) != len(want) {
		t.Fatalf("orphans %v, want %v", got, want)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("orphans %v, want %v", got, want)
		}
	}
	for _, id := range got {
		if _, err = journal.Cancel(ctx, plan.ID(), id); err != nil {
			t.Fatal(err)
		}
	}
	if state, err = journal.LoadPlan(ctx, plan.ID()); err != nil {
		t.Fatal(err)
	}
	// Only the never-issued draft-b is still open; it carries no orphan.
	for _, p := range state.Progress {
		v := p.View()
		if v.Action != "draft-b" && domain.GoalWorkOpen([]domain.Progress{p}) {
			t.Fatalf("%s still open after cancelling the orphans: %s", v.Action, v.Stage)
		}
	}
}

func TestDefenseTargetsIncludeANearHuntingPredator(t *testing.T) {
	t.Parallel()
	threats := []policy.EmergencyThreat{
		{ID: "raider", Kind: policy.Hostile},
		{ID: "bear", Kind: policy.HuntingPredator, Animal: domain.Known(true), Distance: domain.Known(12.0)},
		{ID: "far-cougar", Kind: policy.HuntingPredator, Animal: domain.Known(true), Distance: domain.Known(policy.DistantThreatCells)},
		{ID: "wolf", Kind: policy.NearbyPredator, Animal: domain.Known(true), Distance: domain.Known(5.0)},
		{ID: "hunter", Kind: policy.IgnoredHunter, Animal: domain.Known(true), Distance: domain.Known(5.0)},
		{ID: "hive", Kind: policy.HostileBuilding, Dead: domain.Known(false), Downed: domain.Known(false), Animal: domain.Known(false), SnapshotToken: "tok", Definition: "Hive", Cells: []domain.Cell{{X: 5, Z: 5}}},
		{ID: "rubble", Kind: policy.HostileBuilding, Dead: domain.Known(true), Downed: domain.Known(false), Animal: domain.Known(false), SnapshotToken: "tok", Definition: "Hive", Cells: []domain.Cell{{X: 5, Z: 5}}},
	}
	got, hunting, buildings := defenseTargets(threats)
	if len(got) != 2 || got[0] != "raider" || got[1] != "bear" || hunting["raider"] || !hunting["bear"] {
		t.Fatal(got, hunting)
	}
	// A standing hostile building is a target apart from the pawn reads; a
	// destroyed one is not.
	if len(buildings) != 1 || buildings[0].ID != "hive" {
		t.Fatal(buildings)
	}
}

// A raid that ends after one squad draft was issued but before the other
// was dispatched leaves that draft prepared and every attack pending. A
// review only settles wholly undispatched plans (#290), so the recovered
// goal could not satisfy and the next raid could never open a fresh epoch
// (#226). The planner settles the unissued work once the goal is recovered.
func TestRecoveredCombatGoalSettlesUndispatchedDraft(t *testing.T) {
	t.Parallel()
	r, db, session, _, n := routineFixture(t)
	ctx := context.Background()
	planner, err := NewRoutineDefensePlanner(r, framed{&equipTestNative{routineNative: n}})
	if err != nil {
		t.Fatal(err)
	}
	snapshot := session.State().Snapshot
	review := func(hostiles int64) store.RoundsResult {
		t.Helper()
		current, err := db.LoadRounds(ctx)
		if err != nil {
			t.Fatal(err)
		}
		facts := policy.RoutineFacts{Workers: domain.Known(1), Wood: domain.Known(int64(100)), Hostiles: domain.Known(hostiles), CriticalPatients: domain.Known(int64(0)), CleanupPawns: domain.Known(false), ColonyNaming: domain.Known(false), ChoiceDialog: domain.Known(false)}
		out, err := db.ReviewRoutine(ctx, store.RoundsRequest{Revision: current.Revision, Current: snapshot, Tick: 7 /* the fixture context tick */, Enabled: true, Policy: policy.DefaultRoutinePolicy(), Facts: facts})
		if err != nil {
			t.Fatal(err)
		}
		return out
	}
	combat := func(out store.RoundsResult) (store.RoutineIncident, bool) {
		t.Helper()
		return out.Review.Incident(policy.ActiveCombat)
	}
	goal, ok := combat(review(1))
	if !ok || goal.Need != domain.NeedDeficit {
		t.Fatal(goal)
	}
	squad := func(suffix string) []domain.Action {
		var actions []domain.Action
		for _, pawn := range []domain.PawnID{"a", "b"} {
			draft, _ := domain.NewOwnedDraft(pawn)
			draftAction, _ := domain.NewOwnedDraftAction(domain.ActionID("draft-"+string(pawn)+suffix), draft)
			attack, _ := domain.NewSubdue(pawn, "raider", draftAction.ID())
			attackAction, _ := domain.NewSubdueAction(domain.ActionID("attack-"+string(pawn)+suffix), attack)
			actions = append(actions, draftAction, attackAction)
		}
		return actions
	}
	plan, err := domain.NewPlan("routine-defense-test", 1, squad(""))
	if err != nil {
		t.Fatal(err)
	}
	if _, err = db.CommitIncidentMethod(ctx, goal.Incident, "squad-test", "", plan); err != nil {
		t.Fatal(err)
	}
	// Pawn a's draft was applied; pawn b's draft was prepared but the raid
	// resolved before dispatch.
	planSnapshot := snapshot
	planSnapshot.Plan = plan.ID()
	if _, err = db.Prepare(ctx, plan.ID(), "draft-a", planSnapshot, 7); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Dispatch(ctx, plan.ID(), "draft-a", planSnapshot, 7); err != nil {
		t.Fatal(err)
	}
	if _, err = db.RecordReceipt(ctx, plan.ID(), "draft-a", 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Prepare(ctx, plan.ID(), "draft-b", planSnapshot, 7); err != nil {
		t.Fatal(err)
	}
	first := goal.Incident
	if goal, ok = combat(review(0)); !ok || goal.Incident != first || goal.Need != domain.NeedRecovered {
		t.Fatal("recovered incident with open work should stay open", goal)
	}
	got, err := planner.Step(ctx)
	if err != nil || got.Verdict != BuildingReasonNoDeficit {
		t.Fatal(got, err)
	}
	state, err := db.LoadPlan(ctx, plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	for _, p := range state.Progress {
		if v := p.View(); v.Action != "draft-a" && v.Stage != domain.Cancelled {
			t.Fatalf("%s is %s after recovery, want cancelled", v.Action, v.Stage)
		}
	}
	if _, ok = combat(review(0)); ok {
		t.Fatal("settled incident did not close")
	}
	// The next raid opens the next incident and the planner no longer
	// reports the stale squad plan as existing work.
	if goal, ok = combat(review(1)); !ok || goal.Incident == first || goal.Need != domain.NeedDeficit {
		t.Fatal(goal)
	}
	if got, err = planner.Step(ctx); err != nil || got.Verdict == BuildingReasonExistingWork {
		t.Fatal(got, err)
	}
	fresh, _ := domain.NewPlan("routine-defense-test-2", 1, squad("-2"))
	if _, err = db.CommitIncidentMethod(ctx, goal.Incident, "squad-test-2", "", fresh); err != nil {
		t.Fatal("second raid refused a fresh method:", err)
	}
}
