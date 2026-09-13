package draft

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func TestDraftBoundaryInspectAndExactDispatch(t *testing.T) {
	t.Parallel()
	b, f := NewFixture(t)
	f.Row.Drafted = proto.Bool(false)
	f.Row.DraftClaim = &n.DraftClaimObservation{State: &n.DraftClaimObservation_Unowned{Unowned: &n.NoOwnedDraftClaim{}}}
	v, err := b.InspectDraft(context.Background(), executor.Target{Action: f.P.Action, Snapshot: f.P.Snapshot})
	if err != nil || v.PawnSnapshotToken != "cas" || v.Pawn.Unowned != domain.Known(true) || f.Leases != 0 {
		t.Fatal(v, err)
	}
	b, f = NewFixture(t)
	receipt, err := b.Draft(context.Background(), executor.DraftDispatch{Attempt: f.P, PawnSnapshotToken: "admitted-token"})
	claim, known := receipt.Claim.Value()
	if err != nil || receipt.Receipt.Kind != domain.ReceiptAccepted || !known || claim != KnownClaim(f) || f.Writes != 1 || f.Leases != 1 || f.LastPawn.GetExpectedSnapshotToken() != "admitted-token" {
		t.Fatal(receipt, err)
	}
}
func TestDraftBoundaryOriginalAttemptAndFreshOwnerRequired(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"valid", "lost-receipt", "foreign-direction", "different-claim", "missing-row", "wrong-attempt", "unknown-progress", "missing-owner"} {
		t.Run(kind, func(t *testing.T) {
			b, f := NewFixture(t)
			switch kind {
			case "lost-receipt":
				f.Receipt = nil
			case "foreign-direction":
				f.Row.DraftClaim.GetOwned().Owner.PlayerDirection = proto.Uint64(2)
			case "different-claim":
				f.Row.DraftClaim.GetOwned().ClaimId = proto.String("other")
			case "missing-row":
				f.Row = nil
			case "wrong-attempt":
				f.Progress.Attempt.AttemptId = proto.Uint64(2)
			case "unknown-progress":
				f.Receipt = nil
				f.Progress.Effect = &r.Progress_Unknown{Unknown: &r.UnknownEffect{}}
			case "missing-owner":
				f.Row.DraftClaim.GetOwned().Owner = nil
			}
			out, err := b.ObserveDraft(context.Background(), f.P, f.P.Snapshot)
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
			if f.Leases != 0 || f.Writes != 0 || f.Releases != 0 {
				t.Fatal("read mutated")
			}
		})
	}
}
func TestDraftCleanupInspectionArms(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"release", "unowned", "foreign-owner", "world", "unknown", "missing-row", "unavailable"} {
		t.Run(kind, func(t *testing.T) {
			b, f := NewFixture(t)
			cleanup := domain.DraftCleanup{Stage: domain.DraftCleanupRequired, Claim: domain.Known(KnownClaim(f))}
			switch kind {
			case "unowned":
				f.Row.DraftClaim = &n.DraftClaimObservation{State: &n.DraftClaimObservation_Unowned{Unowned: &n.NoOwnedDraftClaim{}}}
			case "foreign-owner":
				f.Row.DraftClaim.GetOwned().Owner.PlayerDirection = proto.Uint64(2)
			case "world":
				f.Ctx.Identity.LoadToken = proto.String("replacement")
				f.Ctx.NativeGeneration = nil
				cleanup = domain.DraftCleanup{Stage: domain.DraftAwaitingClaim}
			case "unknown":
				cleanup = domain.DraftCleanup{Stage: domain.DraftAwaitingClaim}
			case "missing-row":
				f.Row = nil
			case "unavailable":
				f.Row.DraftClaim = &n.DraftClaimObservation{State: &n.DraftClaimObservation_Unavailable{Unavailable: &c.Unavailable{}}}
			}
			out, err := b.InspectDraftCleanup(context.Background(), f.P, cleanup)
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
			if kind == "world" && (out.ScopeSupersession == nil || f.Reads != 0 || f.Lookups != 0) {
				t.Fatal(out)
			}
			if kind == "unknown" && out.Reconcile == nil {
				t.Fatal(out)
			}
			if f.Leases != 0 || f.Writes != 0 || f.Releases != 0 {
				t.Fatal("cleanup inspection acquired or wrote")
			}
		})
	}
}
func TestDraftReleaseExactRequestAndNoRetry(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"released", "already", "lost", "wrong-token", "wrong-owner", "wrong-world", "old-generation"} {
		t.Run(kind, func(t *testing.T) {
			b, f := NewFixture(t)
			release := domain.DraftRelease{Sequence: 1, Request: domain.DraftReleaseRequest{Claim: KnownClaim(f), PawnSnapshotToken: "persisted-old-token", Observed: f.P.Snapshot, Tick: 10}}
			switch kind {
			case "lost":
				f.ReleaseErr = errors.New("lost reply")
			case "already":
				f.MutateRelease = func(reply *o.ReleaseOwnedDraftReply) {
					v := reply.GetReleased()
					v.Observed.Issued = proto.Bool(false)
					reply.Outcome = &o.ReleaseOwnedDraftReply_AlreadyReleased{AlreadyReleased: v}
				}
			case "wrong-token":
				f.MutateRelease = func(reply *o.ReleaseOwnedDraftReply) {
					reply.GetReleased().Request.Pawn.ExpectedSnapshotToken = proto.String("other")
				}
			case "wrong-owner":
				f.MutateRelease = func(reply *o.ReleaseOwnedDraftReply) {
					reply.GetReleased().Observed.DraftOwner = proto.String("foreign")
				}
			case "wrong-world":
				f.Ctx.Identity.LoadToken = proto.String("new")
			case "old-generation":
				f.Ctx.NativeGeneration = proto.Uint64(1)
			}
			out, err := b.ReleaseDraft(context.Background(), release)
			if kind == "released" || kind == "already" {
				if err != nil || out.Outcome != domain.DraftReleaseConfirmed {
					t.Fatal(out, err)
				}
			} else if err == nil || out.Outcome != domain.DraftReleaseUncertain {
				t.Fatal(out, err)
			}
			if f.Releases != 1 || f.Leases != 0 || f.Writes != 0 || f.LastRelease.Pawn.GetExpectedSnapshotToken() != "persisted-old-token" {
				t.Fatal("retry or lease")
			}
		})
	}
}
func TestDraftBoundaryCancelledBeforeWrites(t *testing.T) {
	t.Parallel()
	b, f := NewFixture(t)
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := b.Draft(ctx, executor.DraftDispatch{Attempt: f.P, PawnSnapshotToken: "cas"}); !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
	if f.Writes != 0 || f.Leases != 0 {
		t.Fatal("cancelled write")
	}
}

