package bridge

import (
	"context"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

const resourceSourceLimit = 64

// ResourceSourceRow is one native ResourceSource row exactly as
// policy.SelectResourceSources needs it. There is no per-source CAS snapshot
// token yet -- the typed NativeResourceSourcesTool read adapter this reads
// does not populate EntityRef.Snapshot, since nothing dispatches
// AcquireResource against a mined source yet (see docs/BACKLOG.md 05.5). A
// caller that later needs to dispatch acquisition against a selected source
// must re-read and re-validate it immediately before admission, the same
// "read then dispatch in one step" discipline ReadGearBenches documents.
type ResourceSourceRow = policy.ResourceSource

// ReadResourceSources is a fresh, uncached read of one resource definition's
// reachable native mine/harvest sources via the typed
// rimgovernor/observations_list_resource_sources RPC (ListResourceSources),
// mirroring production_policy.py's resource_method reading
// home/resource_sources before its acquisition-selection loop. It requires a
// single complete page, like ReadHusbandryTarget/ReadPrisonerInteractionTarget
// -- pagination is unsupported by the native read adapter this calls, and any
// oversized native collection is reported Unavailable rather than silently
// truncated. Extraction-development detail is never requested.
func (client *Client) ReadResourceSources(ctx context.Context, identity *c.Identity, resource string) ([]ResourceSourceRow, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if validID(resource) != nil {
		return nil, Result{}, contract("invalid resource source definition")
	}
	request := &o.ResourceSourcesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)},
		Resource: proto.String(resource), IncludeDevelopment: proto.Bool(false)}
	reply := &o.ResourceSourcesReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_resource_sources", request, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return nil, raw, err
	}
	var snapshot *o.ResourceSourcesSnapshot
	switch v := reply.Outcome.(type) {
	case *o.ResourceSourcesReply_Observed:
		snapshot = v.Observed
	case *o.ResourceSourcesReply_Unavailable:
		return nil, raw, unavailable(v.Unavailable, raw)
	case *o.ResourceSourcesReply_Failure:
		return nil, raw, failure(v.Failure, raw)
	default:
		return nil, raw, contract("missing resource sources outcome")
	}
	if snapshot == nil || ValidateContext(snapshot.Context) != nil || !sameIdentity(snapshot.Context.Identity, identity) {
		return nil, raw, contract("invalid resource sources context")
	}
	if snapshot.GetResource() != resource {
		return nil, raw, contract("resource sources definition mismatch")
	}
	counts := snapshot.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || len(snapshot.Sources) > resourceSourceLimit {
		return nil, raw, contract("incomplete resource sources census")
	}
	seen := map[string]bool{}
	out := make([]ResourceSourceRow, 0, len(snapshot.Sources))
	for _, row := range snapshot.Sources {
		if row == nil || row.Source == nil || validID(row.Source.GetId()) != nil || seen[row.Source.GetId()] {
			return nil, raw, contract("invalid resource source identity")
		}
		if row.Method == nil || validID(row.GetMethod()) != nil {
			return nil, raw, contract("invalid resource source method")
		}
		if row.Yield == nil || row.GetYield() < 0 {
			return nil, raw, contract("invalid resource source yield")
		}
		units := int64(row.GetYield())
		if float64(units) != row.GetYield() {
			return nil, raw, contract("fractional resource source yield")
		}
		if row.Distance == nil || row.GetDistance() < 0 {
			return nil, raw, contract("invalid resource source distance")
		}
		if row.Designated == nil {
			return nil, raw, contract("resource source designation unavailable")
		}
		method := policy.ResourceSourceMethod(row.GetMethod())
		if method == policy.ResourceSourceMine && validID(row.GetSafety()) != nil {
			return nil, raw, contract("mine source safety unavailable")
		}
		seen[row.Source.GetId()] = true
		out = append(out, ResourceSourceRow{
			ThingID:    row.Source.GetId(),
			Yield:      units,
			Distance:   row.GetDistance(),
			Method:     method,
			Designated: row.GetDesignated(),
			Safety:     row.GetSafety(),
		})
	}
	sort.SliceStable(out, func(i, j int) bool {
		if out[i].Distance != out[j].Distance {
			return out[i].Distance < out[j].Distance
		}
		return out[i].ThingID < out[j].ThingID
	})
	return out, raw, nil
}
