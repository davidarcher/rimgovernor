package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"path/filepath"
	"testing"
)

func TestRoutineDefenseRequiresConsistentCompletePawnDetails(t *testing.T) {
	t.Parallel()
	for _, change := range []string{"armed", "unarmed", "unknown-equipment", "missing-pawn", "colony-count", "conflicting-downed", "stale-tick", "stale-native"} {
		t.Run(change, func(t *testing.T) {
			r, db, _, _, n := routineFixture(t)
			r.native = &routineMedicalNative{routineNative: n}
			n.reply.GetObserved().ColonistCount = proto.Uint32(1)
			n.reply.GetObserved().WorkerCount = proto.Uint32(1)
			row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("patient"), MapId: proto.Int32(n.reply.GetObserved().Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Issues: []*o.ReadIssue{{Field: proto.String("pawn.snapshot"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_UNSUPPORTED.Enum()}}}}
			snapshot := &o.PawnSnapshot{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Pawns: []*o.PawnState{row}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}
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
				snapshot.Completeness.Matched = proto.Uint64(0)
				snapshot.Completeness.Returned = proto.Uint64(0)
			case "colony-count":
				n.reply.GetObserved().ColonistCount = proto.Uint32(2)
			case "conflicting-downed":
				row.Downed = proto.Bool(true)
			case "stale-tick":
				snapshot.Context.Tick = proto.Int64(snapshot.Context.GetTick() + 1)
			case "stale-native":
				snapshot.Context.NativeGeneration = proto.Uint64(snapshot.Context.GetNativeGeneration() + 1)
			}
			out, err := r.Step(context.Background())
			if change == "stale-tick" || change == "stale-native" {
				if err == nil {
					t.Fatal("mixed observation committed")
				}
				stored, e := db.LoadRoutineReview(context.Background())
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
	for _, kind := range []domain.ActionKind{domain.OwnedDraftAction, domain.MovementAction, domain.RangedAttackAction} {
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
		attack, _ := domain.NewRangedAttack(pawn, "raider", draftAction.ID())
		attackAction, _ := domain.NewRangedAttackAction(domain.ActionID("attack-"+pawn), attack)
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
	// Pawn a: draft dispatched, claimed, then observed superseded (released).
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Plan: plan.ID(), Revision: 1, Native: 2}
	admission := store.DraftAdmission{Snapshot: snapshot, Tick: 10, Pawn: "a", PawnSnapshotToken: "cas"}
	if _, err = journal.PrepareDraft(ctx, plan.ID(), "draft-a", admission); err != nil {
		t.Fatal(err)
	}
	if _, err = journal.Dispatch(ctx, plan.ID(), "draft-a", snapshot, 10); err != nil {
		t.Fatal(err)
	}
	session, _ := journal.Identity(ctx)
	claim := domain.DraftClaim{Action: "draft-a", Attempt: 1, Pawn: "a", Claim: "claim", Session: domain.ControllerSessionID(session), Origin: snapshot}
	if _, err = journal.RecordDraftReceipt(ctx, plan.ID(), "draft-a", 1, domain.ReceiptAccepted, domain.Known(claim)); err != nil {
		t.Fatal(err)
	}
	observed := domain.Observation{Action: "draft-a", Attempt: 1, Snapshot: snapshot, Tick: 10, Causality: domain.AfterDispatch, Effect: domain.EffectCompleted}
	if _, err = journal.ObserveDraft(ctx, plan.ID(), observed, snapshot, domain.Known(claim)); err != nil {
		t.Fatal(err)
	}
	later := snapshot
	later.Native++
	if _, err = journal.ObserveDraftCleanup(ctx, plan.ID(), "draft-a", domain.DraftCleanupObservation{Claim: claim, Observed: later, Tick: 11, Outcome: domain.DraftReleaseSuperseded}); err != nil {
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
