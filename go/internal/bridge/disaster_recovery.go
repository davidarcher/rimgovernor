package bridge

import (
	"context"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// RecoveryServiceMethod names RecoverDisasterServices' three native service
// jobs: ordinary repair, breakdown restoration and refuel. The native
// contract is NativeRecoveryOperations.cs
// (integrations/rimgovernor-native/src/Bridge/Protocol), wired onto
// Operation_RecoverService in NativeOperationTools.cs's Execute/Preview
// dispatch. It reuses the same real WorkGiver_Repair/WorkGiver_FixBrokenDownBuilding/
// WorkGiver_Refuel path the legacy JSON home/recover_service tool
// (RecoveryTools.cs) issues.
type RecoveryServiceMethod int32

const (
	RecoveryServiceUnspecified RecoveryServiceMethod = iota
	RecoveryServiceRepair
	RecoveryServiceBreakdown
	RecoveryServiceRefuel
)

func (m RecoveryServiceMethod) wire() o.ServiceMethod {
	switch m {
	case RecoveryServiceRepair:
		return o.ServiceMethod_SERVICE_METHOD_REPAIR
	case RecoveryServiceBreakdown:
		return o.ServiceMethod_SERVICE_METHOD_BREAKDOWN
	case RecoveryServiceRefuel:
		return o.ServiceMethod_SERVICE_METHOD_REFUEL
	default:
		return o.ServiceMethod_SERVICE_METHOD_UNSPECIFIED
	}
}

// recoveryServiceJobDef maps each method to the native jobs the
// corresponding WorkGiver issues. Refuel covers WorkGiver_Refuel and its
// turret subclass (Core's RearmTurrets giver, the only one that rearms a
// Building_Turret barrel, #205), each with an atomic variant for a comp
// that takes its whole fuel load at once.
var recoveryServiceJobDef = map[RecoveryServiceMethod][]string{
	RecoveryServiceRepair:    {"Repair"},
	RecoveryServiceBreakdown: {"FixBrokenDownBuilding"},
	RecoveryServiceRefuel:    {"Refuel", "RefuelAtomic", "RearmTurret", "RearmTurretAtomic"},
}

func recoveryServiceJobDefAllowed(method RecoveryServiceMethod, def string) bool {
	for _, allowed := range recoveryServiceJobDef[method] {
		if def == allowed {
			return true
		}
	}
	return false
}

type RecoveryServiceAttempt struct {
	Identity    *c.Identity
	Attempt     *c.AttemptKey
	Generation  uint64
	Pawn, Thing string
	PawnToken   string
	ThingToken  string
	Method      RecoveryServiceMethod
}

// recoveryServiceOperation builds the RecoverService command. The target's
// CAS token travels on the decoupled ExpectedTargetSnapshotToken field
// (mirroring QueueSurgery's expected_health_token), not on Target's own
// EntityPrecondition, which carries identity only: the target's
// recovery-specific token has no existing observation read that could
// produce it ahead of time, so ReadRecoveryServiceTarget discovers it via an
// unconstrained preview (thingToken == "" here, field left unset).
func recoveryServiceOperation(pawn, pawnToken, thing, thingToken string, method RecoveryServiceMethod) *o.Operation {
	wireMethod := method.wire()
	command := &o.RecoverService{Target: &o.EntityPrecondition{EntityId: proto.String(thing)}, Pawn: gearEntity(pawn, pawnToken), Method: &wireMethod}
	if thingToken != "" {
		command.ExpectedTargetSnapshotToken = proto.String(thingToken)
	}
	return &o.Operation{Command: &o.Operation_RecoverService{RecoverService: command}}
}

func recoveryServiceCommand(pawn, pawnToken, thing, thingToken string, method RecoveryServiceMethod) error {
	if validID(pawn) != nil || validID(pawnToken) != nil || validID(thing) != nil || validID(thingToken) != nil || pawn == thing {
		return contract("invalid recovery service command")
	}
	if _, ok := recoveryServiceJobDef[method]; !ok {
		return contract("invalid recovery service method")
	}
	return nil
}

func recoveryServiceDiscoveryCommand(pawn, pawnToken, thing string, method RecoveryServiceMethod) error {
	if validID(pawn) != nil || validID(pawnToken) != nil || validID(thing) != nil || pawn == thing {
		return contract("invalid recovery service target discovery")
	}
	if _, ok := recoveryServiceJobDef[method]; !ok {
		return contract("invalid recovery service method")
	}
	return nil
}

// RecoveryServiceTarget carries one exact structure's freshly
// native-computed recovery-service CAS token (NativeRecoveryOperations.Token:
// hit points, breakdown state, fuel level, forbidden, burning), discovered
// via an unconstrained RecoverService preview -- no expected target token
// supplied -- the same way bridge.SurgeryTarget/ReadSurgeryTarget establishes
// a patient's health-signature baseline for QueueSurgery. The general upkeep
// census strips snapshot tokens, and the row-level building read
// (NativeBuildingObservationTools, bridge.ReadConstructionBuildings) only
// ever populates a row-level "building-" hash over unrelated fields, never
// the nested EntityRef.Snapshot this token would need -- so this preview
// round-trip is the only source.
type RecoveryServiceTarget struct {
	Context   *c.ObservationContext
	Structure string
	Token     string
	Accepted  bool
}

// ReadRecoveryServiceTarget previews the exact pawn/structure/method triple
// with no expected target token supplied, returning the native-computed
// current recovery-service CAS token and whether native reports the method
// presently applicable. Callers must still run PreviewRecoveryService (or go
// straight to admission) with the returned token before dispatch; this call
// establishes the baseline, it is not authority.
func (client *Client) ReadRecoveryServiceTarget(ctx context.Context, identity *c.Identity, pawn, pawnToken, structure string, method RecoveryServiceMethod) (RecoveryServiceTarget, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return RecoveryServiceTarget{}, Result{}, err
	}
	if err := recoveryServiceDiscoveryCommand(pawn, pawnToken, structure, method); err != nil {
		return RecoveryServiceTarget{}, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: recoveryServiceOperation(pawn, pawnToken, structure, "", method)}, reply)
	if err != nil {
		return RecoveryServiceTarget{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return RecoveryServiceTarget{}, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.PreviewReply_Failure:
		return RecoveryServiceTarget{}, raw, failure(v.Failure, raw)
	case *o.PreviewReply_Evaluated:
		value := v.Evaluated
		if value == nil {
			return RecoveryServiceTarget{}, raw, contract("recovery service target preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			return RecoveryServiceTarget{}, raw, err
		}
		job := value.Projected.GetJob()
		if value.Accepted == nil || job == nil || job.GetPawnId() != pawn || job.GetTargetA().GetThingId() != structure ||
			job.TargetSnapshotToken == nil || validID(job.GetTargetSnapshotToken()) != nil {
			return RecoveryServiceTarget{}, raw, contract("recovery service target facts missing")
		}
		return RecoveryServiceTarget{Context: value.Context, Structure: structure, Token: job.GetTargetSnapshotToken(), Accepted: value.GetAccepted()}, raw, nil
	default:
		return RecoveryServiceTarget{}, raw, contract("recovery service target outcome missing")
	}
}

// PreviewRecoveryService checks an exact already-selected pawn/building
// repair, breakdown restoration or refuel order; acceptance is not authority.
func (client *Client) PreviewRecoveryService(ctx context.Context, identity *c.Identity, pawn, pawnToken, thing, thingToken string, method RecoveryServiceMethod) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := recoveryServiceCommand(pawn, pawnToken, thing, thingToken, method); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: recoveryServiceOperation(pawn, pawnToken, thing, thingToken, method)}, reply)
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
			return reply, raw, contract("recovery service preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		job := value.Projected.GetJob()
		if value.Accepted == nil || !diagnostic(value.Reason) || value.Preparation != nil || job == nil {
			err = contract("recovery service preview facts missing")
			break
		}
		// A refused preview projects no job definition; an accepted one
		// projects one of the method's.
		if value.GetAccepted() && !recoveryServiceJobDefAllowed(method, job.GetJobDef()) || !value.GetAccepted() && job.GetJobDef() != "" {
			err = contract("recovery service preview projection mismatch")
			break
		}
		expected := &r.JobEffect{PawnId: proto.String(pawn), JobDef: proto.String(job.GetJobDef()), TargetA: &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: thing}}, CanTry: proto.Bool(value.GetAccepted()), Issued: proto.Bool(false), Verified: proto.Bool(false), TargetSnapshotToken: proto.String(thingToken)}
		if !proto.Equal(job, expected) {
			err = contract("recovery service preview projection mismatch")
		}
	default:
		err = contract("recovery service preview outcome missing")
	}
	return reply, raw, err
}

