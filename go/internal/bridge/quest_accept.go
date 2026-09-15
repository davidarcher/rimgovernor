package bridge

import (
	"context"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// QuestAcceptAttempt is Population/quest-progression's direct-write quest
// custody order: accepting one already-observed, still-open quest offer. The
// native contract is NativeQuestOperations.cs (integrations/rimgovernor-native/
// src/Bridge/Protocol), wired onto Operation_AcceptQuest in
// NativeOperationTools.cs's Execute/Preview dispatch. It ports the legacy
// JSON home/accept_quest tool's (QuestTools.Accept) eligibility checks behind
// the typed boundary: an accepted order is a direct settings write (no
// native job), so acceptance is the effect, not a promise of one.
// RewardChoice is -1 when the quest carries no reward-choice part.
type QuestAcceptAttempt struct {
	Identity                *c.Identity
	Attempt                 *c.AttemptKey
	Generation              uint64
	Quest, QuestToken       string
	AccepterPawn            string
	RewardChoice            int32
}

func questAcceptOperation(quest, questToken, accepterPawn string, rewardChoice int32) *o.Operation {
	command := &o.AcceptQuest{Quest: gearEntity(quest, questToken)}
	if accepterPawn != "" {
		command.AccepterPawnId = proto.String(accepterPawn)
	}
	if rewardChoice != -1 {
		command.RewardChoice = proto.Int32(rewardChoice)
	}
	return &o.Operation{Command: &o.Operation_AcceptQuest{AcceptQuest: command}}
}

func questAcceptCommand(quest, questToken, accepterPawn string, rewardChoice int32) error {
	if validID(quest) != nil || validID(questToken) != nil {
		return contract("invalid quest accept command")
	}
	if accepterPawn != "" && validID(accepterPawn) != nil {
		return contract("invalid quest accept accepter")
	}
	if rewardChoice < -1 {
		return contract("invalid quest accept reward choice")
	}
	return nil
}

// PreviewQuestAccept checks an exact already-selected quest/reward write;
// acceptance is not authority.
func (client *Client) PreviewQuestAccept(ctx context.Context, identity *c.Identity, quest, questToken, accepterPawn string, rewardChoice int32) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := questAcceptCommand(quest, questToken, accepterPawn, rewardChoice); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: questAcceptOperation(quest, questToken, accepterPawn, rewardChoice)}, reply)
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
			return reply, raw, contract("quest accept preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		effect := value.Projected.GetQuest()
		if value.Accepted == nil || !diagnostic(value.Reason) || value.Preparation != nil || effect == nil {
			err = contract("quest accept preview facts missing")
			break
		}
		if effect.GetQuestId() != quest {
			err = contract("quest accept preview quest mismatch")
		}
	default:
		err = contract("quest accept preview outcome missing")
	}
	return reply, raw, err
}

func questAcceptAttempt(v QuestAcceptAttempt) (QuestAcceptAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return QuestAcceptAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return QuestAcceptAttempt{}, err
	}
	if v.Generation == 0 {
		return QuestAcceptAttempt{}, contract("quest accept admission owner or generation mismatch")
	}
	if err := questAcceptCommand(v.Quest, v.QuestToken, v.AccepterPawn, v.RewardChoice); err != nil {
		return QuestAcceptAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	return v, nil
}

func questAcceptEvidence(effect *r.QuestEffect, expected QuestAcceptAttempt) (*r.QuestEffect, error) {
	if effect == nil || effect.GetQuestId() != expected.Quest {
		return nil, contract("quest accept quest mismatch")
	}
	return effect, nil
}

func questAcceptReceipt(v *r.Receipt, expected QuestAcceptAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("quest accept admission mismatch")
	}
	if err := buildingContext(v.AdmittedContext, expected.Identity, expected.Generation, true); err != nil {
		return err
	}
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil {
			return contract("quest accept applied missing")
		}
		_, err := questAcceptEvidence(outcome.Applied.GetObserved().GetQuest(), expected)
		return err
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return contract("quest accept uncertainty missing")
		}
		if outcome.Uncertain.LastObserved != nil {
			_, err := questAcceptEvidence(outcome.Uncertain.LastObserved.GetQuest(), expected)
			return err
		}
		return nil
	default:
		return contract("unsupported quest accept receipt")
	}
}

type QuestAcceptWriter struct{ client *Client }

func NewQuestAcceptWriter(client *Client) (*QuestAcceptWriter, error) {
	if client == nil {
		return nil, contract("quest accept client missing")
	}
	return &QuestAcceptWriter{client}, nil
}

// ApplyQuestAccept dispatches one already-admitted quest accept write.
func (writer *QuestAcceptWriter) ApplyQuestAccept(ctx context.Context, pre *a.WritePrecondition, quest, questToken, accepterPawn string, rewardChoice int32) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 {
		return nil, Result{}, contract("invalid quest accept execution")
	}
	if err := questAcceptCommand(quest, questToken, accepterPawn, rewardChoice); err != nil {
		return nil, Result{}, err
	}
	expected, err := questAcceptAttempt(QuestAcceptAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Generation: pre.GetExpectedGeneration(), Quest: quest, QuestToken: questToken, AccepterPawn: accepterPawn, RewardChoice: rewardChoice})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: questAcceptOperation(quest, questToken, accepterPawn, rewardChoice)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = questAcceptReceipt(v.Receipt, expected)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("quest accept execute outcome missing")
	}
	return reply, raw, err
}

func (client *Client) LookupQuestAccept(ctx context.Context, w QuestAcceptAttempt) (*r.LookupReply, Result, error) {
	expected, err := questAcceptAttempt(w)
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
		err = questAcceptReceipt(v.Receipt, expected)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, expected.Attempt) {
			err = contract("quest accept in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, expected.Identity, expected.Generation, true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("quest accept unknown context missing")
		} else {
			err = buildingContext(v.Unknown.Context, expected.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("quest accept lookup outcome missing")
	}
	return reply, raw, err
}

func (client *Client) ObserveQuestAcceptProgress(ctx context.Context, w QuestAcceptAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := questAcceptAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = questAcceptReceipt(admitted, expected); err != nil {
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
		err = questAcceptProgress(v.Progress, expected, admitted)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("quest accept progress outcome missing")
	}
	return reply, raw, err
}

func questAcceptProgress(v *r.Progress, expected QuestAcceptAttempt, admitted *r.Receipt) error {
	if v == nil || v.CompleteInspection == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("quest accept progress attempt mismatch")
	}
	if err := buildingContext(v.Context, expected.Identity, 0, false); err != nil {
		return err
	}
	if admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("quest accept progress predates admission")
	}
	switch outcome := v.Effect.(type) {
	case *r.Progress_Unknown:
		if outcome.Unknown == nil || !diagnostic(outcome.Unknown.Reason) {
			return contract("quest accept unknown progress missing")
		}
		return nil
	case *r.Progress_Completed:
		if outcome.Completed == nil || !v.GetCompleteInspection() {
			return contract("quest accept completed missing")
		}
		_, err := questAcceptEvidence(outcome.Completed.Evidence.GetQuest(), expected)
		return err
	case *r.Progress_Unsuccessful:
		if outcome.Unsuccessful == nil || outcome.Unsuccessful.Reason == nil || outcome.Unsuccessful.GetReason() == 0 || !v.GetCompleteInspection() || !diagnostic(outcome.Unsuccessful.Detail) {
			return contract("quest accept unsuccessful reason missing")
		}
		_, err := questAcceptEvidence(outcome.Unsuccessful.Evidence.GetQuest(), expected)
		return err
	default:
		return contract("quest accept progress state missing")
	}
}
