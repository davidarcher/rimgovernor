package bridge

import (
	"context"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

const resourceSourceLimit = 64

// ResourceSourceRow is one native ResourceSource row exactly as
// policy.SelectResourceSources needs it. A "mine" row now carries its exact
// Cell and a CAS snapshot Token (NativeResourceSourcesTool.Project populates
// EntityRef.Snapshot for Mineable rows only, via NativeMineAcquisition),
// since AcquireResource can dispatch against a mined source
// (NativeMineAcquisition.Execute). Harvest/hunt rows still carry neither --
// they remain reachable only through the AcquisitionFacts census path. A
// caller dispatching acquisition against a selected source must still
// re-read and re-validate it immediately before admission, the same
// "read then dispatch in one step" discipline ReadGearBenches documents,
// since native recomputes the token fresh from live state at admission time.
type ResourceSourceRow = policy.ResourceSource

// ReadResourceSources is a fresh, uncached read of one resource definition's
// reachable native mine/harvest sources via the typed
// rimgovernor/observations_list_resource_sources RPC (ListResourceSources),
// before the acquisition-selection loop. It requires a
// single complete page, like ReadHusbandryTarget/ReadPrisonerInteractionTarget
// -- pagination is unsupported by the native read adapter this calls, and any
// oversized native collection is reported Unavailable rather than silently
// truncated. Extraction-development detail is never requested. The returned
// policy.ResourceStorage mirrors the reply's always-populated StorageCapacity
// payload (NativeResourceSourcesTool.Storage): the resource method's
// storage branch (material-storage zoning) reads it whenever
// a selected source is a "mine" source -- see policy.SelectResourceStorageZone.
func (client *Client) ReadResourceSources(ctx context.Context, identity *c.Identity, resource string) ([]ResourceSourceRow, policy.ResourceStorage, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, policy.ResourceStorage{}, Result{}, err
	}
	if validID(resource) != nil {
		return nil, policy.ResourceStorage{}, Result{}, contract("invalid resource source definition")
	}
	request := &o.ResourceSourcesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)},
		Resource: proto.String(resource), IncludeDevelopment: proto.Bool(false)}
	reply := &o.ResourceSourcesReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_resource_sources", request, reply)
	if err != nil {
		return nil, policy.ResourceStorage{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return nil, policy.ResourceStorage{}, raw, err
	}
	var snapshot *o.ResourceSourcesSnapshot
	switch v := reply.Outcome.(type) {
	case *o.ResourceSourcesReply_Observed:
		snapshot = v.Observed
	case *o.ResourceSourcesReply_Unavailable:
		return nil, policy.ResourceStorage{}, raw, unavailable(v.Unavailable, raw)
	case *o.ResourceSourcesReply_Failure:
		return nil, policy.ResourceStorage{}, raw, failure(v.Failure, raw)
	default:
		return nil, policy.ResourceStorage{}, raw, contract("missing resource sources outcome")
	}
	if snapshot == nil || ValidateContext(snapshot.Context) != nil || !sameIdentity(snapshot.Context.Identity, identity) {
		return nil, policy.ResourceStorage{}, raw, contract("invalid resource sources context")
	}
	if snapshot.GetResource() != resource {
		return nil, policy.ResourceStorage{}, raw, contract("resource sources definition mismatch")
	}
	counts := snapshot.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || len(snapshot.Sources) > resourceSourceLimit {
		return nil, policy.ResourceStorage{}, raw, contract("incomplete resource sources census")
	}
	storage, err := decodeResourceStorage(snapshot.Storage, resource)
	if err != nil {
		return nil, policy.ResourceStorage{}, raw, err
	}
	seen := map[string]bool{}
	out := make([]ResourceSourceRow, 0, len(snapshot.Sources))
	for _, row := range snapshot.Sources {
		if row == nil || row.Source == nil || validID(row.Source.GetId()) != nil || seen[row.Source.GetId()] {
			return nil, policy.ResourceStorage{}, raw, contract("invalid resource source identity")
		}
		if row.Method == nil || validID(row.GetMethod()) != nil {
			return nil, policy.ResourceStorage{}, raw, contract("invalid resource source method")
		}
		if row.Yield == nil || row.GetYield() < 0 {
			return nil, policy.ResourceStorage{}, raw, contract("invalid resource source yield")
		}
		units := int64(row.GetYield())
		if float64(units) != row.GetYield() {
			return nil, policy.ResourceStorage{}, raw, contract("fractional resource source yield")
		}
		if row.Distance == nil || row.GetDistance() < 0 {
			return nil, policy.ResourceStorage{}, raw, contract("invalid resource source distance")
		}
		if row.Designated == nil {
			return nil, policy.ResourceStorage{}, raw, contract("resource source designation unavailable")
		}
		method := policy.ResourceSourceMethod(row.GetMethod())
		var cell domain.Cell
		var token string
		if method == policy.ResourceSourceMine {
			if validID(row.GetSafety()) != nil {
				return nil, policy.ResourceStorage{}, raw, contract("mine source safety unavailable")
			}
			position := row.Source.GetPosition()
			snapshotToken := row.Source.GetSnapshot().GetToken()
			if position == nil || position.X == nil || position.Z == nil || position.GetX() < 0 || position.GetZ() < 0 || validID(snapshotToken) != nil {
				return nil, policy.ResourceStorage{}, raw, contract("mine source snapshot unavailable")
			}
			cell, token = domain.Cell{X: position.GetX(), Z: position.GetZ()}, snapshotToken
		}
		seen[row.Source.GetId()] = true
		out = append(out, ResourceSourceRow{
			ThingID:    row.Source.GetId(),
			Yield:      units,
			Distance:   row.GetDistance(),
			Method:     method,
			Designated: row.GetDesignated(),
			Safety:     row.GetSafety(),
			Cell:       cell,
			Token:      token,
			Reachable:  emergencyBool(row.Reachable),
		})
	}
	sort.SliceStable(out, func(i, j int) bool {
		if out[i].Distance != out[j].Distance {
			return out[i].Distance < out[j].Distance
		}
		return out[i].ThingID < out[j].ThingID
	})
	return out, storage, raw, nil
}