func TestDraftObservationUsesMovingInspectionTickWithOriginalReceipt(t *testing.T) {
	t.Parallel()
	b, f := NewFixture(t)
	// Admission remains tick 10 while persisted progress advances its lower bound.
	f.P.Tick = 15
	f.Ctx.Tick = proto.Int64(16)
	f.Progress.Context.Tick = proto.Int64(16)
	f.Row.Pawn.Snapshot.Context = proto.Clone(f.Ctx).(*c.ObservationContext)
	f.Row.DraftClaim.GetOwned().PawnSnapshot = proto.Clone(f.Row.Pawn.Snapshot).(*n.SnapshotRef)
	out, err := b.ObserveDraft(context.Background(), f.P, f.P.Snapshot)
	if err != nil || out.Observation.Tick != 16 || out.Observation.Effect != domain.EffectCompleted {
		t.Fatal(out, err)
	}
}

func TestDraftCleanupAfterRevocationAndLostRelease(t *testing.T) {
	t.Parallel()
	b, f := NewFixture(t)
	claim := KnownClaim(f)
	f.Ctx.NativeGeneration = proto.Uint64(3)
	f.Row.Pawn.Snapshot.Context = proto.Clone(f.Ctx).(*c.ObservationContext)
	f.Row.DraftClaim.GetOwned().PawnSnapshot = proto.Clone(f.Row.Pawn.Snapshot).(*n.SnapshotRef)
	inspected, err := b.InspectDraftCleanup(context.Background(), f.P, domain.DraftCleanup{Stage: domain.DraftCleanupRequired, Claim: domain.Known(claim)})
	if err != nil || inspected.Request == nil || inspected.Request.Observed.Native != 3 || inspected.Request.Claim.Origin.Native != 2 {
		t.Fatal(inspected, err)
	}
	release := domain.DraftRelease{Request: *inspected.Request, Sequence: 1}
	f.ReleaseErr = errors.New("lost native reply")
	out, err := b.ReleaseDraft(context.Background(), release)
	if err == nil || out.Outcome != domain.DraftReleaseUncertain {
		t.Fatal(out, err)
	}
	f.Row.DraftClaim = &n.DraftClaimObservation{State: &n.DraftClaimObservation_Unowned{Unowned: &n.NoOwnedDraftClaim{}}}
	f.Row.Drafted = proto.Bool(false)
	after, err := b.InspectDraftCleanup(context.Background(), f.P, domain.DraftCleanup{Stage: domain.DraftCleanupUncertain, Claim: domain.Known(claim), Release: domain.Known(release)})
	if err != nil || after.Supersession == nil || after.Request != nil || after.Supersession.Outcome != domain.DraftReleaseSuperseded || f.Releases != 1 || f.Leases != 0 {
		t.Fatal(after, err)
	}
}

func TestDraftReceiptRefusalDoesNotClaimOwnership(t *testing.T) {
	t.Parallel()
	for _, conflict := range []bool{false, true} {
		b, f := NewFixture(t)
		code := c.FailureCode_FAILURE_CODE_INVALID_REQUEST
		if conflict {
			code = c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT
		}
		f.WriteErr = &bridge.NativeFailure{Value: &c.Failure{Code: code.Enum()}}
		out, err := b.Draft(context.Background(), executor.DraftDispatch{Attempt: f.P, PawnSnapshotToken: "cas"})
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
