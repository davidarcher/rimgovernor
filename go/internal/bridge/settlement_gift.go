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

// SettlementGiftAttempt is settlement-progression's direct-write order: gift
// an exact silver amount from an already-visiting caravan to the exact
// faction of the settlement it currently sits at. The native contract is
// NativeSettlementGiftOperations.cs (integrations/rimgovernor-native/src/
// Bridge/Protocol), wired onto Operation_GiftCaravanSilver in
// NativeOperationTools.cs's Execute/Preview dispatch. It ports the legacy
// JSON home/caravan_gift tool's (CaravanGiftTools.Run) native mechanics: an
// ordinary gift-mode TradeSession, never a direct faction-relation write.
//
// CaravanToken is self-computed identically by Go (see caravanToken below)
// and native (NativeSettlementGiftOperations.CaravanToken) from already-
// observed CaravanState fields, so no dedicated candidate read is needed
// just to admit a caravan that has not moved since it was last observed.
// FactionToken is opaque: native computes it (NativeWorldObservation.
// FactionToken) and Go only forwards the value ReadWorld most recently
// supplied; it is the actual freshness gate on goodwill and hostility.
type SettlementGiftAttempt struct {
	Identity              *c.Identity
	Attempt               *c.AttemptKey
	Generation            uint64
	Caravan, CaravanToken string
	Faction, FactionToken string
	ExpectedPawnIDs       []string
	Silver                int32
}

// caravanToken reproduces NativeSettlementGiftOperations.CaravanToken exactly
// (same joined-string SHA256 hex, lowercase booleans, ordinally sorted pawn
// ids) from CaravanJourney facts alone.
func caravanToken(id string, tile int32, moving bool, pawnIDs []string) string {
	sorted := append([]string(nil), pawnIDs...)
	sort.Strings(sorted)
	movingText := "false"
	if moving {
		movingText = "true"
	}
	sum := sha256.Sum256([]byte(fmt.Sprintf("%s|%d|%s|%s", id, tile, movingText, strings.Join(sorted, ","))))
	return "caravan-gift-" + hex.EncodeToString(sum[:])
}

func settlementGiftOperation(caravan, caravanToken, faction, factionToken string, expectedPawnIDs []string, silver int32) *o.Operation {
	command := &o.GiftCaravanSilver{
		Caravan: gearEntity(caravan, caravanToken), Faction: gearEntity(faction, factionToken),
		ExpectedPawnIds: append([]string(nil), expectedPawnIDs...), Silver: proto.Int32(silver),
	}
	return &o.Operation{Command: &o.Operation_GiftCaravanSilver{GiftCaravanSilver: command}}
}

func settlementGiftCommand(caravan, caravanToken, faction, factionToken string, expectedPawnIDs []string, silver int32) error {
	if validID(caravan) != nil || validID(caravanToken) != nil || validID(faction) != nil || validID(factionToken) != nil || caravan == faction {
		return contract("invalid settlement gift command")
	}
	if len(expectedPawnIDs) == 0 || len(expectedPawnIDs) > 64 {
		return contract("invalid settlement gift crew")
	}
	seen := map[string]bool{}
	for _, id := range expectedPawnIDs {
		if validID(id) != nil || seen[id] {
			return contract("invalid or duplicate settlement gift crew")
		}
		seen[id] = true
	}
	if silver <= 0 {
		return contract("invalid settlement gift silver")
	}
	return nil
}

// PreviewSettlementGift checks an exact already-observed caravan/settlement/
// silver write; a preview accept is not authority.
func (client *Client) PreviewSettlementGift(ctx context.Context, identity *c.Identity, caravan, caravanToken, faction, factionToken string, expectedPawnIDs []string, silver int32) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := settlementGiftCommand(caravan, caravanToken, faction, factionToken, expectedPawnIDs, silver); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: settlementGiftOperation(caravan, caravanToken, faction, factionToken, expectedPawnIDs, silver)}, reply)
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
			return reply, raw, contract("settlement gift preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		effect := value.Projected.GetTrade()
		if value.Accepted == nil || !diagnostic(value.Reason) || value.Preparation != nil || effect == nil {
			err = contract("settlement gift preview facts missing")
			break
		}
		if effect.GetFactionId() != faction {
			err = contract("settlement gift preview faction mismatch")
		}
	default:
		err = contract("settlement gift preview outcome missing")
	}
	return reply, raw, err
}

func settlementGiftAttempt(v SettlementGiftAttempt) (SettlementGiftAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return SettlementGiftAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return SettlementGiftAttempt{}, err
	}
	if v.Generation == 0 {
		return SettlementGiftAttempt{}, contract("settlement gift admission owner or generation mismatch")
	}
	if err := settlementGiftCommand(v.Caravan, v.CaravanToken, v.Faction, v.FactionToken, v.ExpectedPawnIDs, v.Silver); err != nil {
		return SettlementGiftAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	v.ExpectedPawnIDs = append([]string(nil), v.ExpectedPawnIDs...)
	return v, nil
}

