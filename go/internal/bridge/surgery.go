package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// surgeryCareWire converts the domain-level expected medical care policy
// bucket to the wire enum, kept in sync by convention the same way
// recoveryServiceMethodWire tracks domain.RecoveryMethod.
func surgeryCareWire(care domain.MedicalCare) o.MedicalCare {
	switch care {
	case domain.MedicalCareNoCare:
		return o.MedicalCare_MEDICAL_CARE_NO_CARE
	case domain.MedicalCareNoMedicine:
		return o.MedicalCare_MEDICAL_CARE_NO_MEDICINE
	case domain.MedicalCareHerbalOrWorse:
		return o.MedicalCare_MEDICAL_CARE_HERBAL_OR_WORSE
	case domain.MedicalCareNormalOrWorse:
		return o.MedicalCare_MEDICAL_CARE_NORMAL_OR_WORSE
	case domain.MedicalCareBest:
		return o.MedicalCare_MEDICAL_CARE_BEST
	default:
		return o.MedicalCare_MEDICAL_CARE_UNSPECIFIED
	}
}

// SurgeryAttempt carries the exact already-selected patient/recipe/part triple
// the player explicitly requested. The native contract is the typed
// QueueSurgery operation (contracts/proto/operations.proto), the same one the
// legacy JSON home/medical_operations tool (MedicalOperationsTool.cs) drives
// via HealthCardUtility.CreateSurgeryBill.
type SurgeryAttempt struct {
	Identity     *c.Identity
	Attempt      *c.AttemptKey
	Generation   uint64
	Patient      string
	PatientToken string
	Recipe       string
	Part         int32
	HealthToken  string
	Care         domain.MedicalCare
}

func surgeryOperation(patient, patientToken, recipe string, part int32, healthToken string, care domain.MedicalCare) *o.Operation {
	return &o.Operation{Command: &o.Operation_QueueSurgery{QueueSurgery: &o.QueueSurgery{
		Patient: gearEntity(patient, patientToken), RecipeDef: proto.String(recipe), PartIndex: proto.Int32(part),
		ExpectedHealthToken: proto.String(healthToken), ExpectedCare: surgeryCareWire(care).Enum(),
	}}}
}

func surgeryCommand(patient, patientToken, recipe, healthToken string, part int32, care domain.MedicalCare) error {
	if validID(patient) != nil || validID(patientToken) != nil || validID(recipe) != nil || validID(healthToken) != nil || part < -1 {
		return contract("invalid surgery command")
	}
	if !domain.ValidMedicalCare(care) {
		return contract("invalid surgery expected care")
	}
	return nil
}

// SurgeryTarget refreshes the patient's current native-computed
// health-signature CAS token (hediff/body-part state) via an unconstrained
// dry-run preview, mirroring Python's inspect_native('home/medical_operations',
// ..., dryRun=True). This has no expected-token precondition to check because
// its entire purpose is establishing that baseline fresh, the same role
// ReadBedTarget plays for a bed's snapshot token before PreviewBedAssign.
type SurgeryTarget struct {
	Context     *c.ObservationContext
	HealthToken string
	Accepted    bool
}

