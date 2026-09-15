package bridge

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// TravelCaravanAttempt is caravan-progression's direct-write order: route an
// already-formed, already-observed player caravan to a new tile or
// settlement there, send it home, or hold it in place through its native
// path follower. The native contract is NativeCaravanTravel.cs
// (integrations/rimgovernor-native/src/Bridge/Protocol), wired onto
// Operation_TravelCaravan in NativeOperationTools.cs's Execute/Preview
// dispatch. It ports the legacy home/caravan tool's "move"/"visit"/
// "return"/"stop" actions (CaravanTools.Execute) behind the typed boundary:
// TravelingCaravanUtility for route selection and CaravanExitMapUtility for
// re-planning, or the caravan's own path follower for a hold.
//
// CaravanToken is self-computed identically by Go (see travelCaravanToken
// below) and native (NativeCaravanTravel.CaravanToken) from already-observed
// CaravanJourney facts (id/tile/moving only -- unlike GiftCaravanSilver/
// FulfillQuest, TravelCaravan carries no crew precondition in its wire
// message, since route/hold admission never depends on exactly which pawns
// currently ride along), so no dedicated candidate read is needed just to
// admit a caravan that has not moved since it was last observed. A distinct
// token prefix from every other caravan-family token keeps a stale write
// intended for one family from ever being admitted as another.
type TravelCaravanAttempt struct {
	Identity              *c.Identity
	Attempt               *c.AttemptKey
	Generation            uint64
	Caravan, CaravanToken string
	Kind                  o.TravelKind
	DestinationTile       int32 // -1 when Kind carries no destination (ReturnHome/Stop)
}

func travelCaravanToken(id string, tile int32, moving bool) string {
	movingText := "false"
	if moving {
		movingText = "true"
	}
	sum := sha256.Sum256([]byte(fmt.Sprintf("%s|%d|%s", id, tile, movingText)))
	return "caravan-travel-" + hex.EncodeToString(sum[:])
}

// TravelCaravanToken exposes travelCaravanToken to callers that must derive
// the exact freshness token native re-checks (e.g. from a world_progression
// CaravanJourney read), the same way callers already derive quest/gift
// caravan tokens.
func TravelCaravanToken(id string, tile int32, moving bool) string {
	return travelCaravanToken(id, tile, moving)
}

func travelCaravanHasDestination(kind o.TravelKind) bool {
	return kind == o.TravelKind_TRAVEL_KIND_MOVE || kind == o.TravelKind_TRAVEL_KIND_VISIT
}

func travelCaravanValidKind(kind o.TravelKind) bool {
	switch kind {
	case o.TravelKind_TRAVEL_KIND_MOVE, o.TravelKind_TRAVEL_KIND_VISIT, o.TravelKind_TRAVEL_KIND_RETURN_HOME, o.TravelKind_TRAVEL_KIND_STOP:
		return true
	default:
		return false
	}
}

func travelCaravanOperation(caravan, caravanToken string, kind o.TravelKind, destinationTile int32) *o.Operation {
	command := &o.TravelCaravan{Caravan: gearEntity(caravan, caravanToken), Kind: kind.Enum()}
	if travelCaravanHasDestination(kind) {
		command.DestinationTile = proto.Int32(destinationTile)
	}
	return &o.Operation{Command: &o.Operation_TravelCaravan{TravelCaravan: command}}
}

func travelCaravanCommand(caravan, caravanToken string, kind o.TravelKind, destinationTile int32) error {
	if validID(caravan) != nil || validID(caravanToken) != nil || !travelCaravanValidKind(kind) {
		return contract("invalid travel caravan command")
	}
	if travelCaravanHasDestination(kind) {
		if destinationTile < 0 {
			return contract("invalid travel caravan destination")
		}
	} else if destinationTile != -1 {
		return contract("travel caravan return/hold carries no destination")
	}
	return nil
}

