package tend

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

type TendNative interface {
	ReadTendPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	PreviewPawnOrder(context.Context, *c.Identity, *o.PawnTargetOrder) (*o.PreviewReply, bridge.Result, error)
	LookupPawnOrderAttempt(context.Context, bridge.PawnOrderAttempt) (*r.LookupReply, bridge.Result, error)
	ObservePawnOrderProgress(context.Context, bridge.PawnOrderAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type TendWriter interface {
	OrderPawn(context.Context, *a.WritePrecondition, *a.Owner, *o.PawnTargetOrder) (*o.ExecuteReply, bridge.Result, error)
}
type TendCapabilities struct {
	Native TendNative
	Writer TendWriter
}
type TendBoundary struct {
	native  TendNative
	writer  TendWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewTendBoundary(native TendNative, writer TendWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*TendBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid tend boundary dependencies")
	}
	return &TendBoundary{native, writer, leases, clock, session}, nil
}

func tendCommand(doctor, patient, doctorToken, patientToken string) *o.PawnTargetOrder {
	return &o.PawnTargetOrder{Pawn: &o.EntityPrecondition{EntityId: proto.String(doctor), ExpectedSnapshotToken: proto.String(doctorToken)}, Target: &o.EntityPrecondition{EntityId: proto.String(patient), ExpectedSnapshotToken: proto.String(patientToken)}, Kind: o.PawnOrderKind_PAWN_ORDER_KIND_TEND.Enum(), RequireSafeStorage: proto.Bool(false)}
}

func medicineSkillLevel(skills []*n.Skill) (level domain.Fact[int32], disabled domain.Fact[bool]) {
	for _, s := range skills {
		if s == nil || s.Definition == nil {
			continue
		}
		if s.Definition.GetDefName() == "Medicine" {
			if s.Disabled != nil {
				disabled = domain.Known(s.GetDisabled())
			}
			if s.Level != nil {
				level = domain.Known(s.GetLevel())
			}
			return level, disabled
		}
	}
	return domain.Unknown[int32](), domain.Unknown[bool]()
}
func doctorWorkFacts(work []*n.WorkSetting) (enabled domain.Fact[bool], overrideDisabled domain.Fact[bool]) {
	for _, w := range work {
		if w == nil {
			continue
		}
		if w.GetDefName() == "Doctor" {
			if w.Disabled == nil || w.Priority == nil {
				return domain.Unknown[bool](), domain.Unknown[bool]()
			}
			return domain.Known(!w.GetDisabled()), domain.Known(w.GetPriority() == 0)
		}
	}
	return domain.Unknown[bool](), domain.Unknown[bool]()
}

func (b *TendBoundary) InspectTend(ctx context.Context, target executor.Target) (executor.TendInspection, error) {
	out := executor.TendInspection{StartedAt: b.clock.Now()}
	tend, ok := target.Action.Tend()
	if !ok {
		return out, executor.ErrEvidence
	}
	reply, _, err := b.native.ReadTendPawns(ctx, boundary.Identity(target.Snapshot), []string{string(tend.Doctor()), string(tend.Patient())})
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
	var doctorRow, patientRow *n.PawnState
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil {
			return out, executor.ErrEvidence
		}
		switch row.Pawn.GetId() {
		case string(tend.Doctor()):
			if doctorRow != nil {
				return out, executor.ErrEvidence
			}
			doctorRow = row
		case string(tend.Patient()):
			if patientRow != nil {
				return out, executor.ErrEvidence
			}
			patientRow = row
		default:
			return out, executor.ErrEvidence
		}
	}
	if doctorRow == nil || patientRow == nil {
		return out, executor.ErrHeld
	}
	doctorToken, err := boundary.PawnToken(doctorRow, observed.Context)
	if err != nil {
		return out, err
	}
	patientToken, err := boundary.PawnToken(patientRow, observed.Context)
	if err != nil {
		return out, err
	}
	preview, _, err := b.native.PreviewPawnOrder(ctx, boundary.Identity(current), tendCommand(string(tend.Doctor()), string(tend.Patient()), doctorToken, patientToken))
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
	if evaluated.Context.GetTick() < observed.Context.GetTick() || evaluated.Accepted == nil || job == nil || job.GetPawnId() != string(tend.Doctor()) || job.GetTargetA().GetThingId() != string(tend.Patient()) || job.GetJobDef() != "TendPatient" || job.CanTry == nil || job.GetCanTry() != evaluated.GetAccepted() {
		return out, executor.ErrEvidence
	}
	emergency, _, err := b.native.ReadEmergency(ctx, boundary.Identity(current))
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(emergency.Context, current); err != nil {
		return out, err
	}
	if emergency.Context.GetTick() < evaluated.Context.GetTick() {
		return out, executor.ErrEvidence
	}
	facts := policy.TendFacts{Snapshot: current, PawnTick: domain.Tick(observed.Context.GetTick()), PreviewTick: domain.Tick(evaluated.Context.GetTick()), NativeCanTry: boundary.FactBool(job.CanTry)}
	facts.Emergency, err = policy.NewEmergencySnapshot(current, domain.Tick(emergency.Context.GetTick()), emergency.Facts)
	if err != nil {
		return out, err
	}
	facts.Doctor = NewTendDoctorFacts(tend.Doctor(), doctorRow, doctorToken)
	facts.Patient = NewTendPatientFacts(tend.Patient(), patientRow, patientToken)
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func NewTendDoctorFacts(pawn domain.PawnID, row *n.PawnState, token string) policy.TendDoctorFacts {
	facts := policy.TendDoctorFacts{Pawn: pawn, SnapshotToken: token, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "player_forced") && !boundary.IssueField(row.Job.Issues, "queued_jobs") && !boundary.IssueField(row.Job.Issues, "def_name") {
		facts.PlayerForced, facts.QueuedJobs = boundary.FactBool(row.Job.PlayerForced), boundary.FactUint(row.Job.QueuedJobs)
		if row.Job.DefName != nil {
			facts.ExistingJobDef = domain.Known(row.Job.GetDefName())
		}
	}
	if biography := row.Biography; biography != nil && !boundary.IssueField(biography.Issues, "skills") {
		facts.MedicineSkill, facts.MedicineSkillDisabled = medicineSkillLevel(biography.Skills)
	}
	if settings := row.Settings; settings != nil && !boundary.IssueField(settings.Issues, "work") {
		facts.DoctorWorkEnabled, facts.DoctorWorkOverrideDisabled = doctorWorkFacts(settings.Work)
	}
	return facts
}
func NewTendPatientFacts(pawn domain.PawnID, row *n.PawnState, token string) policy.TendPatientFacts {
	facts := policy.TendPatientFacts{Pawn: pawn, SnapshotToken: token, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed)}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "def_name") && row.Job.DefName != nil {
		facts.ExistingJobDef = domain.Known(row.Job.GetDefName())
	}
	if health := row.Health; health != nil && !boundary.IssueField(health.Issues, "health") {
		facts.NeedsTend, facts.Bleeding, facts.LifeThreatening = boundary.FactBool(health.NeedsTend), boundary.FactBool(health.Bleeding), boundary.FactBool(health.LifeThreatening)
		if health.HoursUntilDeathFromBloodLoss != nil {
			facts.HoursUntilDeathFromBloodLoss = domain.Known(health.GetHoursUntilDeathFromBloodLoss())
		}
	}
	if settings := row.Settings; settings != nil && !boundary.IssueField(settings.Issues, "medical_care") && settings.MedicalCare != nil {
		facts.NoCare = domain.Known(settings.GetMedicalCare() == "NoCare")
	}
	return facts
}

