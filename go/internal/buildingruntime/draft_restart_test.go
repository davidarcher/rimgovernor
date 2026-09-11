package buildingruntime

import (
	"context"
	"path/filepath"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func TestDraftRestartPreservesPlayerReplacementThroughRealJournalAndExecutor(t *testing.T) {
	for _, terminal := range []bool{false, true} {
		for _, replacement := range []string{"unowned", "new-claim"} {
			name := replacement
			if terminal {
				name += "-terminal"
			}
			t.Run(name, func(t *testing.T) {
				ctx := context.Background()
				path := filepath.Join(t.TempDir(), "draft.sqlite")
				db, err := store.Open(ctx, path)
				if err != nil {
					t.Fatal(err)
				}
				defer func() { db.Close() }()
				boundary, native := draftBoundaryFixture(t)
				identity, err := db.Identity(ctx)
				if err != nil {
					t.Fatal(err)
				}
				session := string(identity)
				boundary.session = session
				native.receipt.Attempt.ControllerSessionId = proto.String(session)
				native.receipt.AuthorizingOwner.ControllerSessionId = proto.String(session)
				draftReceiptJob(native.receipt).DraftOwner = proto.String(session)
				native.progress.Attempt.ControllerSessionId = proto.String(session)
				native.progress.GetCompleted().GetEvidence().GetJob().DraftOwner = proto.String(session)
				native.row.DraftClaim.GetOwned().Owner.ControllerSessionId = proto.String(session)
				if replacement == "unowned" {
					native.row.Drafted = proto.Bool(false)
					native.row.DraftClaim = &n.DraftClaimObservation{State: &n.DraftClaimObservation_Unowned{Unowned: &n.NoOwnedDraftClaim{}}}
				} else {
					native.row.DraftClaim.GetOwned().ClaimId = proto.String("replacement-claim")
				}
				playerState := proto.Clone(native.row)
				p := native.p
				plan, err := domain.NewPlan(p.Snapshot.Plan, p.Snapshot.Revision, []domain.Action{p.Action})
				if err != nil {
					t.Fatal(err)
				}
				if err = db.CreatePlan(ctx, plan); err != nil {
					t.Fatal(err)
				}
				if _, err = db.PrepareDraft(ctx, plan.ID(), p.Action.ID(), store.DraftAdmission{Snapshot: p.Snapshot, Tick: p.Tick, Pawn: "pawn", PawnSnapshotToken: "before"}); err != nil {
					t.Fatal(err)
				}
				if _, err = db.Dispatch(ctx, plan.ID(), p.Action.ID(), p.Snapshot, p.Tick); err != nil {
					t.Fatal(err)
				}
				progress, err := db.RecordDraftReceipt(ctx, plan.ID(), p.Action.ID(), p.Attempt, domain.ReceiptUnknown, domain.Unknown[domain.DraftClaim]())
				if err != nil {
					t.Fatal(err)
				}
				if terminal {
					progress, err = db.ObserveDraft(ctx, plan.ID(), domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Tick: p.Tick, Causality: domain.AfterDispatch, Effect: domain.EffectUnsuccessful, UnsuccessfulReason: domain.NativeFailure}, p.Snapshot, domain.Unknown[domain.DraftClaim]())
					if err != nil {
						t.Fatal(err)
					}
					native.progress.Effect = &r.Progress_Unsuccessful{Unsuccessful: &r.UnsuccessfulEffect{Reason: r.UnsuccessfulReason_UNSUCCESSFUL_REASON_CANCELLED.Enum()}}
				}
				before := progress.View()
				if err = db.Close(); err != nil {
					t.Fatal(err)
				}
				db, err = store.Open(ctx, path)
				if err != nil {
					t.Fatal(err)
				}
				building, _ := newBoundaryFixture(t)
				hands, err := executor.NewWithDraft(db, building, boundary, boundaryClock{}, executor.Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second})
				if err != nil {
					t.Fatal(err)
				}
				if err = hands.Stop(ctx); err != nil {
					t.Fatal(err)
				}
				for range 2 {
					if _, err = hands.CleanupDraft(ctx, plan.ID(), p.Action.ID()); err != nil {
						t.Fatal(err)
					}
				}
				state, err := db.LoadPlan(ctx, plan.ID())
				if err != nil {
					t.Fatal(err)
				}
				after := state.Progress[0].View()
				cleanup, _ := after.DraftCleanup.Value()
				claim, known := cleanup.Claim.Value()
				if cleanup.Stage != domain.DraftSuperseded || !known || claim.Claim != "claim" || string(claim.Session) != session || after.Stage != before.Stage || after.Unresolved != before.Unresolved || after.UnsuccessfulReason != before.UnsuccessfulReason {
					t.Fatalf("historical evidence changed ordinary outcome: before=%+v after=%+v", before, after)
				}
				if terminal && after.Effect != before.Effect {
					t.Fatal("terminal effect changed")
				}
				if native.writes != 0 || native.releases != 0 || native.leases != 0 || !proto.Equal(playerState, native.row) {
					t.Fatal("restart cleanup changed player state or acquired permission")
				}
			})
		}
	}
}
