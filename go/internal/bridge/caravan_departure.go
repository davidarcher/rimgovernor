package bridge

import (
	"context"
	"slices"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// CaravanCargoSelection names one already-catalogued cargo group (Python's
// caravan_catalog.cargo_groups) and the count to load from it; FormCaravan
// carries selections by group id, not raw item defs, so callers must resolve
// cargo through the catalog read before dispatch.
type CaravanCargoSelection struct {
	GroupID string
	Count   int32
}

// CaravanDepartureAttempt is the exact already-selected crew/cargo/destination
// order; acceptance is not authority. FormCaravan has no per-pawn precondition
// token (unlike ImproveGear's per-entity EntityPrecondition) — freshness is
// enforced once, via the catalog snapshot token.
type CaravanDepartureAttempt struct {
	Identity        *c.Identity
	Attempt         *c.AttemptKey
	Generation      uint64
	CatalogToken    string
	PawnIDs         []string
	Cargo           []CaravanCargoSelection
	DestinationTile int32
}

func caravanCargo(cargo []CaravanCargoSelection) []*o.CargoSelection {
	rows := make([]*o.CargoSelection, len(cargo))
	for i, item := range cargo {
		rows[i] = &o.CargoSelection{GroupId: proto.String(item.GroupID), Count: proto.Int32(item.Count)}
	}
	return rows
}
func caravanDepartureOperation(catalogToken string, pawnIDs []string, cargo []CaravanCargoSelection, destinationTile int32) *o.Operation {
	return &o.Operation{Command: &o.Operation_FormCaravan{FormCaravan: &o.FormCaravan{ExpectedCatalogToken: proto.String(catalogToken), PawnIds: append([]string(nil), pawnIDs...), Cargo: caravanCargo(cargo), DestinationTile: proto.Int32(destinationTile)}}}
}
func caravanDepartureCommand(catalogToken string, pawnIDs []string, cargo []CaravanCargoSelection, destinationTile int32) error {
	if validID(catalogToken) != nil || len(pawnIDs) == 0 || len(pawnIDs) > 64 || destinationTile < 0 {
		return contract("invalid caravan departure command")
	}
	seenPawns := make(map[string]bool, len(pawnIDs))
	for _, pawn := range pawnIDs {
		if validID(pawn) != nil || seenPawns[pawn] {
			return contract("invalid or duplicate caravan departure pawn")
		}
		seenPawns[pawn] = true
	}
	if len(cargo) > 256 {
		return contract("caravan departure cargo exceeds bound")
	}
	seenCargo := make(map[string]bool, len(cargo))
	for _, item := range cargo {
		if validID(item.GroupID) != nil || item.Count <= 0 || seenCargo[item.GroupID] {
			return contract("invalid or duplicate caravan departure cargo group")
		}
		seenCargo[item.GroupID] = true
	}
	return nil
}

// PreviewCaravanDeparture checks an exact already-selected crew/cargo/destination
// order; acceptance is not authority.
func (client *Client) PreviewCaravanDeparture(ctx context.Context, identity *c.Identity, catalogToken string, pawnIDs []string, cargo []CaravanCargoSelection, destinationTile int32) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := caravanDepartureCommand(catalogToken, pawnIDs, cargo, destinationTile); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: caravanDepartureOperation(catalogToken, pawnIDs, cargo, destinationTile)}, reply)
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
			return reply, raw, contract("caravan departure preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		prep, ok := value.Preparation.(*o.PreviewEvaluation_Caravan)
		if value.Accepted == nil || !diagnostic(value.Reason) || !ok || prep.Caravan == nil {
			err = contract("caravan departure preview facts missing")
			break
		}
		effect := value.Projected.GetCaravan()
		if effect == nil || effect.DestinationTile == nil || effect.GetDestinationTile() != destinationTile || !slices.Equal(effect.PawnIds, pawnIDs) || effect.GetAssemblyStarted() != value.GetAccepted() {
			err = contract("caravan departure preview projection mismatch")
		}
	default:
		err = contract("caravan departure preview outcome missing")
	}
	return reply, raw, err
}

