package bridge

import (
	"context"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// MoodReliefNeed names EnsureMood-*'s three ordinary native need jobs. The
// native contract is NativeMoodReliefOperations.cs
// (integrations/rimgovernor-native/src/Bridge/Protocol), wired onto
// Operation_RelieveNeed in NativeOperationTools.cs's Execute/Preview
// dispatch. It ports the legacy JSON home/relieve_need tool's eligibility
// checks (NeedReliefTool.cs) behind the typed Operations.RelieveNeed
// boundary: an accepted job does not prove need recovery.
type MoodReliefNeed int32

const (
	MoodReliefNeedUnspecified MoodReliefNeed = iota
	MoodReliefFood
	MoodReliefRest
	MoodReliefJoy
)

func (n MoodReliefNeed) wire() o.Need {
	switch n {
	case MoodReliefFood:
		return o.Need_NEED_FOOD
	case MoodReliefRest:
		return o.Need_NEED_REST
	case MoodReliefJoy:
		return o.Need_NEED_JOY
	default:
		return o.Need_NEED_UNSPECIFIED
	}
}

var moodReliefValid = map[MoodReliefNeed]bool{MoodReliefFood: true, MoodReliefRest: true, MoodReliefJoy: true}

// MoodReliefExpectedJob mirrors Python's expectedJob: either the pawn's
// exact current job load ID, or explicitly idle.
type MoodReliefExpectedJob struct {
	JobID *int32
	Idle  bool
}

func (e MoodReliefExpectedJob) valid() bool { return e.Idle != (e.JobID != nil) }

func (e MoodReliefExpectedJob) wire() *o.ExpectedJob {
	if e.Idle {
		return &o.ExpectedJob{State: &o.ExpectedJob_Idle{Idle: &o.Clear{}}}
	}
	return &o.ExpectedJob{State: &o.ExpectedJob_JobId{JobId: *e.JobID}}
}

type MoodReliefAttempt struct {
	Identity            *c.Identity
	Attempt             *c.AttemptKey
	Owner               *a.Owner
	Generation          uint64
	Pawn                string
	PawnToken           string
	Need                MoodReliefNeed
	ExpectedJob         MoodReliefExpectedJob
	ExpectedScheduleDef string
}

func moodReliefOperation(pawn, pawnToken string, need MoodReliefNeed, job MoodReliefExpectedJob, schedule string) *o.Operation {
	wireNeed := need.wire()
	return &o.Operation{Command: &o.Operation_RelieveNeed{RelieveNeed: &o.RelieveNeed{
		Pawn: gearEntity(pawn, pawnToken), Need: &wireNeed, ExpectedJob: job.wire(), ExpectedScheduleDef: proto.String(schedule),
	}}}
}

func moodReliefCommand(pawn, pawnToken string, need MoodReliefNeed, job MoodReliefExpectedJob, schedule string) error {
	if validID(pawn) != nil || validID(pawnToken) != nil || validID(schedule) != nil {
		return contract("invalid mood relief command")
	}
	if !moodReliefValid[need] {
		return contract("invalid mood relief need")
	}
	if !job.valid() {
		return contract("invalid mood relief expected job")
	}
	return nil
}

// PreviewMoodRelief checks an exact already-selected pawn/need relief order;
// acceptance is not authority.
func (client *Client) PreviewMoodRelief(ctx context.Context, identity *c.Identity, pawn, pawnToken string, need MoodReliefNeed, job MoodReliefExpectedJob, schedule string) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := moodReliefCommand(pawn, pawnToken, need, job, schedule); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: moodReliefOperation(pawn, pawnToken, need, job, schedule)}, reply)
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
			return reply, raw, contract("mood relief preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		effect := value.Projected.GetJob()
		if value.Accepted == nil || !diagnostic(value.Reason) || value.Preparation != nil || effect == nil {
			err = contract("mood relief preview facts missing")
			break
		}
		expected := &r.JobEffect{PawnId: proto.String(pawn), TargetA: &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: pawn}}, CanTry: proto.Bool(value.GetAccepted()), Issued: proto.Bool(false), Verified: proto.Bool(false)}
		if !proto.Equal(effect, expected) {
			err = contract("mood relief preview projection mismatch")
		}
	default:
		err = contract("mood relief preview outcome missing")
	}
	return reply, raw, err
}

func moodReliefAttempt(v MoodReliefAttempt) (MoodReliefAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return MoodReliefAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return MoodReliefAttempt{}, err
	}
	if err := authorityOwner(v.Owner); err != nil {
		return MoodReliefAttempt{}, err
	}
	if err := buildingUnknown(v.Owner); err != nil {
		return MoodReliefAttempt{}, err
	}
	if v.Generation == 0 || v.Owner.GetControllerSessionId() != v.Attempt.GetControllerSessionId() {
		return MoodReliefAttempt{}, contract("mood relief admission owner or generation mismatch")
	}
	if err := moodReliefCommand(v.Pawn, v.PawnToken, v.Need, v.ExpectedJob, v.ExpectedScheduleDef); err != nil {
		return MoodReliefAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	v.Owner = proto.Clone(v.Owner).(*a.Owner)
	return v, nil
}

