package capture

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"

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

type CaptureNative interface {
	ReadCombatPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	PreviewPawnOrder(context.Context, *c.Identity, *o.PawnTargetOrder) (*o.PreviewReply, bridge.Result, error)
	LookupPawnOrderAttempt(context.Context, bridge.PawnOrderAttempt) (*r.LookupReply, bridge.Result, error)
	ObservePawnOrderProgress(context.Context, bridge.PawnOrderAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type CaptureWriter interface {
	OrderPawn(context.Context, *a.WritePrecondition, *o.PawnTargetOrder) (*o.ExecuteReply, bridge.Result, error)
}
type CaptureCapabilities struct {
	Native CaptureNative
	Writer CaptureWriter
}
type CaptureBoundary struct {
	native  CaptureNative
	writer  CaptureWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewCaptureBoundary(native CaptureNative, writer CaptureWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*CaptureBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid capture boundary dependencies")
	}
	return &CaptureBoundary{native, writer, leases, clock, session}, nil
}

func captureCommand(capturer, patient, capturerToken, patientToken string) *o.PawnTargetOrder {
	return &o.PawnTargetOrder{Pawn: &o.EntityPrecondition{EntityId: proto.String(capturer), ExpectedSnapshotToken: proto.String(capturerToken)}, Target: &o.EntityPrecondition{EntityId: proto.String(patient), ExpectedSnapshotToken: proto.String(patientToken)}, Kind: o.PawnOrderKind_PAWN_ORDER_KIND_CAPTURE.Enum(), RequireSafeStorage: proto.Bool(false)}
}

// NewCapturerFacts reuses Rescue's RescuerFacts shape: capturer eligibility
// (undrafted, not mid-mental-break, no queued player work) is identical.
func NewCapturerFacts(pawn domain.PawnID, row *n.PawnState, token string) policy.RescuerFacts {
	facts := policy.RescuerFacts{Pawn: pawn, SnapshotToken: token, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "player_forced") && !boundary.IssueField(row.Job.Issues, "queued_jobs") && !boundary.IssueField(row.Job.Issues, "def_name") {
		facts.PlayerForced, facts.QueuedJobs = boundary.FactBool(row.Job.PlayerForced), boundary.FactUint(row.Job.QueuedJobs)
		if row.Job.DefName != nil {
			facts.ExistingJobDef = domain.Known(row.Job.GetDefName())
		}
	}
	return facts
}
func NewCapturePatientFacts(pawn domain.PawnID, row *n.PawnState, token string) policy.CapturePatientFacts {
	facts := policy.CapturePatientFacts{Pawn: pawn, SnapshotToken: token, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Prisoner: boundary.FactBool(row.Prisoner)}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "def_name") && row.Job.DefName != nil {
		facts.ExistingJobDef = domain.Known(row.Job.GetDefName())
	}
	return facts
}

func (b *CaptureBoundary) InspectCapture(ctx context.Context, target executor.Target) (executor.CaptureInspection, error) {
	out := executor.CaptureInspection{StartedAt: b.clock.Now()}
	capture, ok := target.Action.Capture()
	if !ok {
		return out, executor.ErrEvidence
	}
	reply, _, err := b.native.ReadCombatPawns(ctx, boundary.Identity(target.Snapshot), []string{string(capture.Capturer()), string(capture.Patient())})
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
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != 2 || counts.GetReturned() != 2 || len(observed.Pawns) != 2 {
		return out, executor.ErrHeld
	}
	var capturerRow, patientRow *n.PawnState
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil {
			return out, executor.ErrEvidence
		}
		switch row.Pawn.GetId() {
		case string(capture.Capturer()):
			if capturerRow != nil {
				return out, executor.ErrEvidence
			}
			capturerRow = row
		case string(capture.Patient()):
			if patientRow != nil {
				return out, executor.ErrEvidence
			}
			patientRow = row
		default:
			return out, executor.ErrEvidence
		}
	}
	if capturerRow == nil || patientRow == nil {
		return out, executor.ErrHeld
	}
	capturerToken, err := boundary.PawnToken(capturerRow, observed.Context)
	if err != nil {
		return out, err
	}
	patientToken, err := boundary.PawnToken(patientRow, observed.Context)
	if err != nil {
		return out, err
	}
	preview, err := b.preview(ctx, current, capture, capturerToken, patientToken)
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
	job := evaluated.GetProjected().GetJob()
	if evaluated.Context.GetTick() < observed.Context.GetTick() || evaluated.Accepted == nil {
		return out, executor.ErrEvidence
	}
	if !capture.Arrest() && (job == nil || job.GetPawnId() != string(capture.Capturer()) || job.GetTargetA().GetThingId() != string(capture.Patient()) || job.GetJobDef() != "Capture" || job.CanTry == nil || job.GetCanTry() != evaluated.GetAccepted()) {
		return out, executor.ErrEvidence
	}
	emergency, _, err := b.native.ReadEmergency(ctx, boundary.Identity(current))
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(emergency.Context, current); err != nil {
		return out, err
	}
	// The emergency read may be served from the step's fact cache, up to
	// PlanningTickTolerance behind this inspection's first read; it must
	// cover that read, not the preview taken after it (#244).
	if !domain.Tick(emergency.Context.GetTick()).Covers(domain.Tick(observed.Context.GetTick())) {
		return out, executor.ErrEvidence
	}
	facts := policy.CaptureFacts{Snapshot: current, PawnTick: domain.Tick(observed.Context.GetTick()), PreviewTick: domain.Tick(evaluated.Context.GetTick()), NativeCanTry: boundary.FactBool(evaluated.Accepted)}
	facts.Emergency, err = policy.NewEmergencySnapshot(current, domain.Tick(evaluated.Context.GetTick()), emergency.Facts)
	if err != nil {
		return out, err
	}
	facts.Capturer = NewCapturerFacts(capture.Capturer(), capturerRow, capturerToken)
	facts.Patient = NewCapturePatientFacts(capture.Patient(), patientRow, patientToken)
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func (b *CaptureBoundary) attempt(dispatch executor.CaptureDispatch) (bridge.PawnOrderAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	capture, ok := p.Action.Capture()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Capturer != capture.Capturer() || admission.Patient != capture.Patient() || admission.Tick > p.Tick || !boundary.ValidID(admission.CapturerSnapshotToken) || !boundary.ValidID(admission.PatientSnapshotToken) {
		return bridge.PawnOrderAttempt{}, executor.ErrEvidence
	}
	return bridge.PawnOrderAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}, NativeGeneration: uint64(p.Snapshot.Native), PawnID: string(capture.Capturer()), TargetID: string(capture.Patient()), Kind: o.PawnOrderKind_PAWN_ORDER_KIND_CAPTURE, RequireSafeStorage: false, ArrestBed: capture.Bed()}, nil
}

