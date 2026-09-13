package bridge

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"sort"
	"strings"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// QuestFulfillAttempt is quest-progression's direct-write order: fulfill one
// already-accepted quest's single native settlement trade-request objective
// using an already-visiting caravan. The native contract is
// NativeQuestFulfillOperations.cs (integrations/rimgovernor-native/src/
// Bridge/Protocol), wired onto Operation_FulfillQuest in
// NativeOperationTools.cs's Execute/Preview dispatch. It ports the legacy
// home/fulfill_quest tool's (QuestFulfillmentTool.cs) native mechanics: an
// actual TradeRequestComp caravan gizmo callback and its confirmation
// dialog, invoked directly (no UI, no camera) rather than opened.
//
// QuestToken reuses NativeQuestOperations.Token (the same CAS AcceptQuest
// already re-checks). CaravanToken is self-computed identically by Go (see
// questFulfillCaravanToken below) and native
// (NativeQuestFulfillOperations.CaravanToken) from already-observed
// CaravanState fields, so no dedicated candidate read is needed just to
// admit a caravan that has not moved since it was last observed. Native
// alone re-derives and re-checks the exact requested resource/count against
// the live TradeRequestComp immediately before dispatch; neither token
// encodes it.
type QuestFulfillAttempt struct {
	Identity              *c.Identity
	Attempt               *c.AttemptKey
	Owner                 *a.Owner
	Generation            uint64
	Quest, QuestToken     string
	Caravan, CaravanToken string
	ExpectedPawnIDs       []string
}

// questFulfillCaravanToken reproduces NativeQuestFulfillOperations.CaravanToken
// exactly (same joined-string SHA256 hex, lowercase booleans, ordinally
// sorted pawn ids) from CaravanJourney facts alone, using a distinct prefix
// from SettlementGift's own caravan token so a stale write intended for one
// family can never be admitted as the other.
func questFulfillCaravanToken(id string, tile int32, moving bool, pawnIDs []string) string {
	sorted := append([]string(nil), pawnIDs...)
	sort.Strings(sorted)
	movingText := "false"
	if moving {
		movingText = "true"
	}
	sum := sha256.Sum256([]byte(fmt.Sprintf("%s|%d|%s|%s", id, tile, movingText, strings.Join(sorted, ","))))
	return "caravan-fulfill-" + hex.EncodeToString(sum[:])
}

func questFulfillOperation(quest, questToken, caravan, caravanToken string, expectedPawnIDs []string) *o.Operation {
	command := &o.FulfillQuest{
		Quest: gearEntity(quest, questToken), Caravan: gearEntity(caravan, caravanToken),
		ExpectedPawnIds: append([]string(nil), expectedPawnIDs...),
	}
	return &o.Operation{Command: &o.Operation_FulfillQuest{FulfillQuest: command}}
}

func questFulfillCommand(quest, questToken, caravan, caravanToken string, expectedPawnIDs []string) error {
	if validID(quest) != nil || validID(questToken) != nil || validID(caravan) != nil || validID(caravanToken) != nil {
		return contract("invalid quest fulfill command")
	}
	if len(expectedPawnIDs) == 0 || len(expectedPawnIDs) > 64 {
		return contract("invalid quest fulfill crew")
	}
	seen := map[string]bool{}
	for _, id := range expectedPawnIDs {
		if validID(id) != nil || seen[id] {
			return contract("invalid or duplicate quest fulfill crew")
		}
		seen[id] = true
	}
	return nil
}

// PreviewQuestFulfill checks an exact already-observed quest/caravan write;
// a preview accept is not authority.
func (client *Client) PreviewQuestFulfill(ctx context.Context, identity *c.Identity, quest, questToken, caravan, caravanToken string, expectedPawnIDs []string) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := questFulfillCommand(quest, questToken, caravan, caravanToken, expectedPawnIDs); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: questFulfillOperation(quest, questToken, caravan, caravanToken, expectedPawnIDs)}, reply)
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
			return reply, raw, contract("quest fulfill preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		effect := value.Projected.GetQuest()
		if value.Accepted == nil || !diagnostic(value.Reason) || value.Preparation != nil || effect == nil {
			err = contract("quest fulfill preview facts missing")
			break
		}
		if effect.GetQuestId() != quest {
			err = contract("quest fulfill preview quest mismatch")
		}
	default:
		err = contract("quest fulfill preview outcome missing")
	}
	return reply, raw, err
}

func questFulfillAttempt(v QuestFulfillAttempt) (QuestFulfillAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return QuestFulfillAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return QuestFulfillAttempt{}, err
	}
	if err := authorityOwner(v.Owner); err != nil {
		return QuestFulfillAttempt{}, err
	}
	if err := buildingUnknown(v.Owner); err != nil {
		return QuestFulfillAttempt{}, err
	}
	if v.Generation == 0 || v.Owner.GetControllerSessionId() != v.Attempt.GetControllerSessionId() {
		return QuestFulfillAttempt{}, contract("quest fulfill admission owner or generation mismatch")
	}
	if err := questFulfillCommand(v.Quest, v.QuestToken, v.Caravan, v.CaravanToken, v.ExpectedPawnIDs); err != nil {
		return QuestFulfillAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	v.Owner = proto.Clone(v.Owner).(*a.Owner)
	v.ExpectedPawnIDs = append([]string(nil), v.ExpectedPawnIDs...)
	return v, nil
}