// ReadSurgeryTarget previews the exact patient/recipe/part triple with no
// expected health token or care supplied, returning the native-computed
// current health signature and whether native reports the recipe presently
// queueable.
func (client *Client) ReadSurgeryTarget(ctx context.Context, identity *c.Identity, patient, patientToken, recipe string, part int32) (SurgeryTarget, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return SurgeryTarget{}, Result{}, err
	}
	if validID(patient) != nil || validID(patientToken) != nil || validID(recipe) != nil || part < -1 {
		return SurgeryTarget{}, Result{}, contract("invalid surgery target")
	}
	identity = proto.Clone(identity).(*c.Identity)
	op := &o.Operation{Command: &o.Operation_QueueSurgery{QueueSurgery: &o.QueueSurgery{
		Patient: gearEntity(patient, patientToken), RecipeDef: proto.String(recipe), PartIndex: proto.Int32(part),
	}}}
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: op}, reply)
	if err != nil {
		return SurgeryTarget{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return SurgeryTarget{}, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.PreviewReply_Failure:
		return SurgeryTarget{}, raw, failure(v.Failure, raw)
	case *o.PreviewReply_Evaluated:
		value := v.Evaluated
		if value == nil {
			return SurgeryTarget{}, raw, contract("surgery target preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			return SurgeryTarget{}, raw, err
		}
		effect := value.Projected.GetSurgery()
		if value.Accepted == nil || effect == nil || effect.PatientId == nil || effect.GetPatientId() != patient ||
			effect.RecipeDef == nil || effect.GetRecipeDef() != recipe || effect.PartIndex == nil || effect.GetPartIndex() != part ||
			effect.HealthToken == nil || validID(effect.GetHealthToken()) != nil {
			return SurgeryTarget{}, raw, contract("surgery target facts missing")
		}
		return SurgeryTarget{Context: value.Context, HealthToken: effect.GetHealthToken(), Accepted: value.GetAccepted()}, raw, nil
	default:
		return SurgeryTarget{}, raw, contract("surgery target outcome missing")
	}
}

// PreviewSurgery checks an exact already-selected patient/recipe/part triple;
// acceptance is not authority.
func (client *Client) PreviewSurgery(ctx context.Context, identity *c.Identity, patient, patientToken, recipe string, part int32, healthToken string, care domain.MedicalCare) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := surgeryCommand(patient, patientToken, recipe, healthToken, part, care); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: surgeryOperation(patient, patientToken, recipe, part, healthToken, care)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.PreviewReply_Failure:
		err = failure(v.Failure, raw)
	case *o.PreviewReply_Evaluated:
		value := v.Evaluated
		if value == nil {
			return reply, raw, contract("surgery preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		effect := value.Projected.GetSurgery()
		if value.Accepted == nil || !diagnostic(value.Reason) || value.Preparation != nil || effect == nil {
			err = contract("surgery preview facts missing")
			break
		}
		if _, err = surgeryEvidence(value.Projected, SurgeryAttempt{Patient: patient, Recipe: recipe, Part: part}); err != nil {
			break
		}
	default:
		err = contract("surgery preview outcome missing")
	}
	return reply, raw, err
}

func surgeryAttempt(v SurgeryAttempt) (SurgeryAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return SurgeryAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return SurgeryAttempt{}, err
	}
	if v.Generation == 0 {
		return SurgeryAttempt{}, contract("surgery admission owner or generation mismatch")
	}
	if err := surgeryCommand(v.Patient, v.PatientToken, v.Recipe, v.HealthToken, v.Part, v.Care); err != nil {
		return SurgeryAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	return v, nil
}

// surgeryEvidence validates the exact identity fields of an observed surgery
// effect (patient/recipe/part) match the admitted attempt. Bill_id, queued,
// health_token and bill_stack evolve over the bill's lifecycle (queued, then
// completed/cancelled/failed) and are not compared against a fixed
// expectation the way BedAssign's boolean Assigned effect is.
func surgeryEvidence(evidence *r.EffectEvidence, expected SurgeryAttempt) (*r.SurgeryEffect, error) {
	surgery := evidence.GetSurgery()
	if surgery == nil || surgery.PatientId == nil || surgery.GetPatientId() != expected.Patient ||
		surgery.RecipeDef == nil || surgery.GetRecipeDef() != expected.Recipe ||
		surgery.PartIndex == nil || surgery.GetPartIndex() != expected.Part {
		return nil, contract("surgery patient, recipe or part mismatch")
	}
	return surgery, nil
}

func surgeryReceipt(v *r.Receipt, expected SurgeryAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("surgery admission mismatch")
	}
	if err := buildingContext(v.AdmittedContext, expected.Identity, expected.Generation, true); err != nil {
		return err
	}
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil {
			return contract("surgery applied missing")
		}
		_, err := surgeryEvidence(outcome.Applied.GetObserved(), expected)
		return err
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return contract("surgery uncertainty missing")
		}
		if outcome.Uncertain.LastObserved != nil {
			_, err := surgeryEvidence(outcome.Uncertain.LastObserved, expected)
			return err
		}
		return nil
	default:
		return contract("unsupported surgery receipt")
	}
}

