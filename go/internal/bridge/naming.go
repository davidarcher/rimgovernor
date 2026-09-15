package bridge

import (
	"context"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// NamingAttempt targets the colony-wide, one-shot initial faction/settlement
// naming dialog: the exact observed window ID plus suggestions stand in for
// an EntityPrecondition/snapshot token, exactly like ResearchSelectAttempt's
// project/token pair.
type NamingAttempt struct {
	Identity       *c.Identity
	Attempt        *c.AttemptKey
	Generation     uint64
	WindowID       int32
	FactionName    string
	SettlementName string
}
type NamingControl struct{ client *Client }

func NewNamingControl(client *Client) (*NamingControl, error) {
	if client == nil {
		return nil, contract("naming client missing")
	}
	return &NamingControl{client}, nil
}
func validNaming(windowID int32, factionName, settlementName string) error {
	if windowID < 0 || validID(factionName) != nil || validID(settlementName) != nil {
		return contract("invalid naming target")
	}
	return nil
}
func namingOperation(windowID int32, factionName, settlementName string) *op.Operation {
	return &op.Operation{Command: &op.Operation_ConfirmColonyNames{ConfirmColonyNames: &op.ConfirmColonyNames{
		WindowId: proto.Int32(windowID), FactionName: proto.String(factionName), SettlementName: proto.String(settlementName)}}}
}
func (client *Client) PreviewConfirmColonyNames(ctx context.Context, identity *c.Identity, windowID int32, factionName, settlementName string) (*op.PreviewReply, Result, error) {
	if ValidateIdentity(identity) != nil || validNaming(windowID, factionName, settlementName) != nil {
		return nil, Result{}, contract("invalid naming preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: namingOperation(windowID, factionName, settlementName)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown naming preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || !v.GetAccepted() || v.Preparation != nil || v.Projected != nil || buildingContext(v.Context, identity, 0, false) != nil {
		return nil, raw, contract("invalid naming preview evidence")
	}
	return reply, raw, nil
}
func (writer *NamingControl) ConfirmColonyNames(ctx context.Context, pre *a.WritePrecondition, windowID int32, factionName, settlementName string) (*op.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validNaming(windowID, factionName, settlementName) != nil {
		return nil, Result{}, contract("invalid naming execution")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: namingOperation(windowID, factionName, settlementName)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown naming execute fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetReceipt()
	if v == nil {
		return nil, raw, contract("naming owner mismatch")
	}
	err = namingReceipt(v, NamingAttempt{pre.Identity, pre.Attempt, pre.GetExpectedGeneration(), windowID, factionName, settlementName})
	return reply, raw, err
}
func validNamingAttempt(w NamingAttempt) error {
	if ValidateIdentity(w.Identity) != nil || buildingAttempt(w.Attempt) != nil || w.Generation == 0 {
		return contract("invalid naming attempt")
	}
	return validNaming(w.WindowID, w.FactionName, w.SettlementName)
}
func namingEffect(v *r.EffectEvidence, w NamingAttempt, applied bool) error {
	effect := v.GetNaming()
	if effect == nil || buildingUnknown(v) != nil || effect.GetWindowId() != w.WindowID {
		return contract("naming effect missing or window mismatch")
	}
	if applied && (!effect.GetConfirmed() || effect.GetFactionName() != w.FactionName || effect.GetSettlementName() != w.SettlementName) {
		return contract("naming effect mismatch")
	}
	return nil
}
func namingReceipt(v *r.Receipt, w NamingAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.AdmittedContext, w.Identity, w.Generation, true) != nil {
		return contract("naming admission mismatch")
	}
	switch out := v.Outcome.(type) {
	case *r.Receipt_Applied:
		return namingEffect(out.Applied.GetObserved(), w, true)
	case *r.Receipt_Uncertain:
		if out.Uncertain == nil {
			return contract("naming uncertainty missing")
		}
		if out.Uncertain.LastObserved != nil {
			return namingEffect(out.Uncertain.LastObserved, w, false)
		}
		return nil
	default:
		return contract("unsupported naming receipt")
	}
}
func (client *Client) LookupConfirmColonyNames(ctx context.Context, w NamingAttempt) (*r.LookupReply, Result, error) {
	if err := validNamingAttempt(w); err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown naming lookup fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = namingReceipt(v.Receipt, w)
	case *r.LookupReply_Unknown:
		err = buildingContext(v.Unknown.GetContext(), w.Identity, 0, false)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, w.Attempt) {
			return nil, raw, contract("naming in-flight mismatch")
		}
		err = buildingContext(v.InFlight.AdmittedContext, w.Identity, w.Generation, true)
	default:
		err = contract("naming lookup outcome missing")
	}
	return reply, raw, err
}
func (client *Client) ObserveConfirmColonyNamesProgress(ctx context.Context, w NamingAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	if validNamingAttempt(w) != nil {
		return nil, Result{}, contract("naming observation admission mismatch")
	}
	if admitted != nil {
		if err := namingReceipt(admitted, w); err != nil {
			return nil, Result{}, err
		}
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown naming progress fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.Context, w.Identity, 0, false) != nil || (admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick()) {
		return nil, raw, contract("naming progress scope mismatch")
	}
	switch out := v.Effect.(type) {
	case *r.Progress_Unknown:
		if out.Unknown == nil {
			return nil, raw, contract("missing naming uncertainty")
		}
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() {
			return nil, raw, contract("incomplete naming completion")
		}
		err = namingEffect(out.Completed.GetEvidence(), w, true)
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || out.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return nil, raw, contract("unverified naming failure")
		}
		err = namingEffect(out.Unsuccessful.GetEvidence(), w, false)
	default:
		err = contract("unsupported naming progress")
	}
	return reply, raw, err
}
