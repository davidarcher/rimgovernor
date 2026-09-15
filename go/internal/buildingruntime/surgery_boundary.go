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

// surgeryCareFromNative maps RimWorld's MedicalCareCategory enum name (as
// returned by PawnSettings.medical_care, i.e. MedicalCareCategory.ToString())
// to the domain bucket QueueSurgery's expected_care precondition uses. The
// native enum member is spelled "NoMeds", not "NoMedicine" — only the
// domain-level bucket name reads more descriptively. An unrecognized name is
// reported unknown rather than guessed.
func surgeryCareFromNative(name string) (domain.MedicalCare, bool) {
	switch name {
	case "NoCare":
		return domain.MedicalCareNoCare, true
	case "NoMeds":
		return domain.MedicalCareNoMedicine, true
	case "HerbalOrWorse":
		return domain.MedicalCareHerbalOrWorse, true
	case "NormalOrWorse":
		return domain.MedicalCareNormalOrWorse, true
	case "Best":
		return domain.MedicalCareBest, true
	default:
		return "", false
	}
}

// SurgeryNative reuses the existing tend pawn read for patient eligibility
// (dead/downed) and current medical care policy, plus a dedicated dry-run
// preview (bridge.ReadSurgeryTarget) for the native-computed health-signature
// CAS token and native eligibility, the same split ReadPawns+ReadBedTarget
// establishes for BedAssign.
type SurgeryNative interface {
	ReadTendPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	ReadSurgeryTarget(context.Context, *c.Identity, string, string, string, int32) (bridge.SurgeryTarget, bridge.Result, error)
	PreviewSurgery(context.Context, *c.Identity, string, string, string, int32, string, domain.MedicalCare) (*o.PreviewReply, bridge.Result, error)
	LookupSurgery(context.Context, bridge.SurgeryAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveSurgeryProgress(context.Context, bridge.SurgeryAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type SurgeryWriter interface {
	ApplySurgery(context.Context, *a.WritePrecondition, string, string, string, int32, string, domain.MedicalCare) (*o.ExecuteReply, bridge.Result, error)
}
type SurgeryCapabilities struct {
	Native SurgeryNative
	Writer SurgeryWriter
}
type SurgeryBoundary struct {
	native  SurgeryNative
	writer  SurgeryWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewSurgeryBoundary(native SurgeryNative, writer SurgeryWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*SurgeryBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid surgery boundary dependencies")
	}
	return &SurgeryBoundary{native, writer, leases, clock, session}, nil
}

func (b *SurgeryBoundary) InspectSurgery(ctx context.Context, target executor.Target) (executor.SurgeryInspection, error) {
	out := executor.SurgeryInspection{StartedAt: b.clock.Now()}
	surgery, ok := target.Action.Surgery()
	if !ok {
		return out, executor.ErrEvidence
	}
	reply, _, err := b.native.ReadTendPawns(ctx, boundary.Identity(target.Snapshot), []string{string(surgery.Patient())})
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
	if row == nil || row.Pawn == nil || row.Pawn.GetId() != string(surgery.Patient()) {
		return out, executor.ErrEvidence
	}
	patientToken, err := boundary.PawnToken(row, observed.Context)
	if err != nil {
		return out, err
	}
	care, ok := surgeryCareFromNative(row.GetSettings().GetMedicalCare())
	if !ok {
		return out, executor.ErrHeld
	}
	target2, _, err := b.native.ReadSurgeryTarget(ctx, boundary.Identity(current), string(surgery.Patient()), patientToken, surgery.Recipe(), surgery.Part())
	if err != nil {
		return out, err
	}
	targetCtx, err := boundary.Context(target2.Context, current)
	if err != nil {
		return out, err
	}
	if targetCtx.Native < current.Native {
		return out, executor.ErrEvidence
	}
	preview, _, err := b.native.PreviewSurgery(ctx, boundary.Identity(targetCtx), string(surgery.Patient()), patientToken, surgery.Recipe(), surgery.Part(), target2.HealthToken, care)
	if err != nil {
		return out, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil {
		return out, executor.ErrHeld
	}
	if _, err = boundary.Context(evaluated.Context, targetCtx); err != nil {
		return out, err
	}
	effect := evaluated.GetProjected().GetSurgery()
	if evaluated.Context.GetTick() < target2.Context.GetTick() || evaluated.Accepted == nil || effect == nil || effect.GetPatientId() != string(surgery.Patient()) || effect.GetRecipeDef() != surgery.Recipe() || effect.GetPartIndex() != surgery.Part() {
		return out, executor.ErrEvidence
	}
	facts := policy.SurgeryFacts{Snapshot: targetCtx, PawnTick: domain.Tick(observed.Context.GetTick()), PreviewTick: domain.Tick(evaluated.Context.GetTick()), HealthToken: target2.HealthToken, Care: care, NativeCanTry: boundary.FactBool(evaluated.Accepted)}
	facts.Patient = policy.SurgeryPatientFacts{Patient: surgery.Patient(), SnapshotToken: patientToken, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed)}
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func (b *SurgeryBoundary) attempt(dispatch executor.SurgeryDispatch) (bridge.SurgeryAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	surgery, ok := p.Action.Surgery()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Patient != surgery.Patient() || admission.Recipe != surgery.Recipe() || admission.Part != surgery.Part() || admission.Tick > p.Tick || !boundary.ValidID(admission.PatientSnapshotToken) || !boundary.ValidID(admission.HealthToken) || !domain.ValidMedicalCare(admission.Care) {
		return bridge.SurgeryAttempt{}, executor.ErrEvidence
	}
	return bridge.SurgeryAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}, Generation: uint64(p.Snapshot.Native), Patient: string(surgery.Patient()), PatientToken: admission.PatientSnapshotToken, Recipe: surgery.Recipe(), Part: surgery.Part(), HealthToken: admission.HealthToken, Care: admission.Care}, nil
}

func (b *SurgeryBoundary) QueueSurgery(ctx context.Context, dispatch executor.SurgeryDispatch) (executor.Receipt, error) {
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
	pre := &a.WritePrecondition{Identity: attempt.Identity, Attempt: attempt.Attempt, ExpectedGeneration: proto.Uint64(attempt.Generation),}
	reply, _, err := b.writer.ApplySurgery(ctx, pre, attempt.Patient, attempt.PatientToken, attempt.Recipe, attempt.Part, attempt.HealthToken, attempt.Care)
	var refused *bridge.NativeFailure
	if errors.As(err, &refused) && refused.Value != nil && refused.Value.GetCode() != c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT {
		out.Kind = domain.ReceiptRefused
		return out, nil
	}
	if err != nil {
		return out, err
	}
	receipt := reply.GetReceipt()
	if err = b.checkReceipt(receipt, dispatch); err != nil {
		return out, err
	}
	switch receipt.Outcome.(type) {
	case *r.Receipt_Applied:
		out.Kind = domain.ReceiptAccepted
	case *r.Receipt_Uncertain:
	default:
		return out, executor.ErrEvidence
	}
	return out, ctx.Err()
}

func (b *SurgeryBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.SurgeryDispatch) error {
	p := dispatch.Attempt
	if err := boundary.Admission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	return nil
}

func (b *SurgeryBoundary) ObserveSurgery(ctx context.Context, dispatch executor.SurgeryDispatch, current domain.GenerationSnapshot) (executor.SurgeryEvidence, error) {
	out := executor.SurgeryEvidence{StartedAt: b.clock.Now()}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
	}
	reply, _, err := b.native.LookupSurgery(ctx, attempt)
	if err != nil {
		return out, err
	}
	p := dispatch.Attempt
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Unknown:
		if _, err = boundary.Context(v.Unknown.GetContext(), current); err != nil {
			return out, err
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: domain.Tick(v.Unknown.GetContext().GetTick()), Effect: domain.EffectUnknown, Causality: domain.AfterDispatch}
		out.ObservedAt = b.clock.Now()
		return out, ctx.Err()
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, attempt.Attempt) {
			return out, executor.ErrEvidence
		}
		if err = boundary.Admission(&r.Receipt{Attempt: v.InFlight.Attempt, AdmittedContext: v.InFlight.AdmittedContext}, p, b.session); err != nil {
			return out, err
		}
	case *r.LookupReply_Receipt:
		if err = b.checkReceipt(v.Receipt, dispatch); err != nil {
			return out, err
		}
	default:
		return out, executor.ErrEvidence
	}
	progress, _, err := b.native.ObserveSurgeryProgress(ctx, attempt, nil)
	if err != nil {
		return out, err
	}
	prog := progress.GetProgress()
	if prog == nil {
		return out, executor.ErrHeld
	}
	if !proto.Equal(prog.Attempt, attempt.Attempt) {
		return out, executor.ErrEvidence
	}
	tickCtx, err := boundary.Context(prog.Context, current)
	if err != nil {
		return out, err
	}
	tick := domain.Tick(prog.Context.GetTick())
	out.ObservedAt = b.clock.Now()
	surgery := mustSurgery(dispatch)
	switch outcome := prog.Effect.(type) {
	case *r.Progress_Unknown:
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectUnknown, Causality: domain.AfterDispatch}
		return out, ctx.Err()
	case *r.Progress_Pending:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectPending, Causality: domain.AfterDispatch}
		out.Patient = surgery.Patient()
		return out, ctx.Err()
	case *r.Progress_Completed:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}
		out.Complete, out.Patient = true, surgery.Patient()
		return out, ctx.Err()
	case *r.Progress_Absent:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectAbsent, Causality: domain.AfterDispatch}
		out.Complete, out.Patient = true, surgery.Patient()
		return out, ctx.Err()
	case *r.Progress_Unsuccessful:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectUnsuccessful, UnsuccessfulReason: domain.NativeFailure, Causality: domain.AfterDispatch}
		out.Complete, out.Patient = true, surgery.Patient()
		return out, ctx.Err()
	default:
		_ = outcome
		return out, executor.ErrEvidence
	}
}

func mustSurgery(dispatch executor.SurgeryDispatch) domain.Surgery {
	surgery, _ := dispatch.Attempt.Action.Surgery()
	return surgery
}

var _ executor.SurgeryBoundary = (*SurgeryBoundary)(nil)
