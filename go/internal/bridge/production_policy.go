package bridge

import (
	"context"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// ProductionDrillTarget is one requested owned bounded drilling facility row
// (mirrors DrillPolicy); StockTarget 0 suspends a retained facility.
type ProductionDrillTarget struct {
	BuildingDef, ResourceDef string
	X, Z                     int32
	StockTarget              int64
}

// ProductionPolicyTarget is the complete replacement floors/commitments/
// stopped/drills state SetProductionPolicy pushes in one call. Per
// contracts/proto/operations.proto's SetProductionPolicy comment, all four
// rows are a full replacement: a present-but-empty row explicitly clears
// (e.g. no drills means suspend every retained facility), never "leave
// unchanged" -- callers wanting to preserve a row must resend it verbatim
// from a fresh ReadProductionPolicy.
type ProductionPolicyTarget struct {
	Floors, Commitments   map[policy.Resource]int64
	Stopped               []policy.Resource
	Drills                []ProductionDrillTarget
	ExpectedSnapshotToken string
}

// ProductionPolicyAttempt is this write's admission identity, mirroring
// GearReplaceAttempt/WorkAttempt: the target replacement state plus the
// owner/generation/attempt this admission was granted under.
type ProductionPolicyAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Owner      *a.Owner
	Generation uint64
	Target     ProductionPolicyTarget
}

func validProductionPolicyTarget(t ProductionPolicyTarget) error {
	if len(t.Floors) > 256 || len(t.Commitments) > 256 || len(t.Stopped) > 256 || len(t.Drills) > 32 {
		return contract("production policy target exceeds bound")
	}
	if validID(t.ExpectedSnapshotToken) != nil {
		return contract("invalid production policy snapshot token")
	}
	for name, count := range t.Floors {
		if validID(string(name)) != nil || count < 0 || count > 10000 {
			return contract("invalid production floor")
		}
	}
	for name, count := range t.Commitments {
		if validID(string(name)) != nil || count < 0 || count > 10000 {
			return contract("invalid production commitment")
		}
	}
	seen := map[policy.Resource]bool{}
	for _, name := range t.Stopped {
		if validID(string(name)) != nil || seen[name] {
			return contract("invalid or duplicate stopped resource")
		}
		seen[name] = true
	}
	seenDrills := map[[2]int32]bool{}
	for _, d := range t.Drills {
		if validID(d.BuildingDef) != nil || validID(d.ResourceDef) != nil || d.StockTarget < 0 || d.StockTarget > 100000 {
			return contract("invalid drilling facility target")
		}
		key := [2]int32{d.X, d.Z}
		if seenDrills[key] {
			return contract("duplicate drilling facility position")
		}
		seenDrills[key] = true
	}
	return nil
}

func productionPolicyOperation(t ProductionPolicyTarget) *o.Operation {
	set := &o.SetProductionPolicy{ExpectedSnapshotToken: proto.String(t.ExpectedSnapshotToken),
		Floors: &o.DefCounts{}, Commitments: &o.DefCounts{}, StoppedDefs: &o.DefinitionList{}, Drills: &o.DrillPolicies{}}
	floorNames := make([]string, 0, len(t.Floors))
	for name := range t.Floors {
		floorNames = append(floorNames, string(name))
	}
	sort.Strings(floorNames)
	for _, name := range floorNames {
		set.Floors.Rows = append(set.Floors.Rows, &o.DefCount{DefName: proto.String(name), Count: proto.Int32(int32(t.Floors[policy.Resource(name)]))})
	}
	commitmentNames := make([]string, 0, len(t.Commitments))
	for name := range t.Commitments {
		commitmentNames = append(commitmentNames, string(name))
	}
	sort.Strings(commitmentNames)
	for _, name := range commitmentNames {
		set.Commitments.Rows = append(set.Commitments.Rows, &o.DefCount{DefName: proto.String(name), Count: proto.Int32(int32(t.Commitments[policy.Resource(name)]))})
	}
	stopped := make([]string, 0, len(t.Stopped))
	for _, name := range t.Stopped {
		stopped = append(stopped, string(name))
	}
	sort.Strings(stopped)
	set.StoppedDefs.Defs = stopped
	drills := append([]ProductionDrillTarget(nil), t.Drills...)
	sort.Slice(drills, func(i, j int) bool {
		if drills[i].X != drills[j].X {
			return drills[i].X < drills[j].X
		}
		return drills[i].Z < drills[j].Z
	})
	for _, d := range drills {
		set.Drills.Rows = append(set.Drills.Rows, &o.DrillPolicy{BuildingDef: proto.String(d.BuildingDef),
			Cell: &c.Cell{X: proto.Int32(d.X), Z: proto.Int32(d.Z)}, ResourceDef: proto.String(d.ResourceDef), StockTarget: proto.Int32(int32(d.StockTarget))})
	}
	return &o.Operation{Command: &o.Operation_SetProductionPolicy{SetProductionPolicy: set}}
}