func questFulfillEvidence(effect *r.QuestEffect, expected QuestFulfillAttempt) (*r.QuestEffect, error) {
	if effect == nil || effect.GetQuestId() != expected.Quest {
		return nil, contract("quest fulfill quest mismatch")
	}
	return effect, nil
}

func questFulfillReceipt(v *r.Receipt, expected QuestFulfillAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, expected.Attempt) || !proto.Equal(v.AuthorizingOwner, expected.Owner) {
		return contract("quest fulfill admission mismatch")
	}
	if err := buildingContext(v.AdmittedContext, expected.Identity, expected.Generation, true); err != nil {
		return err
	}
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil {
			return contract("quest fulfill applied missing")
		}
		_, err := questFulfillEvidence(outcome.Applied.GetObserved().GetQuest(), expected)
		return err
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return contract("quest fulfill uncertainty missing")
		}
		if outcome.Uncertain.LastObserved != nil {
			_, err := questFulfillEvidence(outcome.Uncertain.LastObserved.GetQuest(), expected)
			return err
		}
		return nil
	default:
		return contract("unsupported quest fulfill receipt")
	}
}

type QuestFulfillWriter struct{ client *Client }

func NewQuestFulfillWriter(client *Client) (*QuestFulfillWriter, error) {
	if client == nil {
		return nil, contract("quest fulfill client missing")
	}
	return &QuestFulfillWriter{client}, nil
}

// ApplyQuestFulfill dispatches one already-admitted quest fulfill write.
func (writer *QuestFulfillWriter) ApplyQuestFulfill(ctx context.Context, pre *a.WritePrecondition, owner *a.Owner, quest, questToken, caravan, caravanToken string, expectedPawnIDs []string) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validID(pre.GetLeaseId()) != nil {
		return nil, Result{}, contract("invalid quest fulfill execution")
	}
	if err := questFulfillCommand(quest, questToken, caravan, caravanToken, expectedPawnIDs); err != nil {
		return nil, Result{}, err
	}
	expected, err := questFulfillAttempt(QuestFulfillAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Owner: owner, Generation: pre.GetExpectedGeneration(), Quest: quest, QuestToken: questToken, Caravan: caravan, CaravanToken: caravanToken, ExpectedPawnIDs: expectedPawnIDs})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: questFulfillOperation(quest, questToken, caravan, caravanToken, expectedPawnIDs)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = questFulfillReceipt(v.Receipt, expected)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("quest fulfill execute outcome missing")
	}
	return reply, raw, err
}

func (client *Client) LookupQuestFulfill(ctx context.Context, w QuestFulfillAttempt) (*r.LookupReply, Result, error) {
	expected, err := questFulfillAttempt(w)
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
		err = questFulfillReceipt(v.Receipt, expected)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, expected.Attempt) {
			err = contract("quest fulfill in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, expected.Identity, expected.Generation, true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("quest fulfill unknown context missing")
		} else {
			err = buildingContext(v.Unknown.Context, expected.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("quest fulfill lookup outcome missing")
	}
	return reply, raw, err
}

func (client *Client) ObserveQuestFulfillProgress(ctx context.Context, w QuestFulfillAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := questFulfillAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = questFulfillReceipt(admitted, expected); err != nil {
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
		err = questFulfillProgress(v.Progress, expected, admitted)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("quest fulfill progress outcome missing")
	}
	return reply, raw, err
}

func questFulfillProgress(v *r.Progress, expected QuestFulfillAttempt, admitted *r.Receipt) error {
	if v == nil || v.CompleteInspection == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("quest fulfill progress attempt mismatch")
	}
	if err := buildingContext(v.Context, expected.Identity, 0, false); err != nil {
		return err
	}
	if admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("quest fulfill progress predates admission")
	}
	switch outcome := v.Effect.(type) {
	case *r.Progress_Unknown:
		if outcome.Unknown == nil || !diagnostic(outcome.Unknown.Reason) {
			return contract("quest fulfill unknown progress missing")
		}
		return nil
	case *r.Progress_Completed:
		if outcome.Completed == nil || !v.GetCompleteInspection() {
			return contract("quest fulfill completed missing")
		}
		_, err := questFulfillEvidence(outcome.Completed.Evidence.GetQuest(), expected)
		return err
	case *r.Progress_Unsuccessful:
		if outcome.Unsuccessful == nil || outcome.Unsuccessful.Reason == nil || outcome.Unsuccessful.GetReason() == 0 || !v.GetCompleteInspection() || !diagnostic(outcome.Unsuccessful.Detail) {
			return contract("quest fulfill unsuccessful reason missing")
		}
		_, err := questFulfillEvidence(outcome.Unsuccessful.Evidence.GetQuest(), expected)
		return err
	default:
		return contract("quest fulfill progress state missing")
	}
}