func captureJob(job *r.JobEffect, dispatch executor.CaptureDispatch) error {
	if job == nil {
		return nil
	}
	capture, _ := dispatch.Attempt.Action.Capture()
	if job.GetPawnId() != string(capture.Capturer()) || job.GetTargetA().GetThingId() != string(capture.Patient()) || job.GetJobDef() != captureJobDef(capture) || job.JobId == nil || job.GetJobId() < 0 {
		return executor.ErrEvidence
	}
	if capture.Arrest() {
		if job.GetTargetB().GetThingId() != capture.Bed() || !boundary.ValidID(job.GetDraftClaimId()) {
			return executor.ErrEvidence
		}
		return nil
	}
	if job.Drafted != nil && job.GetDrafted() || job.DraftClaimId != nil || job.DraftOwner != nil {
		return executor.ErrEvidence
	}
	return nil
}

func (b *CaptureBoundary) CapturePatient(ctx context.Context, dispatch executor.CaptureDispatch) (executor.Receipt, error) {
	if c, _ := dispatch.Attempt.Action.Capture(); c.Arrest() {
		return b.arrest(ctx, dispatch)
	}
	return boundary.DispatchPawnOrder(ctx, b.leases, b.writer, dispatch.Attempt,
		func() (bridge.PawnOrderAttempt, error) { return b.attempt(dispatch) },
		func(attempt bridge.PawnOrderAttempt) *o.PawnTargetOrder {
			return captureCommand(attempt.PawnID, attempt.TargetID, dispatch.Admission.CapturerSnapshotToken, dispatch.Admission.PatientSnapshotToken)
		},
		func(receipt *r.Receipt) error { return b.checkReceipt(receipt, dispatch) },
	)
}

func (b *CaptureBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.CaptureDispatch) error {
	p := dispatch.Attempt
	if err := boundary.Admission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	job := boundary.ReceiptJob(receipt)
	return captureJob(job, dispatch)
}

var _ executor.CaptureBoundary = (*CaptureBoundary)(nil)
