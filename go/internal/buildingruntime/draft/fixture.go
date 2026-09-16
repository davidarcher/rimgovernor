package draft

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
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

// Fixture is a fake native draft dependency shared across the buildingruntime
// family test suites: several action families (e.g. melee) require a
// completed prior draft action for the same pawn, so their boundary tests
// build on top of a draft Fixture rather than duplicating one.
type Fixture struct {
	P                                                    executor.Placement
	Ctx                                                  *c.ObservationContext
	Row                                                  *n.PawnState
	Receipt                                              *r.Receipt
	Progress                                             *r.Progress
	ReadErr, WriteErr, ReleaseErr                        error
	MutateRelease                                        func(*o.ReleaseOwnedDraftReply)
	Leases, Writes, Reads, Lookups, Releases, Identities int
	LastPre                                              *a.WritePrecondition
	LastPawn                                             *o.EntityPrecondition
	LastRelease                                          *o.ReleaseOwnedDraftRequest
}

func NewFixture(t *testing.T) (*DraftBoundary, *Fixture) {
	t.Helper()
	draft, _ := domain.NewOwnedDraft("pawn")
	action, _ := domain.NewOwnedDraftAction("action", draft)
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
	p := executor.Placement{Action: action, Snapshot: snapshot, Attempt: 1, Tick: 10}
	ctx := &c.ObservationContext{Identity: boundary.Identity(snapshot), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(2)}
	ref := &n.SnapshotRef{Context: proto.Clone(ctx).(*c.ObservationContext), EntityId: proto.String("pawn"), Token: proto.String("cas")}
	row := &n.PawnState{Pawn: &n.EntityRef{Id: proto.String("pawn"), Snapshot: ref}, Drafted: proto.Bool(true), DraftClaim: &n.DraftClaimObservation{State: &n.DraftClaimObservation_Owned{Owned: &n.OwnedDraftClaim{ClaimId: proto.String("claim"), PawnSnapshot: proto.Clone(ref).(*n.SnapshotRef)}}}, Job: &n.JobEvidence{PlayerForced: proto.Bool(false), QueuedJobs: proto.Uint32(0)}}
	job := &r.JobEffect{PawnId: proto.String("pawn"), Drafted: proto.Bool(true), Verified: proto.Bool(true), Issued: proto.Bool(true), DraftOwner: proto.String("session"), DraftClaimId: proto.String("claim"), ResultingSnapshotToken: proto.String("cas")}
	effect := &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: job}}
	key := &c.AttemptKey{ControllerSessionId: proto.String("session"), ActionId: proto.String("action"), AttemptId: proto.Uint64(1)}
	f := &Fixture{P: p, Ctx: ctx, Row: row, Receipt: &r.Receipt{Attempt: key, AdmittedContext: proto.Clone(ctx).(*c.ObservationContext), Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: effect}}}, Progress: &r.Progress{Attempt: proto.Clone(key).(*c.AttemptKey), Context: proto.Clone(ctx).(*c.ObservationContext), CompleteInspection: proto.Bool(true), Effect: &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: proto.Clone(effect).(*r.EffectEvidence)}}}}
	b, err := NewDraftBoundary(f, f, f, f, boundary.FixedClock{}, "session")
	if err != nil {
		t.Fatal(err)
	}
	return b, f
}
func (f *Fixture) Identity(context.Context) (*l.IdentityReply, bridge.Result, error) {
	f.Identities++
	return &l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: proto.Clone(f.Ctx).(*c.ObservationContext)}}}, bridge.Result{}, f.ReadErr
}
func (f *Fixture) ReadPawns(_ context.Context, id *c.Identity, ids []string) (*n.ListPawnsReply, bridge.Result, error) {
	f.Reads++
	if !proto.Equal(id, f.Ctx.Identity) || len(ids) != 1 || ids[0] != "pawn" {
		return nil, bridge.Result{}, executor.ErrEvidence
	}
	var rows []*n.PawnState
	if f.Row != nil {
		rows = []*n.PawnState{proto.Clone(f.Row).(*n.PawnState)}
	}
	return &n.ListPawnsReply{Outcome: &n.ListPawnsReply_Observed{Observed: &n.PawnSnapshot{Context: proto.Clone(f.Ctx).(*c.ObservationContext), Pawns: rows, Completeness: &n.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(uint64(len(rows))), Returned: proto.Uint64(uint64(len(rows))), Filtered: proto.Uint64(10), Unreadable: proto.Uint64(0)}}}}, bridge.Result{}, f.ReadErr
}
func (f *Fixture) ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	return bridge.EmergencyObservation{Context: proto.Clone(f.Ctx).(*c.ObservationContext), Facts: policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true)}}, bridge.Result{}, f.ReadErr
}
func (f *Fixture) PreviewDraft(_ context.Context, _ *c.Identity, pawn *o.EntityPrecondition) (*o.PreviewReply, bridge.Result, error) {
	f.LastPawn = proto.Clone(pawn).(*o.EntityPrecondition)
	return &o.PreviewReply{Outcome: &o.PreviewReply_Evaluated{Evaluated: &o.PreviewEvaluation{Context: proto.Clone(f.Ctx).(*c.ObservationContext), Accepted: proto.Bool(true), Projected: &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: &r.JobEffect{PawnId: proto.String("pawn"), CanTry: proto.Bool(true)}}}}}}, bridge.Result{}, f.ReadErr
}
func (f *Fixture) LookupDraftAttempt(_ context.Context, attempt bridge.DraftAttempt) (*r.LookupReply, bridge.Result, error) {
	f.Lookups++
	if attempt.Attempt.GetActionId() != string(f.P.Action.ID()) || attempt.NativeGeneration != uint64(f.P.Snapshot.Native) {
		return nil, bridge.Result{}, executor.ErrEvidence
	}
	if f.Receipt == nil {
		return &r.LookupReply{Outcome: &r.LookupReply_Unknown{Unknown: &r.UnknownAttempt{Context: proto.Clone(f.Ctx).(*c.ObservationContext)}}}, bridge.Result{}, nil
	}
	return &r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: proto.Clone(f.Receipt).(*r.Receipt)}}, bridge.Result{}, f.ReadErr
}
func (f *Fixture) ObserveDraftProgress(context.Context, bridge.DraftAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
	return &r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: proto.Clone(f.Progress).(*r.Progress)}}, bridge.Result{}, f.ReadErr
}
func (f *Fixture) Lease(domain.GenerationSnapshot) (string, error) {
	f.Leases++
	return "lease", nil
}
func (f *Fixture) DraftPawn(_ context.Context, pre *a.WritePrecondition, pawn *o.EntityPrecondition) (*o.ExecuteReply, bridge.Result, error) {
	f.Writes++
	f.LastPre = proto.Clone(pre).(*a.WritePrecondition)
	f.LastPawn = proto.Clone(pawn).(*o.EntityPrecondition)
	return &o.ExecuteReply{Outcome: &o.ExecuteReply_Receipt{Receipt: proto.Clone(f.Receipt).(*r.Receipt)}}, bridge.Result{}, f.WriteErr
}
func (f *Fixture) ReleaseOwnedDraft(_ context.Context, q *o.ReleaseOwnedDraftRequest) (*o.ReleaseOwnedDraftReply, bridge.Result, error) {
	f.Releases++
	f.LastRelease = proto.Clone(q).(*o.ReleaseOwnedDraftRequest)
	reply := &o.ReleaseOwnedDraftReply{Outcome: &o.ReleaseOwnedDraftReply_Released{Released: &o.DraftRelease{Request: proto.Clone(q).(*o.ReleaseOwnedDraftRequest), Context: proto.Clone(f.Ctx).(*c.ObservationContext), Observed: &r.JobEffect{PawnId: q.Pawn.EntityId, Drafted: proto.Bool(false), Verified: proto.Bool(true), Issued: proto.Bool(true), DraftOwner: proto.String("session"), DraftClaimId: q.ExpectedClaimId, ResultingSnapshotToken: proto.String("after")}}}}
	if f.MutateRelease != nil {
		f.MutateRelease(reply)
	}
	return reply, bridge.Result{}, f.ReleaseErr
}

// KnownClaim returns the draft claim that NewFixture's dispatch establishes
// for "pawn", for tests (in this package and others) that assert against it.
func KnownClaim(f *Fixture) domain.DraftClaim {
	return domain.DraftClaim{Action: f.P.Action.ID(), Attempt: 1, Pawn: "pawn", Claim: "claim", Session: "session", Origin: f.P.Snapshot}
}