// PreviewTravelCaravan checks an exact already-observed caravan/route order;
// a preview accept is not authority.
func (client *Client) PreviewTravelCaravan(ctx context.Context, identity *c.Identity, caravan, caravanToken string, kind o.TravelKind, destinationTile int32) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := travelCaravanCommand(caravan, caravanToken, kind, destinationTile); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: travelCaravanOperation(caravan, caravanToken, kind, destinationTile)}, reply)
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
			return reply, raw, contract("travel caravan preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		prep, ok := value.Preparation.(*o.PreviewEvaluation_Caravan)
		effect := value.Projected.GetCaravan()
		if value.Accepted == nil || !diagnostic(value.Reason) || !ok || prep.Caravan == nil || effect == nil {
			err = contract("travel caravan preview facts missing")
			break
		}
		if travelCaravanHasDestination(kind) && (effect.DestinationTile == nil || effect.GetDestinationTile() != destinationTile) {
			err = contract("travel caravan preview destination mismatch")
			break
		}
		observedAccept := effect.GetPathStarted()
		if kind == o.TravelKind_TRAVEL_KIND_STOP {
			observedAccept = effect.GetStopped()
		}
		if observedAccept != value.GetAccepted() {
			err = contract("travel caravan preview acceptance mismatch")
		}
	default:
		err = contract("travel caravan preview outcome missing")
	}
	return reply, raw, err
}

func travelCaravanAttempt(v TravelCaravanAttempt) (TravelCaravanAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return TravelCaravanAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return TravelCaravanAttempt{}, err
	}
	if v.Generation == 0 {
		return TravelCaravanAttempt{}, contract("travel caravan admission owner or generation mismatch")
	}
	if err := travelCaravanCommand(v.Caravan, v.CaravanToken, v.Kind, v.DestinationTile); err != nil {
		return TravelCaravanAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	return v, nil
}

func travelCaravanEvidence(evidence *r.EffectEvidence, expected TravelCaravanAttempt) (*r.CaravanEffect, error) {
	effect := evidence.GetCaravan()
	if effect == nil {
		return nil, contract("travel caravan effect missing")
	}
	if travelCaravanHasDestination(expected.Kind) && (effect.DestinationTile == nil || effect.GetDestinationTile() != expected.DestinationTile) {
		return nil, contract("travel caravan destination mismatch")
	}
	allowed := &r.CaravanEffect{CaravanId: effect.CaravanId, AssemblyStarted: effect.AssemblyStarted, PathStarted: effect.PathStarted, Stopped: effect.Stopped, DestinationTile: effect.DestinationTile, PawnIds: effect.PawnIds, Snapshot: effect.Snapshot}
	if !proto.Equal(effect, allowed) {
		return nil, contract("travel caravan effect fields missing or unsupported")
	}
	if effect.CaravanId != nil {
		if validID(effect.GetCaravanId()) != nil {
			return nil, contract("travel caravan id invalid")
		}
		if effect.GetCaravanId() != expected.Caravan {
			return nil, contract("travel caravan id mismatch")
		}
	}
	switch expected.Kind {
	case o.TravelKind_TRAVEL_KIND_STOP:
		if !effect.GetStopped() {
			return nil, contract("travel caravan hold effect missing")
		}
	case o.TravelKind_TRAVEL_KIND_MOVE, o.TravelKind_TRAVEL_KIND_VISIT, o.TravelKind_TRAVEL_KIND_RETURN_HOME:
		if !effect.GetPathStarted() {
			return nil, contract("travel caravan route effect missing")
		}
	}
	return effect, nil
}

func travelCaravanReceipt(v *r.Receipt, expected TravelCaravanAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("travel caravan admission mismatch")
	}
	if err := buildingContext(v.AdmittedContext, expected.Identity, expected.Generation, true); err != nil {
		return err
	}
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil {
			return contract("travel caravan applied missing")
		}
		_, err := travelCaravanEvidence(outcome.Applied.GetObserved(), expected)
		return err
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return contract("travel caravan uncertainty missing")
		}
		if outcome.Uncertain.LastObserved != nil {
			_, err := travelCaravanEvidence(outcome.Uncertain.LastObserved, expected)
			return err
		}
		return nil
	default:
		return contract("unsupported travel caravan receipt")
	}
}