// PreviewProductionPolicy checks an exact already-selected target replacement
// against the currently expected snapshot token; acceptance is not authority.
func (client *Client) PreviewProductionPolicy(ctx context.Context, identity *c.Identity, target ProductionPolicyTarget) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := validProductionPolicyTarget(target); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: productionPolicyOperation(target)}, reply)
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
			return reply, raw, contract("production policy preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		if value.Accepted == nil || !diagnostic(value.Reason) || value.Preparation != nil || value.Projected != nil {
			err = contract("production policy preview facts missing")
		}
	default:
		err = contract("production policy preview outcome missing")
	}
	return reply, raw, err
}

func productionPolicyAttempt(v ProductionPolicyAttempt) (ProductionPolicyAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return ProductionPolicyAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return ProductionPolicyAttempt{}, err
	}
	if err := authorityOwner(v.Owner); err != nil {
		return ProductionPolicyAttempt{}, err
	}
	if err := buildingUnknown(v.Owner); err != nil {
		return ProductionPolicyAttempt{}, err
	}
	if v.Generation == 0 || v.Owner.GetControllerSessionId() != v.Attempt.GetControllerSessionId() {
		return ProductionPolicyAttempt{}, contract("production policy admission owner or generation mismatch")
	}
	if err := validProductionPolicyTarget(v.Target); err != nil {
		return ProductionPolicyAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	v.Owner = proto.Clone(v.Owner).(*a.Owner)
	return v, nil
}

func productionPolicyEvidence(evidence *r.EffectEvidence, expected ProductionPolicyAttempt) (*r.ProductionPolicyEffect, error) {
	effect := evidence.GetProductionPolicy()
	if effect == nil || effect.Snapshot == nil || validID(effect.Snapshot.GetEntityId()) != nil ||
		effect.Snapshot.GetBeforeToken() != expected.Target.ExpectedSnapshotToken || validID(effect.Snapshot.GetAfterToken()) != nil {
		return nil, contract("production policy effect mismatch")
	}
	if len(effect.InterruptedPawnIds) > 4096 {
		return nil, contract("production policy interrupted pawns exceed bound")
	}
	seen := map[string]bool{}
	for _, id := range effect.InterruptedPawnIds {
		if validID(id) != nil || seen[id] {
			return nil, contract("invalid or duplicate interrupted pawn id")
		}
		seen[id] = true
	}
	return effect, nil
}

func productionPolicyReceipt(v *r.Receipt, expected ProductionPolicyAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, expected.Attempt) || !proto.Equal(v.AuthorizingOwner, expected.Owner) {
		return contract("production policy admission mismatch")
	}
	if err := buildingContext(v.AdmittedContext, expected.Identity, expected.Generation, true); err != nil {
		return err
	}
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil {
			return contract("production policy applied missing")
		}
		_, err := productionPolicyEvidence(outcome.Applied.GetObserved(), expected)
		return err
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return contract("production policy uncertainty missing")
		}
		if outcome.Uncertain.LastObserved != nil {
			_, err := productionPolicyEvidence(outcome.Uncertain.LastObserved, expected)
			return err
		}
		return nil
	default:
		return contract("unsupported production policy receipt")
	}
}

