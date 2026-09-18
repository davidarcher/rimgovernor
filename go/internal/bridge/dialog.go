package bridge

import (
	"context"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	ob "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// maxDialogOptions bounds a ChoiceDialog census; RimWorld dialog trees never
// offer more than a handful of options per node. maxDialogOptionKeys bounds
// the translation keys native recovers for one label.
const maxDialogOptions, maxDialogOptionKeys = 32, 32

// DialogAttempt targets one exact option of the single force-pausing choice
// dialog (Verse.Dialog_NodeTree) the game opened by itself (#156): the
// observed window ID plus the option's list position and label stand in for
// an EntityPrecondition, exactly like NamingAttempt's window/suggestion pair.
type DialogAttempt struct {
	Identity    *c.Identity
	Attempt     *c.AttemptKey
	Generation  uint64
	WindowID    int32
	OptionIndex int32
	OptionLabel string
}
type DialogControl struct{ client *Client }

func NewDialogControl(client *Client) (*DialogControl, error) {
	if client == nil {
		return nil, contract("dialog client missing")
	}
	return &DialogControl{client}, nil
}
func validDialog(windowID, optionIndex int32, label string) error {
	if windowID < 0 || optionIndex < 0 || validID(label) != nil {
		return contract("invalid dialog target")
	}
	return nil
}
func dialogOperation(windowID, optionIndex int32, label string) *op.Operation {
	return &op.Operation{Command: &op.Operation_AnswerDialog{AnswerDialog: &op.AnswerDialog{
		WindowId: proto.Int32(windowID), OptionIndex: proto.Int32(optionIndex), OptionLabel: proto.String(label)}}}
}

// validateChoiceDialog admits the colony census's dialog section: every
// option carries its own list position, a bounded valid label, and the
// selectable/disabled evidence agrees with itself.
func validateChoiceDialog(v *ob.ChoiceDialog) error {
	if v == nil {
		return nil
	}
	if v.WindowId == nil || v.GetWindowId() < 0 || v.WindowType == nil || validID(v.GetWindowType()) != nil || v.Title == nil || v.Text == nil || v.Interactive == nil ||
		len(v.Options) == 0 || len(v.Options) > maxDialogOptions || len(v.ProtoReflect().GetUnknown()) != 0 {
		return contract("invalid choice dialog census")
	}
	for i, option := range v.Options {
		if option == nil || option.Index == nil || option.GetIndex() != int32(i) || option.Label == nil || validID(option.GetLabel()) != nil || option.Selectable == nil || option.Resolves == nil ||
			(option.GetSelectable() && option.GetDisabledReason() != "") || len(option.Keys) > maxDialogOptionKeys || len(option.ProtoReflect().GetUnknown()) != 0 {
			return contract("invalid choice dialog option")
		}
		for _, key := range option.Keys {
			if validID(key) != nil {
				return contract("invalid choice dialog option key")
			}
		}
	}
	return nil
}
func (client *Client) PreviewAnswerDialog(ctx context.Context, identity *c.Identity, windowID, optionIndex int32, label string) (*op.PreviewReply, Result, error) {
	if ValidateIdentity(identity) != nil || validDialog(windowID, optionIndex, label) != nil {
		return nil, Result{}, contract("invalid dialog preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: dialogOperation(windowID, optionIndex, label)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown dialog preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || !v.GetAccepted() || v.Preparation != nil || v.Projected != nil || buildingContext(v.Context, identity, 0, false) != nil {
		return nil, raw, contract("invalid dialog preview evidence")
	}
	return reply, raw, nil
}
func (writer *DialogControl) AnswerDialog(ctx context.Context, pre *a.WritePrecondition, windowID, optionIndex int32, label string) (*op.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validDialog(windowID, optionIndex, label) != nil {
		return nil, Result{}, contract("invalid dialog execution")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: dialogOperation(windowID, optionIndex, label)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown dialog execute fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetReceipt()
	if v == nil {
		return nil, raw, contract("dialog owner mismatch")
	}
	err = dialogReceipt(v, DialogAttempt{pre.Identity, pre.Attempt, pre.GetExpectedGeneration(), windowID, optionIndex, label})
	return reply, raw, err
}
func validDialogAttempt(w DialogAttempt) error {
	if ValidateIdentity(w.Identity) != nil || buildingAttempt(w.Attempt) != nil || w.Generation == 0 {
		return contract("invalid dialog attempt")
	}
	return validDialog(w.WindowID, w.OptionIndex, w.OptionLabel)
}

// dialogEffect: an applied answer activated the exact option and the dialog
// either closed (resolveTree) or advanced to another node.
func dialogEffect(v *r.EffectEvidence, w DialogAttempt, applied bool) error {
	effect := v.GetDialog()
	if effect == nil || buildingUnknown(v) != nil || effect.GetWindowId() != w.WindowID || effect.GetOptionIndex() != w.OptionIndex || effect.GetOptionLabel() != w.OptionLabel {
		return contract("dialog effect missing or target mismatch")
	}
	if applied && (!effect.GetActivated() || !(effect.GetClosed() || effect.GetAdvanced())) {
		return contract("dialog effect mismatch")
	}
	return nil
}
func dialogReceipt(v *r.Receipt, w DialogAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.AdmittedContext, w.Identity, w.Generation, true) != nil {
		return contract("dialog admission mismatch")
	}
	switch out := v.Outcome.(type) {
	case *r.Receipt_Applied:
		return dialogEffect(out.Applied.GetObserved(), w, true)
	case *r.Receipt_Uncertain:
		if out.Uncertain == nil {
			return contract("dialog uncertainty missing")
		}
		if out.Uncertain.LastObserved != nil {
			return dialogEffect(out.Uncertain.LastObserved, w, false)
		}
		return nil
	default:
		return contract("unsupported dialog receipt")
	}
}
func (client *Client) LookupAnswerDialog(ctx context.Context, w DialogAttempt) (*r.LookupReply, Result, error) {
	if err := validDialogAttempt(w); err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown dialog lookup fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = dialogReceipt(v.Receipt, w)
	case *r.LookupReply_Unknown:
		err = buildingContext(v.Unknown.GetContext(), w.Identity, 0, false)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, w.Attempt) {
			return nil, raw, contract("dialog in-flight mismatch")
		}
		err = buildingContext(v.InFlight.AdmittedContext, w.Identity, w.Generation, true)
	default:
		err = contract("dialog lookup outcome missing")
	}
	return reply, raw, err
}
func (client *Client) ObserveAnswerDialogProgress(ctx context.Context, w DialogAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	if validDialogAttempt(w) != nil {
		return nil, Result{}, contract("dialog observation admission mismatch")
	}
	if admitted != nil {
		if err := dialogReceipt(admitted, w); err != nil {
			return nil, Result{}, err
		}
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown dialog progress fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.Context, w.Identity, 0, false) != nil || (admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick()) {
		return nil, raw, contract("dialog progress scope mismatch")
	}
	switch out := v.Effect.(type) {
	case *r.Progress_Unknown:
		if out.Unknown == nil {
			return nil, raw, contract("missing dialog uncertainty")
		}
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() {
			return nil, raw, contract("incomplete dialog completion")
		}
		err = dialogEffect(out.Completed.GetEvidence(), w, true)
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || out.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return nil, raw, contract("unverified dialog failure")
		}
		err = dialogEffect(out.Unsuccessful.GetEvidence(), w, false)
	default:
		err = contract("unsupported dialog progress")
	}
	return reply, raw, err
}