func moodReliefEvidence(evidence *r.EffectEvidence, expected MoodReliefAttempt) (*r.JobEffect, error) {
	job := evidence.GetJob()
	if job == nil || job.PawnId == nil || job.GetPawnId() != expected.Pawn || job.TargetA == nil || job.TargetA.GetThingId() != expected.Pawn {
		return nil, contract("mood relief pawn mismatch")
	}
	allowed := &r.JobEffect{PawnId: job.PawnId, JobId: job.JobId, JobDef: job.JobDef, TargetA: job.TargetA, CanTry: job.CanTry, Issued: job.Issued, Verified: job.Verified, VerifiedReason: job.VerifiedReason}
	if !proto.Equal(job, allowed) {
		return nil, contract("mood relief effect fields missing or unsupported")
	}
	if job.VerifiedReason != nil && !diagnostic(job.VerifiedReason) {
		return nil, contract("mood relief verification reason invalid")
	}
	return job, nil
}

func moodReliefReceipt(v *r.Receipt, expected MoodReliefAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, expected.Attempt) || !proto.Equal(v.AuthorizingOwner, expected.Owner) {
		return contract("mood relief admission mismatch")
	}
	if err := buildingContext(v.AdmittedContext, expected.Identity, expected.Generation, true); err != nil {
		return err
	}
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil {
			return contract("mood relief applied missing")
		}
		_, err := moodReliefEvidence(outcome.Applied.GetObserved(), expected)
		return err
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return contract("mood relief uncertainty missing")
		}
		if outcome.Uncertain.LastObserved != nil {
			_, err := moodReliefEvidence(outcome.Uncertain.LastObserved, expected)
			return err
		}
		return nil
	default:
		return contract("unsupported mood relief receipt")
	}
}

type MoodReliefWriter struct{ client *Client }

func NewMoodReliefWriter(client *Client) (*MoodReliefWriter, error) {
	if client == nil {
		return nil, contract("mood relief client missing")
	}
	return &MoodReliefWriter{client}, nil
}

// ApplyMoodRelief dispatches one already-admitted need relief order.
func (writer *MoodReliefWriter) ApplyMoodRelief(ctx context.Context, pre *a.WritePrecondition, owner *a.Owner, pawn, pawnToken string, need MoodReliefNeed, job MoodReliefExpectedJob, schedule string) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validID(pre.GetLeaseId()) != nil {
		return nil, Result{}, contract("invalid mood relief execution")
	}
	if err := moodReliefCommand(pawn, pawnToken, need, job, schedule); err != nil {
		return nil, Result{}, err
	}
	expected, err := moodReliefAttempt(MoodReliefAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Owner: owner, Generation: pre.GetExpectedGeneration(), Pawn: pawn, PawnToken: pawnToken, Need: need, ExpectedJob: job, ExpectedScheduleDef: schedule})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: moodReliefOperation(pawn, pawnToken, need, job, schedule)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = moodReliefReceipt(v.Receipt, expected)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("mood relief execute outcome missing")
	}
	return reply, raw, err
}

func (client *Client) LookupMoodRelief(ctx context.Context, w MoodReliefAttempt) (*r.LookupReply, Result, error) {
	expected, err := moodReliefAttempt(w)
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
		err = moodReliefReceipt(v.Receipt, expected)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, expected.Attempt) {
			err = contract("mood relief in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, expected.Identity, expected.Generation, true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("mood relief unknown context missing")
		} else {
			err = buildingContext(v.Unknown.Context, expected.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("mood relief lookup outcome missing")
	}
	return reply, raw, err
}

func (client *Client) ObserveMoodReliefProgress(ctx context.Context, w MoodReliefAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := moodReliefAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = moodReliefReceipt(admitted, expected); err != nil {
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
		err = moodReliefProgress(v.Progress, expected, admitted)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("mood relief progress outcome missing")
	}
	return reply, raw, err
}

func moodReliefProgress(v *r.Progress, expected MoodReliefAttempt, admitted *r.Receipt) error {
	if v == nil || v.CompleteInspection == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("mood relief progress attempt mismatch")
	}
	if err := buildingContext(v.Context, expected.Identity, 0, false); err != nil {
		return err
	}
	if admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("mood relief progress predates admission")
	}
	switch outcome := v.Effect.(type) {
	case *r.Progress_Unknown:
		if outcome.Unknown == nil || !diagnostic(outcome.Unknown.Reason) {
			return contract("mood relief unknown progress missing")
		}
		return nil
	case *r.Progress_Pending:
		if outcome.Pending == nil || !v.GetCompleteInspection() {
			return contract("mood relief pending missing")
		}
		_, err := moodReliefEvidence(outcome.Pending.Evidence, expected)
		return err
	case *r.Progress_Completed:
		if outcome.Completed == nil || !v.GetCompleteInspection() {
			return contract("mood relief completed missing")
		}
		_, err := moodReliefEvidence(outcome.Completed.Evidence, expected)
		return err
	case *r.Progress_Absent:
		if outcome.Absent == nil || validID(outcome.Absent.GetInspectionToken()) != nil || !v.GetCompleteInspection() {
			return contract("mood relief absence lacks inspection")
		}
		return nil
	case *r.Progress_Unsuccessful:
		if outcome.Unsuccessful == nil || outcome.Unsuccessful.Reason == nil || outcome.Unsuccessful.GetReason() == 0 || !v.GetCompleteInspection() || !diagnostic(outcome.Unsuccessful.Detail) {
			return contract("mood relief unsuccessful reason missing")
		}
		_, err := moodReliefEvidence(outcome.Unsuccessful.Evidence, expected)
		return err
	default:
		return contract("mood relief progress state missing")
	}
}