// decodeResourceStorage decodes NativeResourceSourcesTool.Storage's always-
// populated StorageCapacity payload. Haulers are counted, not carried in
// full, matching policy.ResourceStorage's own narrowing -- nothing dispatches
// hauling jobs directly from this read. Candidate cells are native's own
// hauler-reachable, roofed, unreserved scan and are trusted as exact
// placement sites, not re-validated against colony geometry here.
func decodeResourceStorage(storage *o.StorageCapacity, resource string) (policy.ResourceStorage, error) {
	if storage == nil || storage.GetResource() != resource {
		return policy.ResourceStorage{}, contract("resource storage identity mismatch")
	}
	if storage.Capacity == nil || storage.GetCapacity() < 0 {
		return policy.ResourceStorage{}, contract("invalid resource storage capacity")
	}
	if storage.Stored == nil || storage.GetStored() < 0 {
		return policy.ResourceStorage{}, contract("invalid resource storage stored amount")
	}
	if storage.StackLimit == nil || storage.GetStackLimit() <= 0 {
		return policy.ResourceStorage{}, contract("invalid resource storage stack limit")
	}
	if len(storage.Haulers) > 4096 || len(storage.Candidates) > 4096 {
		return policy.ResourceStorage{}, contract("resource storage collection exceeds bound")
	}
	for _, hauler := range storage.Haulers {
		if hauler == nil || validID(hauler.GetId()) != nil {
			return policy.ResourceStorage{}, contract("invalid resource storage hauler")
		}
	}
	cells := make([]domain.Cell, 0, len(storage.Candidates))
	for _, cell := range storage.Candidates {
		if cell == nil || cell.X == nil || cell.Z == nil || cell.GetX() < 0 || cell.GetZ() < 0 {
			return policy.ResourceStorage{}, contract("invalid resource storage candidate cell")
		}
		cells = append(cells, domain.Cell{X: cell.GetX(), Z: cell.GetZ()})
	}
	return policy.ResourceStorage{
		Resource:   policy.Resource(resource),
		Capacity:   storage.GetCapacity(),
		Stored:     storage.GetStored(),
		StackLimit: int64(storage.GetStackLimit()),
		Haulers:    int64(len(storage.Haulers)),
		Candidates: cells,
	}, nil
}
