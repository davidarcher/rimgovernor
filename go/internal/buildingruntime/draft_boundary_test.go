package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type draftFixtureNative struct {
	p                                                    executor.Placement
	context                                              *c.ObservationContext
	row                                                  *n.PawnState
	receipt                                              *r.Receipt
	progress                                             *r.Progress
	readErr, writeErr, releaseErr                        error
	mutateRelease                                        func(*o.ReleaseOwnedDraftReply)
	leases, writes, reads, lookups, releases, identities int
	lastPre                                              *a.WritePrecondition
	lastPawn                                             *o.EntityPrecondition
	lastRelease                                          *o.ReleaseOwnedDraftRequest
}

func draftBoundaryFixture(t *testing.T) (*DraftBoundary, *draftFixtureNative) {
	t.Helper()
	draft, _ := domain.NewOwnedDraft("pawn")
	action, _ := domain.NewOwnedDraftAction("action", draft)
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Direction: 1, Native: 2}
	p := executor.Placement{Action: action, Snapshot: snapshot, Attempt: 1, Tick: 10}
	ctx := &c.ObservationContext{Identity: boundaryIdentity(snapshot), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(2)}
	owner := &a.Owner{ControllerSessionId: proto.String("session"), PlayerDirection: proto.Uint64(1)}
	ref := &n.SnapshotRef{Context: proto.Clone(ctx).(*c.ObservationContext), EntityId: proto.String("pawn"), Token: proto.String("cas")}
	row := &n.PawnState{Pawn: &n.EntityRef{Id: proto.String("pawn"), Snapshot: ref}, Drafted: proto.Bool(true), DraftClaim: &n.DraftClaimObservation{State: &n.DraftClaimObservation_Owned{Owned: &n.OwnedDraftClaim{ClaimId: proto.String("claim"), Owner: owner, PawnSnapshot: proto.Clone(ref).(*n.SnapshotRef)}}}, Job: &n.JobEvidence{PlayerForced: proto.Bool(false), QueuedJobs: proto.Uint32(0)}}
	job := &r.JobEffect{PawnId: proto.String("pawn"), Drafted: proto.Bool(true), Verified: proto.Bool(true), Issued: proto.Bool(true), DraftOwner: proto.String("session"), DraftClaimId: proto.String("claim"), ResultingSnapshotToken: proto.String("cas")}
	effect := &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: job}}
	key := &c.AttemptKey{ControllerSessionId: proto.String("session"), ActionId: proto.String("action"), AttemptId: proto.Uint64(1)}
	f := &draftFixtureNative{p: p, context: ctx, row: row, receipt: &r.Receipt{Attempt: key, AdmittedContext: proto.Clone(ctx).(*c.ObservationContext), AuthorizingOwner: proto.Clone(owner).(*a.Owner), Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: effect}}}, progress: &r.Progress{Attempt: proto.Clone(key).(*c.AttemptKey), Context: proto.Clone(ctx).(*c.ObservationContext), CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: proto.Clone(effect).(*r.EffectEvidence)}}}}
	b, err := NewDraftBoundary(f, f, f, f, boundaryClock{}, "session")
	if err != nil {
		t.Fatal(err)
	}
	return b, f
}
func (f *draftFixtureNative) Identity(context.Context) (*l.IdentityReply, bridge.Result, error) {
	f.identities++
	return &l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: proto.Clone(f.context).(*c.ObservationContext)}}}, bridge.Result{}, f.readErr
}
func (f *draftFixtureNative) ReadPawns(_ context.Context, id *c.Identity, ids []string) (*n.ListPawnsReply, bridge.Result, error) {
	f.reads++
	if !proto.Equal(id, f.context.Identity) || len(ids) != 1 || ids[0] != "pawn" {
		return nil, bridge.Result{}, executor.ErrEvidence
	}
	var rows []*n.PawnState
	if f.row != nil {
		rows = []*n.PawnState{proto.Clone(f.row).(*n.PawnState)}
	}
	return &n.ListPawnsReply{Outcome: &n.ListPawnsReply_Observed{Observed: &n.PawnSnapshot{Context: proto.Clone(f.context).(*c.ObservationContext), Pawns: rows, Completeness: &n.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(uint64(len(rows))), Returned: proto.Uint64(uint64(len(rows))), Filtered: proto.Uint64(10), Unreadable: proto.Uint64(0)}}}}, bridge.Result{}, f.readErr
}
func (f *draftFixtureNative) ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	return bridge.EmergencyObservation{Context: proto.Clone(f.context).(*c.ObservationContext), Facts: policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true)}}, bridge.Result{}, f.readErr
}
func (f *draftFixtureNative) PreviewDraft(_ context.Context, _ *c.Identity, pawn *o.EntityPrecondition) (*o.PreviewReply, bridge.Result, error) {
	f.lastPawn = proto.Clone(pawn).(*o.EntityPrecondition)
	return &o.PreviewReply{Outcome: &o.PreviewReply_Evaluated{Evaluated: &o.PreviewEvaluation{Context: proto.Clone(f.context).(*c.ObservationContext), Accepted: proto.Bool(true), Projected: &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: &r.JobEffect{PawnId: proto.String("pawn"), CanTry: proto.Bool(true)}}}}}}, bridge.Result{}, f.readErr
}
func (f *draftFixtureNative) LookupDraftAttempt(_ context.Context, attempt bridge.DraftAttempt) (*r.LookupReply, bridge.Result, error) {
	f.lookups++
	if attempt.Attempt.GetActionId() != string(f.p.Action.ID()) || attempt.NativeGeneration != uint64(f.p.Snapshot.Native) || attempt.Owner.GetPlayerDirection() != uint64(f.p.Snapshot.Direction) {
		return nil, bridge.Result{}, executor.ErrEvidence
	}
	if f.receipt == nil {
		return &r.LookupReply{Outcome: &r.LookupReply_Unknown{Unknown: &r.UnknownAttempt{Context: proto.Clone(f.context).(*c.ObservationContext)}}}, bridge.Result{}, nil
	}
	return &r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: proto.Clone(f.receipt).(*r.Receipt)}}, bridge.Result{}, f.readErr
}
func (f *draftFixtureNative) ObserveDraftProgress(context.Context, bridge.DraftAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
	return &r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: proto.Clone(f.progress).(*r.Progress)}}, bridge.Result{}, f.readErr
}
func (f *draftFixtureNative) Lease(domain.GenerationSnapshot) (string, error) {
	f.leases++
	return "lease", nil
}
func (f *draftFixtureNative) DraftPawn(_ context.Context, pre *a.WritePrecondition, _ *a.Owner, pawn *o.EntityPrecondition) (*o.ExecuteReply, bridge.Result, error) {
	f.writes++
	f.lastPre = proto.Clone(pre).(*a.WritePrecondition)
	f.lastPawn = proto.Clone(pawn).(*o.EntityPrecondition)
	return &o.ExecuteReply{Outcome: &o.ExecuteReply_Receipt{Receipt: proto.Clone(f.receipt).(*r.Receipt)}}, bridge.Result{}, f.writeErr
}
func (f *draftFixtureNative) ReleaseOwnedDraft(_ context.Context, q *o.ReleaseOwnedDraftRequest) (*o.ReleaseOwnedDraftReply, bridge.Result, error) {
	f.releases++
	f.lastRelease = proto.Clone(q).(*o.ReleaseOwnedDraftRequest)
	reply := &o.ReleaseOwnedDraftReply{Outcome: &o.ReleaseOwnedDraftReply_Released{Released: &o.DraftRelease{Request: proto.Clone(q).(*o.ReleaseOwnedDraftRequest), Context: proto.Clone(f.context).(*c.ObservationContext), Observed: &r.JobEffect{PawnId: q.Pawn.EntityId, Drafted: proto.Bool(false), Verified: proto.Bool(true), Issued: proto.Bool(true), DraftOwner: q.OriginalOwner.ControllerSessionId, DraftClaimId: q.ExpectedClaimId, ResultingSnapshotToken: proto.String("after")}}}}
	if f.mutateRelease != nil {
		f.mutateRelease(reply)
	}
	return reply, bridge.Result{}, f.releaseErr
}
func draftKnownClaim(f *draftFixtureNative) domain.DraftClaim {
	return domain.DraftClaim{Action: f.p.Action.ID(), Attempt: 1, Pawn: "pawn", Claim: "claim", Session: "session", Origin: f.p.Snapshot}
}

