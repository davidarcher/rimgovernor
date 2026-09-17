package bridge

import (
	"context"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type ResearchSelectAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Generation uint64
	Project    string
	Token      string
}
type ResearchSelectControl struct{ client *Client }

func NewResearchSelectControl(client *Client) (*ResearchSelectControl, error) {
	if client == nil {
		return nil, contract("research select client missing")
	}
	return &ResearchSelectControl{client}, nil
}
func validResearchSelect(project, token string) error {
	if validID(project) != nil || validID(token) != nil {
		return contract("invalid research select target")
	}
	return nil
}
func researchSelectOperation(project, token string) *op.Operation {
	return &op.Operation{Command: &op.Operation_SelectResearch{SelectResearch: &op.SelectResearch{ProjectDef: proto.String(project), ExpectedSnapshotToken: proto.String(token)}}}
}
func (client *Client) PreviewResearchSelect(ctx context.Context, identity *c.Identity, project, token string) (*op.PreviewReply, Result, error) {
	if ValidateIdentity(identity) != nil || validResearchSelect(project, token) != nil {
		return nil, Result{}, contract("invalid research select preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: researchSelectOperation(project, token)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown research select preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || !v.GetAccepted() || v.Preparation != nil || v.Projected != nil || buildingContext(v.Context, identity, 0, false) != nil {
		return nil, raw, contract("invalid research select preview evidence")
	}
	return reply, raw, nil
}
func (writer *ResearchSelectControl) SelectResearch(ctx context.Context, pre *a.WritePrecondition, project, token string) (*op.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validResearchSelect(project, token) != nil {
		return nil, Result{}, contract("invalid research select execution")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: researchSelectOperation(project, token)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown research select execute fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetReceipt()
	if v == nil {
		return nil, raw, contract("research select owner mismatch")
	}
	err = researchSelectReceipt(v, ResearchSelectAttempt{pre.Identity, pre.Attempt, pre.GetExpectedGeneration(), project, token})
	return reply, raw, err
}
func validResearchSelectAttempt(w ResearchSelectAttempt) error {
	if ValidateIdentity(w.Identity) != nil || buildingAttempt(w.Attempt) != nil || w.Generation == 0 {
		return contract("invalid research select attempt")
	}
	return validResearchSelect(w.Project, w.Token)
}
func researchSelectEffect(v *r.EffectEvidence, project string, applied bool) error {
	effect := v.GetResearch()
	if effect == nil || buildingUnknown(v) != nil {
		return contract("research select effect missing")
	}
	if applied {
		if effect.CurrentProjectDef == nil || effect.GetCurrentProjectDef() != project || effect.PreviousProjectDef != nil && effect.GetPreviousProjectDef() != "" {
			return contract("research select effect mismatch")
		}
	}
	return nil
}

// ValidateResearchSelectEffect checks a lookup/progress ResearchEffect against
// the project this attempt targeted; see ValidateAcquisitionEffect for the
// analogous CAS-token direct-write shape.
func ValidateResearchSelectEffect(v *r.EffectEvidence, project string, applied bool) error {
	return researchSelectEffect(v, project, applied)
}
func researchSelectReceipt(v *r.Receipt, w ResearchSelectAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.AdmittedContext, w.Identity, w.Generation, true) != nil {
		return contract("research select admission mismatch")
	}
	switch out := v.Outcome.(type) {
	case *r.Receipt_Applied:
		return researchSelectEffect(out.Applied.GetObserved(), w.Project, true)
	case *r.Receipt_Uncertain:
		if out.Uncertain == nil {
			return contract("research select uncertainty missing")
		}
		if out.Uncertain.LastObserved != nil {
			return researchSelectEffect(out.Uncertain.LastObserved, w.Project, false)
		}
		return nil
	default:
		return contract("unsupported research select receipt")
	}
}
func (client *Client) LookupResearchSelect(ctx context.Context, w ResearchSelectAttempt) (*r.LookupReply, Result, error) {
	if err := validResearchSelectAttempt(w); err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown research select lookup fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = researchSelectReceipt(v.Receipt, w)
	case *r.LookupReply_Unknown:
		err = buildingContext(v.Unknown.GetContext(), w.Identity, 0, false)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, w.Attempt) {
			return nil, raw, contract("research select in-flight mismatch")
		}
		err = buildingContext(v.InFlight.AdmittedContext, w.Identity, w.Generation, true)
	default:
		err = contract("research select lookup outcome missing")
	}
	return reply, raw, err
}
func (client *Client) ObserveResearchSelectProgress(ctx context.Context, w ResearchSelectAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	if validResearchSelectAttempt(w) != nil {
		return nil, Result{}, contract("research select observation admission mismatch")
	}
	if admitted != nil {
		if err := researchSelectReceipt(admitted, w); err != nil {
			return nil, Result{}, err
		}
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown research select progress fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.Context, w.Identity, 0, false) != nil || (admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick()) {
		return nil, raw, contract("research select progress scope mismatch")
	}
	switch out := v.Effect.(type) {
	case *r.Progress_Unknown:
		if out.Unknown == nil {
			return nil, raw, contract("missing research select uncertainty")
		}
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() {
			return nil, raw, contract("incomplete research select completion")
		}
		err = researchSelectEffect(out.Completed.GetEvidence(), w.Project, true)
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || out.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return nil, raw, contract("unverified research select failure")
		}
		err = researchSelectEffect(out.Unsuccessful.GetEvidence(), w.Project, false)
	default:
		err = contract("unsupported research select progress")
	}
	return reply, raw, err
}