func recoveryServiceAttempt(v RecoveryServiceAttempt) (RecoveryServiceAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return RecoveryServiceAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return RecoveryServiceAttempt{}, err
	}
	if v.Generation == 0 {
		return RecoveryServiceAttempt{}, contract("recovery service admission owner or generation mismatch")
	}
	if err := recoveryServiceCommand(v.Pawn, v.PawnToken, v.Thing, v.ThingToken, v.Method); err != nil {
		return RecoveryServiceAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	return v, nil
}

func recoveryServiceEvidence(evidence *r.EffectEvidence, expected RecoveryServiceAttempt) (*r.JobEffect, error) {
	job := evidence.GetJob()
	if job == nil || job.PawnId == nil || job.GetPawnId() != expected.Pawn || job.TargetA == nil || job.TargetA.GetThingId() != expected.Thing {
		return nil, contract("recovery service pawn or target mismatch")
	}
	// The native service record's evidence carries the pawn-order fields
	// every issued job's does (drafted state and the resulting pawn
	// snapshot token) besides the job itself.
	allowed := &r.JobEffect{PawnId: job.PawnId, JobId: job.JobId, JobDef: job.JobDef, TargetA: job.TargetA, CanTry: job.CanTry, Drafted: job.Drafted, Issued: job.Issued, Verified: job.Verified, VerifiedReason: job.VerifiedReason, ResultingSnapshotToken: job.ResultingSnapshotToken}
	if !proto.Equal(job, allowed) || job.JobDef == nil || !recoveryServiceJobDefAllowed(expected.Method, job.GetJobDef()) {
		return nil, contract("recovery service effect fields missing or unsupported")
	}
	if job.VerifiedReason != nil && !diagnostic(job.VerifiedReason) {
		return nil, contract("recovery service verification reason invalid")
	}
	return job, nil
}