func (b *TendBoundary) attempt(dispatch executor.TendDispatch) (bridge.PawnOrderAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	tend, ok := p.Action.Tend()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Doctor != tend.Doctor() || admission.Patient != tend.Patient() || admission.Tick > p.Tick || !boundary.ValidID(admission.DoctorSnapshotToken) || !boundary.ValidID(admission.PatientSnapshotToken) {
		return bridge.PawnOrderAttempt{}, executor.ErrEvidence
	}
	return bridge.PawnOrderAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}, NativeGeneration: uint64(p.Snapshot.Native), Owner: &a.Owner{ControllerSessionId: proto.String(b.session), PlayerDirection: proto.Uint64(uint64(p.Snapshot.Direction))}, PawnID: string(tend.Doctor()), TargetID: string(tend.Patient()), Kind: o.PawnOrderKind_PAWN_ORDER_KIND_TEND, RequireSafeStorage: false}, nil
}

func tendJob(job *r.JobEffect, dispatch executor.TendDispatch) error {
	if job == nil {
		return nil
	}
	tend, _ := dispatch.Attempt.Action.Tend()
	if job.GetPawnId() != string(tend.Doctor()) || job.GetTargetA().GetThingId() != string(tend.Patient()) || job.GetJobDef() != "TendPatient" || job.JobId == nil || job.GetJobId() < 0 {
		return executor.ErrEvidence
	}
	if job.Drafted != nil && job.GetDrafted() || job.DraftClaimId != nil || job.DraftOwner != nil {
		return executor.ErrEvidence
	}
	return nil
}

func (b *TendBoundary) TendPatient(ctx context.Context, dispatch executor.TendDispatch) (executor.Receipt, error) {
	return boundary.DispatchPawnOrder(ctx, b.leases, b.writer, dispatch.Attempt,
		func() (bridge.PawnOrderAttempt, error) { return b.attempt(dispatch) },
		func(attempt bridge.PawnOrderAttempt) *o.PawnTargetOrder {
			return tendCommand(attempt.PawnID, attempt.TargetID, dispatch.Admission.DoctorSnapshotToken, dispatch.Admission.PatientSnapshotToken)
		},
		func(receipt *r.Receipt) error { return b.checkReceipt(receipt, dispatch) },
	)
}

func (b *TendBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.TendDispatch) error {
	p := dispatch.Attempt
	if err := boundary.Admission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	job := boundary.ReceiptJob(receipt)
	return tendJob(job, dispatch)
}

var _ executor.TendBoundary = (*TendBoundary)(nil)