func settlementGiftEvidence(effect *r.TradeEffect, expected SettlementGiftAttempt) (*r.TradeEffect, error) {
	if effect == nil || effect.GetFactionId() != expected.Faction {
		return nil, contract("settlement gift faction mismatch")
	}
	return effect, nil
}

func settlementGiftReceipt(v *r.Receipt, expected SettlementGiftAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("settlement gift admission mismatch")
	}
	if err := buildingContext(v.AdmittedContext, expected.Identity, expected.Generation, true); err != nil {
		return err
	}
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil {
			return contract("settlement gift applied missing")
		}
		_, err := settlementGiftEvidence(outcome.Applied.GetObserved().GetTrade(), expected)
		return err
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return contract("settlement gift uncertainty missing")
		}
		if outcome.Uncertain.LastObserved != nil {
			_, err := settlementGiftEvidence(outcome.Uncertain.LastObserved.GetTrade(), expected)
			return err
		}
		return nil
	default:
		return contract("unsupported settlement gift receipt")
	}
}

type SettlementGiftWriter struct{ client *Client }

func NewSettlementGiftWriter(client *Client) (*SettlementGiftWriter, error) {
	if client == nil {
		return nil, contract("settlement gift client missing")
	}
	return &SettlementGiftWriter{client}, nil
}

// ApplySettlementGift dispatches one already-admitted settlement gift write.
func (writer *SettlementGiftWriter) ApplySettlementGift(ctx context.Context, pre *a.WritePrecondition, caravan, caravanToken, faction, factionToken string, expectedPawnIDs []string, silver int32) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 {
		return nil, Result{}, contract("invalid settlement gift execution")
	}
	if err := settlementGiftCommand(caravan, caravanToken, faction, factionToken, expectedPawnIDs, silver); err != nil {
		return nil, Result{}, err
	}
	expected, err := settlementGiftAttempt(SettlementGiftAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Generation: pre.GetExpectedGeneration(), Caravan: caravan, CaravanToken: caravanToken, Faction: faction, FactionToken: factionToken, ExpectedPawnIDs: expectedPawnIDs, Silver: silver})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: settlementGiftOperation(caravan, caravanToken, faction, factionToken, expectedPawnIDs, silver)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = settlementGiftReceipt(v.Receipt, expected)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("settlement gift execute outcome missing")
	}
	return reply, raw, err
}

func (client *Client) LookupSettlementGift(ctx context.Context, w SettlementGiftAttempt) (*r.LookupReply, Result, error) {
	expected, err := settlementGiftAttempt(w)
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
		err = settlementGiftReceipt(v.Receipt, expected)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, expected.Attempt) {
			err = contract("settlement gift in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, expected.Identity, expected.Generation, true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("settlement gift unknown context missing")
		} else {
			err = buildingContext(v.Unknown.Context, expected.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("settlement gift lookup outcome missing")
	}
	return reply, raw, err
}

func (client *Client) ObserveSettlementGiftProgress(ctx context.Context, w SettlementGiftAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := settlementGiftAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = settlementGiftReceipt(admitted, expected); err != nil {
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
		err = settlementGiftProgress(v.Progress, expected, admitted)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("settlement gift progress outcome missing")
	}
	return reply, raw, err
}

func settlementGiftProgress(v *r.Progress, expected SettlementGiftAttempt, admitted *r.Receipt) error {
	if v == nil || v.CompleteInspection == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("settlement gift progress attempt mismatch")
	}
	if err := buildingContext(v.Context, expected.Identity, 0, false); err != nil {
		return err
	}
	if admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("settlement gift progress predates admission")
	}
	switch outcome := v.Effect.(type) {
	case *r.Progress_Unknown:
		if outcome.Unknown == nil || !diagnostic(outcome.Unknown.Reason) {
			return contract("settlement gift unknown progress missing")
		}
		return nil
	case *r.Progress_Completed:
		if outcome.Completed == nil || !v.GetCompleteInspection() {
			return contract("settlement gift completed missing")
		}
		_, err := settlementGiftEvidence(outcome.Completed.Evidence.GetTrade(), expected)
		return err
	case *r.Progress_Unsuccessful:
		if outcome.Unsuccessful == nil || outcome.Unsuccessful.Reason == nil || outcome.Unsuccessful.GetReason() == 0 || !v.GetCompleteInspection() || !diagnostic(outcome.Unsuccessful.Detail) {
			return contract("settlement gift unsuccessful reason missing")
		}
		_, err := settlementGiftEvidence(outcome.Unsuccessful.Evidence.GetTrade(), expected)
		return err
	default:
		return contract("settlement gift progress state missing")
	}
}
