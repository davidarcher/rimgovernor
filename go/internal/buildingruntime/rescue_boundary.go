package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
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

type RescueNative interface {
	ReadCombatPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	PreviewPawnOrder(context.Context, *c.Identity, *o.PawnTargetOrder) (*o.PreviewReply, bridge.Result, error)
	LookupPawnOrderAttempt(context.Context, bridge.PawnOrderAttempt) (*r.LookupReply, bridge.Result, error)
	ObservePawnOrderProgress(context.Context, bridge.PawnOrderAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type RescueWriter interface {
	OrderPawn(context.Context, *a.WritePrecondition, *a.Owner, *o.PawnTargetOrder) (*o.ExecuteReply, bridge.Result, error)
}
type RescueCapabilities struct {
	Native RescueNative
	Writer RescueWriter
}
type RescueBoundary struct {
	native  RescueNative
	writer  RescueWriter
	leases  LeaseSource
	clock   executor.Clock
	session string
}

func NewRescueBoundary(native RescueNative, writer RescueWriter, leases LeaseSource, clock executor.Clock, session string) (*RescueBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundaryID(session) {
		return nil, errors.New("invalid rescue boundary dependencies")
	}
	return &RescueBoundary{native, writer, leases, clock, session}, nil
}

func rescueCommand(rescuer, patient, rescuerToken, patientToken string) *o.PawnTargetOrder {
	return &o.PawnTargetOrder{Pawn: &o.EntityPrecondition{EntityId: proto.String(rescuer), ExpectedSnapshotToken: proto.String(rescuerToken)}, Target: &o.EntityPrecondition{EntityId: proto.String(patient), ExpectedSnapshotToken: proto.String(patientToken)}, Kind: o.PawnOrderKind_PAWN_ORDER_KIND_RESCUE.Enum(), RequireSafeStorage: proto.Bool(false)}
}

func rescuerFacts(pawn domain.PawnID, row *n.PawnState, token string) policy.RescuerFacts {
	facts := policy.RescuerFacts{Pawn: pawn, SnapshotToken: token, Dead: draftBool(row.Dead), Downed: draftBool(row.Downed), Drafted: draftBool(row.Drafted), MentalState: draftPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !tendIssue(row.Job.Issues, "player_forced") && !tendIssue(row.Job.Issues, "queued_jobs") && !tendIssue(row.Job.Issues, "def_name") {
		facts.PlayerForced, facts.QueuedJobs = draftBool(row.Job.PlayerForced), draftUint(row.Job.QueuedJobs)
		if row.Job.DefName != nil {
			facts.ExistingJobDef = domain.Known(row.Job.GetDefName())
		}
	}
	return facts
}
func rescuePatientFacts(pawn domain.PawnID, row *n.PawnState, token string) policy.RescuePatientFacts {
	facts := policy.RescuePatientFacts{Pawn: pawn, SnapshotToken: token, Dead: draftBool(row.Dead), Downed: draftBool(row.Downed), InBed: draftBool(row.InBed)}
	if row.Job != nil && !tendIssue(row.Job.Issues, "def_name") && row.Job.DefName != nil {
		facts.ExistingJobDef = domain.Known(row.Job.GetDefName())
	}
	if health := row.Health; health != nil && !tendIssue(health.Issues, "health") && health.BedId != nil {
		facts.BedID = domain.Known(health.GetBedId())
	}
	return facts
}

func (b *RescueBoundary) InspectRescue(ctx context.Context, target executor.Target) (executor.RescueInspection, error) {
	out := executor.RescueInspection{StartedAt: b.clock.Now()}
	rescue, ok := target.Action.Rescue()
	if !ok {
		return out, executor.ErrEvidence
	}
	reply, _, err := b.native.ReadCombatPawns(ctx, boundaryIdentity(target.Snapshot), []string{string(rescue.Rescuer()), string(rescue.Patient())})
	if err != nil {
		return out, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return out, executor.ErrHeld
	}
	current, err := boundaryContext(observed.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	counts := observed.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != 2 || counts.GetReturned() != 2 || len(observed.Pawns) != 2 {
		return out, executor.ErrHeld
	}
	var rescuerRow, patientRow *n.PawnState
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil {
			return out, executor.ErrEvidence
		}
		switch row.Pawn.GetId() {
		case string(rescue.Rescuer()):
			if rescuerRow != nil {
				return out, executor.ErrEvidence
			}
			rescuerRow = row
		case string(rescue.Patient()):
			if patientRow != nil {
				return out, executor.ErrEvidence
			}
			patientRow = row
		default:
			return out, executor.ErrEvidence
		}
	}
	if rescuerRow == nil || patientRow == nil {
		return out, executor.ErrHeld
	}
	rescuerToken, err := draftToken(rescuerRow, observed.Context)
	if err != nil {
		return out, err
	}
	patientToken, err := draftToken(patientRow, observed.Context)
	if err != nil {
		return out, err
	}
	preview, _, err := b.native.PreviewPawnOrder(ctx, boundaryIdentity(current), rescueCommand(string(rescue.Rescuer()), string(rescue.Patient()), rescuerToken, patientToken))
	if err != nil {
		return out, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil {
		return out, executor.ErrHeld
	}
	if _, err = boundaryContext(evaluated.Context, current); err != nil {
		return out, err
	}
	job := evaluated.GetProjected().GetJob()
	if evaluated.Context.GetTick() < observed.Context.GetTick() || evaluated.Accepted == nil || job == nil || job.GetPawnId() != string(rescue.Rescuer()) || job.GetTargetA().GetThingId() != string(rescue.Patient()) || job.GetJobDef() != "Rescue" || job.CanTry == nil || job.GetCanTry() != evaluated.GetAccepted() {
		return out, executor.ErrEvidence
	}
	emergency, _, err := b.native.ReadEmergency(ctx, boundaryIdentity(current))
	if err != nil {
		return out, err
	}
	if _, err = boundaryContext(emergency.Context, current); err != nil {
		return out, err
	}
	if emergency.Context.GetTick() < evaluated.Context.GetTick() {
		return out, executor.ErrEvidence
	}
	facts := policy.RescueFacts{Snapshot: current, PawnTick: domain.Tick(observed.Context.GetTick()), PreviewTick: domain.Tick(evaluated.Context.GetTick()), NativeCanTry: draftBool(job.CanTry)}
	facts.Emergency, err = policy.NewEmergencySnapshot(current, domain.Tick(emergency.Context.GetTick()), emergency.Facts)
	if err != nil {
		return out, err
	}
	facts.Rescuer = rescuerFacts(rescue.Rescuer(), rescuerRow, rescuerToken)
	facts.Patient = rescuePatientFacts(rescue.Patient(), patientRow, patientToken)
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func (b *RescueBoundary) attempt(dispatch executor.RescueDispatch) (bridge.PawnOrderAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	rescue, ok := p.Action.Rescue()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Rescuer != rescue.Rescuer() || admission.Patient != rescue.Patient() || admission.Tick > p.Tick || !boundaryID(admission.RescuerSnapshotToken) || !boundaryID(admission.PatientSnapshotToken) {
		return bridge.PawnOrderAttempt{}, executor.ErrEvidence
	}
	return bridge.PawnOrderAttempt{Identity: boundaryIdentity(p.Snapshot), Attempt: &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}, NativeGeneration: uint64(p.Snapshot.Native), Owner: &a.Owner{ControllerSessionId: proto.String(b.session), PlayerDirection: proto.Uint64(uint64(p.Snapshot.Direction))}, PawnID: string(rescue.Rescuer()), TargetID: string(rescue.Patient()), Kind: o.PawnOrderKind_PAWN_ORDER_KIND_RESCUE, RequireSafeStorage: false}, nil
}

func rescueJob(job *r.JobEffect, dispatch executor.RescueDispatch) error {
	if job == nil {
		return nil
	}
	rescue, _ := dispatch.Attempt.Action.Rescue()
	if job.GetPawnId() != string(rescue.Rescuer()) || job.GetTargetA().GetThingId() != string(rescue.Patient()) || job.GetJobDef() != "Rescue" || job.JobId == nil || job.GetJobId() < 0 {
		return executor.ErrEvidence
	}
	if job.Drafted != nil && job.GetDrafted() || job.DraftClaimId != nil || job.DraftOwner != nil {
		return executor.ErrEvidence
	}
	return nil
}

func (b *RescueBoundary) RescuePatient(ctx context.Context, dispatch executor.RescueDispatch) (executor.Receipt, error) {
	return dispatchPawnOrder(ctx, b.leases, b.writer, dispatch.Attempt,
		func() (bridge.PawnOrderAttempt, error) { return b.attempt(dispatch) },
		func(attempt bridge.PawnOrderAttempt) *o.PawnTargetOrder {
			return rescueCommand(attempt.PawnID, attempt.TargetID, dispatch.Admission.RescuerSnapshotToken, dispatch.Admission.PatientSnapshotToken)
		},
		func(receipt *r.Receipt) error { return b.checkReceipt(receipt, dispatch) },
	)
}

func (b *RescueBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.RescueDispatch) error {
	p := dispatch.Attempt
	if err := boundaryAdmission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	job := draftReceiptJob(receipt)
	return rescueJob(job, dispatch)
}

var _ executor.RescueBoundary = (*RescueBoundary)(nil)
