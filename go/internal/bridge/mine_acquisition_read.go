package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ReadMineAcquisition is the mine-acquisition vertical's own read, backed by
// the same rimgovernor/observations_list_resource_sources RPC
// ReadResourceSources uses, rather than the generic AcquisitionFacts census
// ReadAcquisition reads: a mined resource is structurally excluded from that
// census (see contracts/proto/observations.proto -- it covers tree/food/hunt
// sources only), so the mine vertical's InspectAcquisition step needs this
// dedicated read to re-validate a selected source immediately before
// dispatch, mirroring ReadAcquisition's exact AcquisitionRead shape
// (Context plus one AcquisitionTarget per matching, still-undesignated row)
// so the same generic AcquisitionBoundary-style inspect/dispatch/observe
// machinery (bridge.PreviewAcquisition/Acquire/LookupAcquisition/
// ObserveAcquisition, all already parameterized over AcquisitionTarget/
// AcquisitionAttempt rather than any particular ActionKind) can dispatch
// against it unchanged. Matching is by exact cell plus the row's own CAS
// snapshot token, since a Mineable carries no back-reference to its eventual
// output the way a hunted pawn's Corpse does (see NativeMineAcquisition.cs).
func (client *Client) ReadMineAcquisition(ctx context.Context, identity *c.Identity, resource string, cell domain.Cell) (AcquisitionRead, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return AcquisitionRead{}, Result{}, err
	}
	if validID(resource) != nil || cell.X < 0 || cell.Z < 0 {
		return AcquisitionRead{}, Result{}, contract("invalid mine acquisition request")
	}
	request := &o.ResourceSourcesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)},
		Resource: proto.String(resource), IncludeDevelopment: proto.Bool(false)}
	reply := &o.ResourceSourcesReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_resource_sources", request, reply)
	if err != nil {
		return AcquisitionRead{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return AcquisitionRead{}, raw, err
	}
	var snapshot *o.ResourceSourcesSnapshot
	switch v := reply.Outcome.(type) {
	case *o.ResourceSourcesReply_Observed:
		snapshot = v.Observed
	case *o.ResourceSourcesReply_Unavailable:
		return AcquisitionRead{}, raw, unavailable(v.Unavailable, raw)
	case *o.ResourceSourcesReply_Failure:
		return AcquisitionRead{}, raw, failure(v.Failure, raw)
	default:
		return AcquisitionRead{}, raw, contract("missing resource sources outcome")
	}
	if snapshot == nil || ValidateContext(snapshot.Context) != nil || !sameIdentity(snapshot.Context.Identity, identity) {
		return AcquisitionRead{}, raw, contract("invalid resource sources context")
	}
	if snapshot.GetResource() != resource {
		return AcquisitionRead{}, raw, contract("resource sources definition mismatch")
	}
	counts := snapshot.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || len(snapshot.Sources) > resourceSourceLimit {
		return AcquisitionRead{}, raw, contract("incomplete resource sources census")
	}
	out := AcquisitionRead{Context: proto.Clone(snapshot.Context).(*c.ObservationContext), Targets: []AcquisitionTarget{}}
	seen := map[string]bool{}
	for _, row := range snapshot.Sources {
		if row == nil || row.Source == nil || validID(row.Source.GetId()) != nil || seen[row.Source.GetId()] {
			return AcquisitionRead{}, raw, contract("invalid resource source identity")
		}
		seen[row.Source.GetId()] = true
		if row.Method == nil || policy.ResourceSourceMethod(row.GetMethod()) != policy.ResourceSourceMine || row.GetDesignated() {
			continue
		}
		position := row.Source.GetPosition()
		token := row.Source.GetSnapshot().GetToken()
		if position == nil || position.X == nil || position.Z == nil || position.GetX() < 0 || position.GetZ() < 0 || validID(token) != nil {
			return AcquisitionRead{}, raw, contract("mine source snapshot unavailable")
		}
		if position.GetX() != cell.X || position.GetZ() != cell.Z {
			continue
		}
		target, err := domain.NewAcquisition(row.Source.GetId(), resource, cell)
		if err != nil {
			return AcquisitionRead{}, raw, err
		}
		out.Targets = append(out.Targets, AcquisitionTarget{target, token})
	}
	return out, raw, nil
}