func recoveryServiceReceipt(v *r.Receipt, expected RecoveryServiceAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("recovery service admission mismatch")
	}
	if err := buildingContext(v.AdmittedContext, expected.Identity, expected.Generation, true); err != nil {
		return err
	}
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil {
			return contract("recovery service applied missing")
		}
		_, err := recoveryServiceEvidence(outcome.Applied.GetObserved(), expected)
		return err
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return contract("recovery service uncertainty missing")
		}
		if outcome.Uncertain.LastObserved != nil {
			_, err := recoveryServiceEvidence(outcome.Uncertain.LastObserved, expected)
			return err
		}
		return nil
	default:
		return contract("unsupported recovery service receipt")
	}
}

type RecoveryServiceWriter struct{ client *Client }

func NewRecoveryServiceWriter(client *Client) (*RecoveryServiceWriter, error) {
	if client == nil {
		return nil, contract("recovery service client missing")
	}
	return &RecoveryServiceWriter{client}, nil
}

// ApplyRecoveryService dispatches one already-admitted repair, breakdown
// restoration or refuel order.
func (writer *RecoveryServiceWriter) ApplyRecoveryService(ctx context.Context, pre *a.WritePrecondition, pawn, pawnToken, thing, thingToken string, method RecoveryServiceMethod) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 {
		return nil, Result{}, contract("invalid recovery service execution")
	}
	if err := recoveryServiceCommand(pawn, pawnToken, thing, thingToken, method); err != nil {
		return nil, Result{}, err
	}
	expected, err := recoveryServiceAttempt(RecoveryServiceAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Generation: pre.GetExpectedGeneration(), Pawn: pawn, Thing: thing, PawnToken: pawnToken, ThingToken: thingToken, Method: method})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: recoveryServiceOperation(pawn, pawnToken, thing, thingToken, method)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = recoveryServiceReceipt(v.Receipt, expected)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("recovery service execute outcome missing")
	}
	return reply, raw, err
}

func (client *Client) LookupRecoveryService(ctx context.Context, w RecoveryServiceAttempt) (*r.LookupReply, Result, error) {
	expected, err := recoveryServiceAttempt(w)
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
		err = recoveryServiceReceipt(v.Receipt, expected)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, expected.Attempt) {
			err = contract("recovery service in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, expected.Identity, expected.Generation, true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("recovery service unknown context missing")
		} else {
			err = buildingContext(v.Unknown.Context, expected.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("recovery service lookup outcome missing")
	}
	return reply, raw, err
}

func (client *Client) ObserveRecoveryServiceProgress(ctx context.Context, w RecoveryServiceAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := recoveryServiceAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = recoveryServiceReceipt(admitted, expected); err != nil {
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
		err = recoveryServiceProgress(v.Progress, expected, admitted)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("recovery service progress outcome missing")
	}
	return reply, raw, err
}

func recoveryServiceProgress(v *r.Progress, expected RecoveryServiceAttempt, admitted *r.Receipt) error {
	if v == nil || v.CompleteInspection == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("recovery service progress attempt mismatch")
	}
	if err := buildingContext(v.Context, expected.Identity, 0, false); err != nil {
		return err
	}
	if admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("recovery service progress predates admission")
	}
	switch outcome := v.Effect.(type) {
	case *r.Progress_Unknown:
		if outcome.Unknown == nil || !diagnostic(outcome.Unknown.Reason) {
			return contract("recovery service unknown progress missing")
		}
		return nil
	case *r.Progress_Pending:
		if outcome.Pending == nil || !v.GetCompleteInspection() {
			return contract("recovery service pending missing")
		}
		_, err := recoveryServiceEvidence(outcome.Pending.Evidence, expected)
		return err
	case *r.Progress_Completed:
		if outcome.Completed == nil || !v.GetCompleteInspection() {
			return contract("recovery service completed missing")
		}
		_, err := recoveryServiceEvidence(outcome.Completed.Evidence, expected)
		return err
	case *r.Progress_Absent:
		if outcome.Absent == nil || validID(outcome.Absent.GetInspectionToken()) != nil || !v.GetCompleteInspection() {
			return contract("recovery service absence lacks inspection")
		}
		return nil
	case *r.Progress_Unsuccessful:
		if outcome.Unsuccessful == nil || outcome.Unsuccessful.Reason == nil || outcome.Unsuccessful.GetReason() == 0 || !v.GetCompleteInspection() || !diagnostic(outcome.Unsuccessful.Detail) {
			return contract("recovery service unsuccessful reason missing")
		}
		_, err := recoveryServiceEvidence(outcome.Unsuccessful.Evidence, expected)
		return err
	default:
		return contract("recovery service progress state missing")
	}
}