type ProductionPolicyWriter struct{ client *Client }

func NewProductionPolicyWriter(client *Client) (*ProductionPolicyWriter, error) {
	if client == nil {
		return nil, contract("production policy client missing")
	}
	return &ProductionPolicyWriter{client}, nil
}

// ApplyProductionPolicy dispatches one already-admitted whole-policy
// replacement. Idempotent: a retry of the same admitted attempt (or a later
// attempt whose target already matches live state) still requires the
// caller to pass the currently expected snapshot token, since the native
// handler compares it before applying -- see
// NativeProductionPolicyOperations.Prepare on the native side.
func (writer *ProductionPolicyWriter) ApplyProductionPolicy(ctx context.Context, pre *a.WritePrecondition, owner *a.Owner, target ProductionPolicyTarget) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validID(pre.GetLeaseId()) != nil {
		return nil, Result{}, contract("invalid production policy execution")
	}
	if err := validProductionPolicyTarget(target); err != nil {
		return nil, Result{}, err
	}
	expected, err := productionPolicyAttempt(ProductionPolicyAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Owner: owner, Generation: pre.GetExpectedGeneration(), Target: target})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: productionPolicyOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = productionPolicyReceipt(v.Receipt, expected)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("production policy execute outcome missing")
	}
	return reply, raw, err
}
func (client *Client) LookupProductionPolicy(ctx context.Context, w ProductionPolicyAttempt) (*r.LookupReply, Result, error) {
	expected, err := productionPolicyAttempt(w)
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
		err = productionPolicyReceipt(v.Receipt, expected)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, expected.Attempt) {
			err = contract("production policy in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, expected.Identity, expected.Generation, true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("production policy unknown context missing")
		} else {
			err = buildingContext(v.Unknown.Context, expected.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("production policy lookup outcome missing")
	}
	return reply, raw, err
}
func (client *Client) ObserveProductionPolicyProgress(ctx context.Context, w ProductionPolicyAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := productionPolicyAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = productionPolicyReceipt(admitted, expected); err != nil {
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
		err = productionPolicyProgress(v.Progress, expected, admitted)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("production policy progress outcome missing")
	}
	return reply, raw, err
}
func productionPolicyProgress(v *r.Progress, expected ProductionPolicyAttempt, admitted *r.Receipt) error {
	if v == nil || v.CompleteInspection == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("production policy progress attempt mismatch")
	}
	if err := buildingContext(v.Context, expected.Identity, 0, false); err != nil {
		return err
	}
	if admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("production policy progress predates admission")
	}
	switch outcome := v.Effect.(type) {
	case *r.Progress_Unknown:
		if outcome.Unknown == nil || !diagnostic(outcome.Unknown.Reason) {
			return contract("production policy unknown progress missing")
		}
		return nil
	case *r.Progress_Completed:
		if outcome.Completed == nil || !v.GetCompleteInspection() {
			return contract("production policy completed missing")
		}
		_, err := productionPolicyEvidence(outcome.Completed.Evidence, expected)
		return err
	case *r.Progress_Unsuccessful:
		if outcome.Unsuccessful == nil || outcome.Unsuccessful.Reason == nil || outcome.Unsuccessful.GetReason() == 0 || !v.GetCompleteInspection() || !diagnostic(outcome.Unsuccessful.Detail) {
			return contract("production policy unsuccessful reason missing")
		}
		_, err := productionPolicyEvidence(outcome.Unsuccessful.Evidence, expected)
		return err
	default:
		return contract("production policy progress state missing")
	}
}