func caravanDepartureAttempt(v CaravanDepartureAttempt) (CaravanDepartureAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return CaravanDepartureAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return CaravanDepartureAttempt{}, err
	}
	if v.Generation == 0 {
		return CaravanDepartureAttempt{}, contract("caravan departure admission owner or generation mismatch")
	}
	if err := caravanDepartureCommand(v.CatalogToken, v.PawnIDs, v.Cargo, v.DestinationTile); err != nil {
		return CaravanDepartureAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	v.PawnIDs = append([]string(nil), v.PawnIDs...)
	v.Cargo = append([]CaravanCargoSelection(nil), v.Cargo...)
	return v, nil
}
func caravanDepartureEvidence(evidence *r.EffectEvidence, expected CaravanDepartureAttempt) (*r.CaravanEffect, error) {
	effect := evidence.GetCaravan()
	if effect == nil || effect.DestinationTile == nil || effect.GetDestinationTile() != expected.DestinationTile {
		return nil, contract("caravan departure destination mismatch")
	}
	allowed := &r.CaravanEffect{CaravanId: effect.CaravanId, AssemblyStarted: effect.AssemblyStarted, PathStarted: effect.PathStarted, Stopped: effect.Stopped, DestinationTile: effect.DestinationTile, PawnIds: effect.PawnIds, Snapshot: effect.Snapshot}
	if !proto.Equal(effect, allowed) {
		return nil, contract("caravan departure effect fields missing or unsupported")
	}
	if effect.CaravanId != nil && validID(effect.GetCaravanId()) != nil {
		return nil, contract("caravan departure id invalid")
	}
	seen := make(map[string]bool, len(effect.PawnIds))
	for _, pawn := range effect.PawnIds {
		if !slices.Contains(expected.PawnIDs, pawn) || seen[pawn] {
			return nil, contract("caravan departure pawn not in expected crew")
		}
		seen[pawn] = true
	}
	return effect, nil
}
func caravanDepartureReceipt(v *r.Receipt, expected CaravanDepartureAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("caravan departure admission mismatch")
	}
	if err := buildingContext(v.AdmittedContext, expected.Identity, expected.Generation, true); err != nil {
		return err
	}
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil {
			return contract("caravan departure applied missing")
		}
		_, err := caravanDepartureEvidence(outcome.Applied.GetObserved(), expected)
		return err
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return contract("caravan departure uncertainty missing")
		}
		if outcome.Uncertain.LastObserved != nil {
			_, err := caravanDepartureEvidence(outcome.Uncertain.LastObserved, expected)
			return err
		}
		return nil
	default:
		return contract("unsupported caravan departure receipt")
	}
}

type CaravanDepartureWriter struct{ client *Client }

func NewCaravanDepartureWriter(client *Client) (*CaravanDepartureWriter, error) {
	if client == nil {
		return nil, contract("caravan departure client missing")
	}
	return &CaravanDepartureWriter{client}, nil
}

// ApplyCaravanDeparture dispatches one already-admitted FormCaravan order.
func (writer *CaravanDepartureWriter) ApplyCaravanDeparture(ctx context.Context, pre *a.WritePrecondition, catalogToken string, pawnIDs []string, cargo []CaravanCargoSelection, destinationTile int32) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 {
		return nil, Result{}, contract("invalid caravan departure execution")
	}
	if err := caravanDepartureCommand(catalogToken, pawnIDs, cargo, destinationTile); err != nil {
		return nil, Result{}, err
	}
	expected, err := caravanDepartureAttempt(CaravanDepartureAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Generation: pre.GetExpectedGeneration(), CatalogToken: catalogToken, PawnIDs: pawnIDs, Cargo: cargo, DestinationTile: destinationTile})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: caravanDepartureOperation(catalogToken, pawnIDs, cargo, destinationTile)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = caravanDepartureReceipt(v.Receipt, expected)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("caravan departure execute outcome missing")
	}
	return reply, raw, err
}
func (client *Client) LookupCaravanDeparture(ctx context.Context, w CaravanDepartureAttempt) (*r.LookupReply, Result, error) {
	expected, err := caravanDepartureAttempt(w)
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
		err = caravanDepartureReceipt(v.Receipt, expected)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, expected.Attempt) {
			err = contract("caravan departure in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, expected.Identity, expected.Generation, true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("caravan departure unknown context missing")
		} else {
			err = buildingContext(v.Unknown.Context, expected.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("caravan departure lookup outcome missing")
	}
	return reply, raw, err
}
func (client *Client) ObserveCaravanDepartureProgress(ctx context.Context, w CaravanDepartureAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := caravanDepartureAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = caravanDepartureReceipt(admitted, expected); err != nil {
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
		err = caravanDepartureProgress(v.Progress, expected, admitted)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("caravan departure progress outcome missing")
	}
	return reply, raw, err
}
func caravanDepartureProgress(v *r.Progress, expected CaravanDepartureAttempt, admitted *r.Receipt) error {
	if v == nil || v.CompleteInspection == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("caravan departure progress attempt mismatch")
	}
	if err := buildingContext(v.Context, expected.Identity, 0, false); err != nil {
		return err
	}
	if admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("caravan departure progress predates admission")
	}
	switch outcome := v.Effect.(type) {
	case *r.Progress_Unknown:
		if outcome.Unknown == nil || !diagnostic(outcome.Unknown.Reason) {
			return contract("caravan departure unknown progress missing")
		}
		return nil
	case *r.Progress_Pending:
		if outcome.Pending == nil || !v.GetCompleteInspection() {
			return contract("caravan departure pending missing")
		}
		_, err := caravanDepartureEvidence(outcome.Pending.Evidence, expected)
		return err
	case *r.Progress_Completed:
		if outcome.Completed == nil || !v.GetCompleteInspection() {
			return contract("caravan departure completed missing")
		}
		_, err := caravanDepartureEvidence(outcome.Completed.Evidence, expected)
		return err
	case *r.Progress_Absent:
		if outcome.Absent == nil || validID(outcome.Absent.GetInspectionToken()) != nil || !v.GetCompleteInspection() {
			return contract("caravan departure absence lacks inspection")
		}
		return nil
	case *r.Progress_Unsuccessful:
		if outcome.Unsuccessful == nil || outcome.Unsuccessful.Reason == nil || outcome.Unsuccessful.GetReason() == 0 || !v.GetCompleteInspection() || !diagnostic(outcome.Unsuccessful.Detail) {
			return contract("caravan departure unsuccessful reason missing")
		}
		_, err := caravanDepartureEvidence(outcome.Unsuccessful.Evidence, expected)
		return err
	default:
		return contract("caravan departure progress state missing")
	}
}