type SurgeryWriter struct{ client *Client }

func NewSurgeryWriter(client *Client) (*SurgeryWriter, error) {
	if client == nil {
		return nil, contract("surgery client missing")
	}
	return &SurgeryWriter{client}, nil
}

// ApplySurgery dispatches one already-admitted operation bill request.
func (writer *SurgeryWriter) ApplySurgery(ctx context.Context, pre *a.WritePrecondition, patient, patientToken, recipe string, part int32, healthToken string, care domain.MedicalCare) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 {
		return nil, Result{}, contract("invalid surgery execution")
	}
	if err := surgeryCommand(patient, patientToken, recipe, healthToken, part, care); err != nil {
		return nil, Result{}, err
	}
	expected, err := surgeryAttempt(SurgeryAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Generation: pre.GetExpectedGeneration(), Patient: patient, PatientToken: patientToken, Recipe: recipe, Part: part, HealthToken: healthToken, Care: care})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: surgeryOperation(patient, patientToken, recipe, part, healthToken, care)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = surgeryReceipt(v.Receipt, expected)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("surgery execute outcome missing")
	}
	return reply, raw, err
}

func (client *Client) LookupSurgery(ctx context.Context, w SurgeryAttempt) (*r.LookupReply, Result, error) {
	expected, err := surgeryAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = surgeryReceipt(v.Receipt, expected)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, expected.Attempt) {
			err = contract("surgery in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, expected.Identity, expected.Generation, true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("surgery unknown context missing")
		} else {
			err = buildingContext(v.Unknown.Context, expected.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("surgery lookup outcome missing")
	}
	return reply, raw, err
}

func (client *Client) ObserveSurgeryProgress(ctx context.Context, w SurgeryAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := surgeryAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = surgeryReceipt(admitted, expected); err != nil {
			return nil, Result{}, err
		}
		admitted = proto.Clone(admitted).(*r.Receipt)
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *r.ProgressReply_Progress:
		err = surgeryProgress(v.Progress, expected, admitted)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("surgery progress outcome missing")
	}
	return reply, raw, err
}

func surgeryProgress(v *r.Progress, expected SurgeryAttempt, admitted *r.Receipt) error {
	if v == nil || v.CompleteInspection == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("surgery progress attempt mismatch")
	}
	if err := buildingContext(v.Context, expected.Identity, 0, false); err != nil {
		return err
	}
	if admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("surgery progress predates admission")
	}
	switch outcome := v.Effect.(type) {
	case *r.Progress_Unknown:
		if outcome.Unknown == nil || !diagnostic(outcome.Unknown.Reason) {
			return contract("surgery unknown progress missing")
		}
		return nil
	case *r.Progress_Pending:
		if outcome.Pending == nil || !v.GetCompleteInspection() {
			return contract("surgery pending missing")
		}
		_, err := surgeryEvidence(outcome.Pending.Evidence, expected)
		return err
	case *r.Progress_Completed:
		if outcome.Completed == nil || !v.GetCompleteInspection() {
			return contract("surgery completed missing")
		}
		_, err := surgeryEvidence(outcome.Completed.Evidence, expected)
		return err
	case *r.Progress_Absent:
		if outcome.Absent == nil || validID(outcome.Absent.GetInspectionToken()) != nil || !v.GetCompleteInspection() {
			return contract("surgery absence lacks inspection")
		}
		return nil
	case *r.Progress_Unsuccessful:
		if outcome.Unsuccessful == nil || outcome.Unsuccessful.Reason == nil || outcome.Unsuccessful.GetReason() == 0 || !v.GetCompleteInspection() || !diagnostic(outcome.Unsuccessful.Detail) {
			return contract("surgery unsuccessful reason missing")
		}
		_, err := surgeryEvidence(outcome.Unsuccessful.Evidence, expected)
		return err
	default:
		return contract("surgery progress state missing")
	}
}