type TravelCaravanWriter struct{ client *Client }

func NewTravelCaravanWriter(client *Client) (*TravelCaravanWriter, error) {
	if client == nil {
		return nil, contract("travel caravan client missing")
	}
	return &TravelCaravanWriter{client}, nil
}

// ApplyTravelCaravan dispatches one already-admitted TravelCaravan order.
func (writer *TravelCaravanWriter) ApplyTravelCaravan(ctx context.Context, pre *a.WritePrecondition, caravan, caravanToken string, kind o.TravelKind, destinationTile int32) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 {
		return nil, Result{}, contract("invalid travel caravan execution")
	}
	if err := travelCaravanCommand(caravan, caravanToken, kind, destinationTile); err != nil {
		return nil, Result{}, err
	}
	expected, err := travelCaravanAttempt(TravelCaravanAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Generation: pre.GetExpectedGeneration(), Caravan: caravan, CaravanToken: caravanToken, Kind: kind, DestinationTile: destinationTile})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: travelCaravanOperation(caravan, caravanToken, kind, destinationTile)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = travelCaravanReceipt(v.Receipt, expected)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("travel caravan execute outcome missing")
	}
	return reply, raw, err
}

func (client *Client) LookupTravelCaravan(ctx context.Context, w TravelCaravanAttempt) (*r.LookupReply, Result, error) {
	expected, err := travelCaravanAttempt(w)
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
		err = travelCaravanReceipt(v.Receipt, expected)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, expected.Attempt) {
			err = contract("travel caravan in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, expected.Identity, expected.Generation, true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("travel caravan unknown context missing")
		} else {
			err = buildingContext(v.Unknown.Context, expected.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("travel caravan lookup outcome missing")
	}
	return reply, raw, err
}

func (client *Client) ObserveTravelCaravanProgress(ctx context.Context, w TravelCaravanAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := travelCaravanAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = travelCaravanReceipt(admitted, expected); err != nil {
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
		err = travelCaravanProgress(v.Progress, expected, admitted)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("travel caravan progress outcome missing")
	}
	return reply, raw, err
}

func travelCaravanProgress(v *r.Progress, expected TravelCaravanAttempt, admitted *r.Receipt) error {
	if v == nil || v.CompleteInspection == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("travel caravan progress attempt mismatch")
	}
	if err := buildingContext(v.Context, expected.Identity, 0, false); err != nil {
		return err
	}
	if admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("travel caravan progress predates admission")
	}
	switch outcome := v.Effect.(type) {
	case *r.Progress_Unknown:
		if outcome.Unknown == nil || !diagnostic(outcome.Unknown.Reason) {
			return contract("travel caravan unknown progress missing")
		}
		return nil
	case *r.Progress_Pending:
		if outcome.Pending == nil || !v.GetCompleteInspection() {
			return contract("travel caravan pending missing")
		}
		_, err := travelCaravanEvidence(outcome.Pending.Evidence, expected)
		return err
	case *r.Progress_Completed:
		if outcome.Completed == nil || !v.GetCompleteInspection() {
			return contract("travel caravan completed missing")
		}
		_, err := travelCaravanEvidence(outcome.Completed.Evidence, expected)
		return err
	case *r.Progress_Absent:
		if outcome.Absent == nil || validID(outcome.Absent.GetInspectionToken()) != nil || !v.GetCompleteInspection() {
			return contract("travel caravan absence lacks inspection")
		}
		return nil
	case *r.Progress_Unsuccessful:
		if outcome.Unsuccessful == nil || outcome.Unsuccessful.Reason == nil || outcome.Unsuccessful.GetReason() == 0 || !v.GetCompleteInspection() || !diagnostic(outcome.Unsuccessful.Detail) {
			return contract("travel caravan unsuccessful reason missing")
		}
		_, err := travelCaravanEvidence(outcome.Unsuccessful.Evidence, expected)
		return err
	default:
		return contract("travel caravan progress state missing")
	}
}