func TestDraftBoundaryInspectAndExactDispatch(t *testing.T) {
	b, f := draftBoundaryFixture(t)
	f.row.Drafted = proto.Bool(false)
	f.row.DraftClaim = &n.DraftClaimObservation{State: &n.DraftClaimObservation_Unowned{Unowned: &n.NoOwnedDraftClaim{}}}
	v, err := b.InspectDraft(context.Background(), executor.Target{Action: f.p.Action, Snapshot: f.p.Snapshot})
	if err != nil || v.PawnSnapshotToken != "cas" || v.Pawn.Unowned != domain.Known(true) || f.leases != 0 {
		t.Fatal(v, err)
	}
	b, f = draftBoundaryFixture(t)
	receipt, err := b.Draft(context.Background(), executor.DraftDispatch{Attempt: f.p, PawnSnapshotToken: "admitted-token"})
	claim, known := receipt.Claim.Value()
	if err != nil || receipt.Receipt.Kind != domain.ReceiptAccepted || !known || claim != draftKnownClaim(f) || f.writes != 1 || f.leases != 1 || f.lastPawn.GetExpectedSnapshotToken() != "admitted-token" {
		t.Fatal(receipt, err)
	}
}
func TestDraftBoundaryOriginalAttemptAndFreshOwnerRequired(t *testing.T) {
	for _, kind := range []string{"valid", "lost-receipt", "foreign-direction", "different-claim", "missing-row", "wrong-attempt", "unknown-progress", "missing-owner"} {
		t.Run(kind, func(t *testing.T) {
			b, f := draftBoundaryFixture(t)
			switch kind {
			case "lost-receipt":
				f.receipt = nil
			case "foreign-direction":
				f.row.DraftClaim.GetOwned().Owner.PlayerDirection = proto.Uint64(2)
			case "different-claim":
				f.row.DraftClaim.GetOwned().ClaimId = proto.String("other")
			case "missing-row":
				f.row = nil
			case "wrong-attempt":
				f.progress.Attempt.AttemptId = proto.Uint64(2)
			case "unknown-progress":
				f.receipt = nil
				f.progress.Effect = &r.Progress_Unknown{Unknown: &r.UnknownEffect{}}
			case "missing-owner":
				f.row.DraftClaim.GetOwned().Owner = nil
			}
			out, err := b.ObserveDraft(context.Background(), f.p, f.p.Snapshot)
			_, known := out.Claim.Value()
			if kind == "valid" || kind == "lost-receipt" {
				if err != nil || !known || out.Observation.Effect != domain.EffectCompleted {
					t.Fatal(out, err)
				}
			} else if kind == "foreign-direction" || kind == "different-claim" {
				if err != nil || !known || out.Observation.Effect != domain.EffectUnknown {
					t.Fatal(out, err)
				}
			} else if kind == "unknown-progress" {
				if err != nil || known || out.Observation.Effect != domain.EffectUnknown {
					t.Fatal(out, err)
				}
			} else if err == nil {
				t.Fatal("mismatch accepted", out)
			}
			if f.leases != 0 || f.writes != 0 || f.releases != 0 {
				t.Fatal("read mutated")
			}
		})
	}
}
func TestDraftCleanupInspectionArms(t *testing.T) {
	for _, kind := range []string{"release", "unowned", "foreign-owner", "world", "unknown", "missing-row", "unavailable"} {
		t.Run(kind, func(t *testing.T) {
			b, f := draftBoundaryFixture(t)
			cleanup := domain.DraftCleanup{Stage: domain.DraftCleanupRequired, Claim: domain.Known(draftKnownClaim(f))}
			switch kind {
			case "unowned":
				f.row.DraftClaim = &n.DraftClaimObservation{State: &n.DraftClaimObservation_Unowned{Unowned: &n.NoOwnedDraftClaim{}}}
			case "foreign-owner":
				f.row.DraftClaim.GetOwned().Owner.PlayerDirection = proto.Uint64(2)
			case "world":
				f.context.Identity.LoadToken = proto.String("replacement")
				f.context.NativeGeneration = nil
				cleanup = domain.DraftCleanup{Stage: domain.DraftAwaitingClaim}
			case "unknown":
				cleanup = domain.DraftCleanup{Stage: domain.DraftAwaitingClaim}
			case "missing-row":
				f.row = nil
			case "unavailable":
				f.row.DraftClaim = &n.DraftClaimObservation{State: &n.DraftClaimObservation_Unavailable{Unavailable: &c.Unavailable{}}}
			}
			out, err := b.InspectDraftCleanup(context.Background(), f.p, cleanup)
			if kind == "missing-row" || kind == "unavailable" {
				if err == nil {
					t.Fatal("missing evidence accepted")
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			arms := 0
			for _, ok := range []bool{out.Request != nil, out.Reconcile != nil, out.ScopeSupersession != nil, out.Supersession != nil} {
				if ok {
					arms++
				}
			}
			if arms != 1 {
				t.Fatal(out)
			}
			if kind == "release" && (out.Request == nil || out.Request.PawnSnapshotToken != "cas") {
				t.Fatal(out)
			}
			if (kind == "unowned" || kind == "foreign-owner") && out.Supersession == nil {
				t.Fatal(out)
			}
			if kind == "world" && (out.ScopeSupersession == nil || f.reads != 0 || f.lookups != 0) {
				t.Fatal(out)
			}
			if kind == "unknown" && out.Reconcile == nil {
				t.Fatal(out)
			}
			if f.leases != 0 || f.writes != 0 || f.releases != 0 {
				t.Fatal("cleanup inspection acquired or wrote")
			}
		})
	}
}
func TestDraftReleaseExactRequestAndNoRetry(t *testing.T) {
	for _, kind := range []string{"released", "already", "lost", "wrong-token", "wrong-owner", "wrong-world", "old-generation"} {
		t.Run(kind, func(t *testing.T) {
			b, f := draftBoundaryFixture(t)
			release := domain.DraftRelease{Sequence: 1, Request: domain.DraftReleaseRequest{Claim: draftKnownClaim(f), PawnSnapshotToken: "persisted-old-token", Observed: f.p.Snapshot, Tick: 10}}
			switch kind {
			case "lost":
				f.releaseErr = errors.New("lost reply")
			case "already":
				f.mutateRelease = func(reply *o.ReleaseOwnedDraftReply) {
					v := reply.GetReleased()
					v.Observed.Issued = proto.Bool(false)
					reply.Outcome = &o.ReleaseOwnedDraftReply_AlreadyReleased{AlreadyReleased: v}
				}
			case "wrong-token":
				f.mutateRelease = func(reply *o.ReleaseOwnedDraftReply) {
					reply.GetReleased().Request.Pawn.ExpectedSnapshotToken = proto.String("other")
				}
			case "wrong-owner":
				f.mutateRelease = func(reply *o.ReleaseOwnedDraftReply) {
					reply.GetReleased().Observed.DraftOwner = proto.String("foreign")
				}
			case "wrong-world":
				f.context.Identity.LoadToken = proto.String("new")
			case "old-generation":
				f.context.NativeGeneration = proto.Uint64(1)
			}
			out, err := b.ReleaseDraft(context.Background(), release)
			if kind == "released" || kind == "already" {
				if err != nil || out.Outcome != domain.DraftReleaseConfirmed {
					t.Fatal(out, err)
				}
			} else if err == nil || out.Outcome != domain.DraftReleaseUncertain {
				t.Fatal(out, err)
			}
			if f.releases != 1 || f.leases != 0 || f.writes != 0 || f.lastRelease.Pawn.GetExpectedSnapshotToken() != "persisted-old-token" {
				t.Fatal("retry or lease")
			}
		})
	}
}
func TestDraftBoundaryCancelledBeforeWrites(t *testing.T) {
	b, f := draftBoundaryFixture(t)
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := b.Draft(ctx, executor.DraftDispatch{Attempt: f.p, PawnSnapshotToken: "cas"}); !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
	if f.writes != 0 || f.leases != 0 {
		t.Fatal("cancelled write")
	}
}

func TestDraftObservationUsesMovingInspectionTickWithOriginalReceipt(t *testing.T) {
	b, f := draftBoundaryFixture(t)
	// Admission remains tick 10 while persisted progress advances its lower bound.
	f.p.Tick = 15
	f.context.Tick = proto.Int64(16)
	f.progress.Context.Tick = proto.Int64(16)
	f.row.Pawn.Snapshot.Context = proto.Clone(f.context).(*c.ObservationContext)
	f.row.DraftClaim.GetOwned().PawnSnapshot = proto.Clone(f.row.Pawn.Snapshot).(*n.SnapshotRef)
	out, err := b.ObserveDraft(context.Background(), f.p, f.p.Snapshot)
	if err != nil || out.Observation.Tick != 16 || out.Observation.Effect != domain.EffectCompleted {
		t.Fatal(out, err)
	}
}

func TestDraftCleanupAfterRevocationAndLostRelease(t *testing.T) {
	b, f := draftBoundaryFixture(t)
	claim := draftKnownClaim(f)
	f.context.NativeGeneration = proto.Uint64(3)
	f.row.Pawn.Snapshot.Context = proto.Clone(f.context).(*c.ObservationContext)
	f.row.DraftClaim.GetOwned().PawnSnapshot = proto.Clone(f.row.Pawn.Snapshot).(*n.SnapshotRef)
	inspected, err := b.InspectDraftCleanup(context.Background(), f.p, domain.DraftCleanup{Stage: domain.DraftCleanupRequired, Claim: domain.Known(claim)})
	if err != nil || inspected.Request == nil || inspected.Request.Observed.Native != 3 || inspected.Request.Claim.Origin.Native != 2 {
		t.Fatal(inspected, err)
	}
	release := domain.DraftRelease{Request: *inspected.Request, Sequence: 1}
	f.releaseErr = errors.New("lost native reply")
	out, err := b.ReleaseDraft(context.Background(), release)
	if err == nil || out.Outcome != domain.DraftReleaseUncertain {
		t.Fatal(out, err)
	}
	f.row.DraftClaim = &n.DraftClaimObservation{State: &n.DraftClaimObservation_Unowned{Unowned: &n.NoOwnedDraftClaim{}}}
	f.row.Drafted = proto.Bool(false)
	after, err := b.InspectDraftCleanup(context.Background(), f.p, domain.DraftCleanup{Stage: domain.DraftCleanupUncertain, Claim: domain.Known(claim), Release: domain.Known(release)})
	if err != nil || after.Supersession == nil || after.Request != nil || after.Supersession.Outcome != domain.DraftReleaseSuperseded || f.releases != 1 || f.leases != 0 {
		t.Fatal(after, err)
	}
}

func TestDraftReceiptRefusalDoesNotClaimOwnership(t *testing.T) {
	for _, conflict := range []bool{false, true} {
		b, f := draftBoundaryFixture(t)
		code := c.FailureCode_FAILURE_CODE_INVALID_REQUEST
		if conflict {
			code = c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT
		}
		f.writeErr = &bridge.NativeFailure{Value: &c.Failure{Code: code.Enum()}}
		out, err := b.Draft(context.Background(), executor.DraftDispatch{Attempt: f.p, PawnSnapshotToken: "cas"})
		if conflict {
			if err == nil || out.Receipt.Kind != domain.ReceiptUnknown {
				t.Fatal(out, err)
			}
		} else if err != nil || out.Receipt.Kind != domain.ReceiptRefused {
			t.Fatal(out, err)
		}
		if _, known := out.Claim.Value(); known {
			t.Fatal("refusal invented claim")
		}
	}
}
