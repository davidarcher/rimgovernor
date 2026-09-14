package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// MoodReliefNative reuses the generic bridge.ReadPawns (its PawnState rows
// already carry the Job and Settings/Schedule fields moodReliefDispatchFacts
// decodes, unlike waste_boundary's ReadPawns use which only needs
// dead/downed/drafted/mental) and the dedicated bridge.MoodReliefWriter
// preview/lookup/progress calls, which already validate the receipt/evidence
// shape against the exact attempt, mirroring WasteNative's role for
// MaintainWaste.
type MoodReliefNative interface {
	ReadPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	PreviewMoodRelief(context.Context, *c.Identity, string, string, bridge.MoodReliefNeed, bridge.MoodReliefExpectedJob, string) (*o.PreviewReply, bridge.Result, error)
	LookupMoodRelief(context.Context, bridge.MoodReliefAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveMoodReliefProgress(context.Context, bridge.MoodReliefAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type MoodReliefWriter interface {
	ApplyMoodRelief(context.Context, *a.WritePrecondition, *a.Owner, string, string, bridge.MoodReliefNeed, bridge.MoodReliefExpectedJob, string) (*o.ExecuteReply, bridge.Result, error)
}
type MoodReliefCapabilities struct {
	Native MoodReliefNative
	Writer MoodReliefWriter
}
type MoodReliefBoundary struct {
	native    MoodReliefNative
	writer    MoodReliefWriter
	leases    boundary.LeaseSource
	clock     executor.Clock
	session   string
	longitude domain.Fact[float64]
}

func NewMoodReliefBoundary(native MoodReliefNative, writer MoodReliefWriter, leases boundary.LeaseSource, clock executor.Clock, session string, longitude domain.Fact[float64]) (*MoodReliefBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid mood relief boundary dependencies")
	}
	if lon, known := longitude.Value(); known && (lon < -180 || lon > 180) {
		return nil, errors.New("invalid mood relief boundary longitude")
	}
	return &MoodReliefBoundary{native, writer, leases, clock, session, longitude}, nil
}

func moodReliefNeedWire(need domain.MoodReliefNeed) bridge.MoodReliefNeed {
	switch need {
	case domain.MoodReliefFood:
		return bridge.MoodReliefFood
	case domain.MoodReliefRest:
		return bridge.MoodReliefRest
	case domain.MoodReliefJoy:
		return bridge.MoodReliefJoy
	default:
		return bridge.MoodReliefNeedUnspecified
	}
}

func moodReliefJobWire(job domain.MoodReliefJob) bridge.MoodReliefExpectedJob {
	if job.Idle {
		return bridge.MoodReliefExpectedJob{Idle: true}
	}
	id := job.JobID
	return bridge.MoodReliefExpectedJob{JobID: &id}
}

func moodReliefJobDomain(job bridge.MoodReliefExpectedJob) (domain.MoodReliefJob, bool) {
	if !job.Idle && job.JobID == nil {
		return domain.MoodReliefJob{}, false
	}
	if job.Idle {
		return domain.MoodReliefJob{Idle: true}, true
	}
	return domain.MoodReliefJob{JobID: *job.JobID}, true
}

func moodReliefPawnFacts(pawn domain.PawnID, row *n.PawnState, token string, absTicks int64, longitude float64, longitudeKnown bool) policy.MoodReliefPawnFacts {
	facts := policy.MoodReliefPawnFacts{Pawn: pawn, SnapshotToken: token, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), Mental: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "player_forced") {
		facts.PlayerForced = boundary.FactBool(row.Job.PlayerForced)
	}
	if longitudeKnown {
		if job, def, ok := moodReliefDispatchFacts(row, absTicks, longitude); ok {
			if value, ok := moodReliefJobDomain(job); ok {
				facts.ExpectedJob = domain.Known(value)
				facts.ExpectedScheduleDef = domain.Known(def)
			}
		}
	}
	return facts
}

func (b *MoodReliefBoundary) InspectMoodRelief(ctx context.Context, target executor.Target) (executor.MoodReliefInspection, error) {
	out := executor.MoodReliefInspection{StartedAt: b.clock.Now()}
	relief, ok := target.Action.MoodRelief()
	if !ok {
		return out, executor.ErrEvidence
	}
	reply, _, err := b.native.ReadPawns(ctx, boundary.Identity(target.Snapshot), []string{string(relief.Pawn())})
	if err != nil {
		return out, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return out, executor.ErrHeld
	}
	current, err := boundary.Context(observed.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	counts := observed.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != 1 || counts.GetReturned() != 1 || len(observed.Pawns) != 1 {
		return out, executor.ErrHeld
	}
	row := observed.Pawns[0]
	if row == nil || row.Pawn == nil || row.Pawn.GetId() != string(relief.Pawn()) {
		return out, executor.ErrEvidence
	}
	pawnToken, err := boundary.PawnToken(row, observed.Context)
	if err != nil {
		return out, err
	}
	longitude, longitudeKnown := b.longitude.Value()
	facts := policy.MoodReliefDispatchFacts{Snapshot: current, PawnTick: domain.Tick(observed.Context.GetTick())}
	facts.Pawn = moodReliefPawnFacts(relief.Pawn(), row, pawnToken, observed.Context.GetTick(), longitude, longitudeKnown)
	job, jobKnown := facts.Pawn.ExpectedJob.Value()
	def, defKnown := facts.Pawn.ExpectedScheduleDef.Value()
	if !jobKnown || !defKnown || job != relief.ExpectedJob() || def != relief.ExpectedScheduleDef() {
		// Stale or unknown fencing never invents a preview; EvaluateMoodRelief
		// (via the unknown/mismatched facts already recorded above) refuses.
		facts.PreviewTick = facts.PawnTick
		out.Facts, out.ObservedAt = facts, b.clock.Now()
		return out, ctx.Err()
	}
	preview, _, err := b.native.PreviewMoodRelief(ctx, boundary.Identity(current), string(relief.Pawn()), pawnToken, moodReliefNeedWire(relief.Need()), moodReliefJobWire(relief.ExpectedJob()), relief.ExpectedScheduleDef())
	if err != nil {
		return out, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil {
		return out, executor.ErrHeld
	}
	if _, err = boundary.Context(evaluated.Context, current); err != nil {
		return out, err
	}
	if evaluated.Context.GetTick() < observed.Context.GetTick() || evaluated.Accepted == nil {
		return out, executor.ErrEvidence
	}
	facts.PreviewTick = domain.Tick(evaluated.Context.GetTick())
	facts.NativeCanTry = domain.Known(evaluated.GetAccepted())
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func (b *MoodReliefBoundary) attempt(dispatch executor.MoodReliefDispatch) (bridge.MoodReliefAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	relief, ok := p.Action.MoodRelief()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Pawn != relief.Pawn() || admission.Need != relief.Need() || admission.ExpectedJob != relief.ExpectedJob() || admission.ExpectedScheduleDef != relief.ExpectedScheduleDef() || admission.Tick > p.Tick || !boundary.ValidID(admission.PawnSnapshotToken) {
		return bridge.MoodReliefAttempt{}, executor.ErrEvidence
	}
	return bridge.MoodReliefAttempt{
		Identity:   boundary.Identity(p.Snapshot),
		Attempt:    &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))},
		Owner:      &a.Owner{ControllerSessionId: proto.String(b.session), PlayerDirection: proto.Uint64(uint64(p.Snapshot.Direction))},
		Generation: uint64(p.Snapshot.Native),
		Pawn:       string(relief.Pawn()), PawnToken: admission.PawnSnapshotToken,
		Need: moodReliefNeedWire(relief.Need()), ExpectedJob: moodReliefJobWire(relief.ExpectedJob()), ExpectedScheduleDef: relief.ExpectedScheduleDef(),
	}, nil
}

func (b *MoodReliefBoundary) ManageMoodRelief(ctx context.Context, dispatch executor.MoodReliefDispatch) (executor.Receipt, error) {
	p := dispatch.Attempt
	out := executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptUnknown}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
	}
	lease, err := b.leases.Lease(p.Snapshot)
	if err != nil {
		return out, err
	}
	if !boundary.ValidID(lease) {
		return out, executor.ErrAuthority
	}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	pre := &a.WritePrecondition{Identity: attempt.Identity, Attempt: attempt.Attempt, ExpectedGeneration: proto.Uint64(attempt.Generation), LeaseId: proto.String(lease)}
	reply, _, err := b.writer.ApplyMoodRelief(ctx, pre, attempt.Owner, attempt.Pawn, attempt.PawnToken, attempt.Need, attempt.ExpectedJob, attempt.ExpectedScheduleDef)
	var refused *bridge.NativeFailure
	if errors.As(err, &refused) && refused.Value != nil && refused.Value.GetCode() != c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT {
		out.Kind = domain.ReceiptRefused
		return out, nil
	}
	if err != nil {
		return out, err
	}
	switch reply.GetReceipt().Outcome.(type) {
	case *r.Receipt_Applied:
		out.Kind = domain.ReceiptAccepted
	case *r.Receipt_Uncertain:
	default:
		return out, executor.ErrEvidence
	}
	return out, ctx.Err()
}

var _ executor.MoodReliefBoundary = (*MoodReliefBoundary)(nil)
