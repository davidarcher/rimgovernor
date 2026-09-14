package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ProductionDrill mirrors one native owned bounded drilling facility row from
// ProductionPolicySnapshot.drills (see DrillingGuard/DrillingRecord on the
// native side): a resource target enforced only while drilling, independent
// of the persistent Floors/Stopped rows.
type ProductionDrill struct {
	DefName, Resource   string
	X, Z                int32
	StockTarget         int64
	Recovered           int64
	Missing             bool
}

// ProductionPolicyRead is the map-scoped ProductionPolicyState
// ProductionPolicyGuard enforces (persistent Floors/Stopped rows, transient
// supervised Commitments, owned Drills) plus the CAS snapshot token
// SetProductionPolicy's expected_snapshot_token must match to replace it.
type ProductionPolicyRead struct {
	Context           *c.ObservationContext
	SnapshotToken     string
	Floors            map[policy.Resource]int64
	Commitments       map[policy.Resource]int64
	Stopped           []policy.Resource
	Drills            []ProductionDrill
	CommitmentsActive bool
}

func (client *Client) ReadProductionPolicy(ctx context.Context, identity *c.Identity) (ProductionPolicyRead, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return ProductionPolicyRead{}, Result{}, err
	}
	request := &o.ProductionPolicyRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}}
	reply := &o.ProductionPolicyReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_read_production_policy", request, reply)
	if err != nil {
		return ProductionPolicyRead{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return ProductionPolicyRead{}, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ProductionPolicyReply_Failure:
		return ProductionPolicyRead{}, raw, failure(v.Failure, raw)
	case *o.ProductionPolicyReply_Unavailable:
		return ProductionPolicyRead{}, raw, unavailable(v.Unavailable, raw)
	case *o.ProductionPolicyReply_Observed:
		out, err := readProductionPolicySnapshot(v.Observed, identity)
		return out, raw, err
	default:
		return ProductionPolicyRead{}, raw, contract("missing production policy outcome")
	}
}

func readProductionPolicySnapshot(v *o.ProductionPolicySnapshot, identity *c.Identity) (ProductionPolicyRead, error) {
	if v == nil || v.Snapshot == nil || ValidateContext(v.Snapshot.Context) != nil || !sameIdentity(v.Snapshot.Context.Identity, identity) {
		return ProductionPolicyRead{}, contract("invalid production policy context")
	}
	if validID(v.Snapshot.GetToken()) != nil {
		return ProductionPolicyRead{}, contract("invalid production policy snapshot token")
	}
	if len(v.Floors) > 256 || len(v.Commitments) > 256 || len(v.StoppedDefs) > 256 || len(v.Drills) > 32 {
		return ProductionPolicyRead{}, contract("production policy snapshot exceeds bound")
	}
	out := ProductionPolicyRead{Context: v.Snapshot.Context, SnapshotToken: v.Snapshot.GetToken(),
		Floors: map[policy.Resource]int64{}, Commitments: map[policy.Resource]int64{}, CommitmentsActive: v.GetCommitmentsActive()}
	for _, row := range v.Floors {
		if row == nil || validID(row.GetDefName()) != nil || row.Units == nil || row.GetUnits() < 0 {
			return ProductionPolicyRead{}, contract("invalid production floor")
		}
		name := policy.Resource(row.GetDefName())
		if _, exists := out.Floors[name]; exists {
			return ProductionPolicyRead{}, contract("duplicate production floor")
		}
		out.Floors[name] = row.GetUnits()
	}
	for _, row := range v.Commitments {
		if row == nil || validID(row.GetDefName()) != nil || row.Units == nil || row.GetUnits() < 0 {
			return ProductionPolicyRead{}, contract("invalid production commitment")
		}
		name := policy.Resource(row.GetDefName())
		if _, exists := out.Commitments[name]; exists {
			return ProductionPolicyRead{}, contract("duplicate production commitment")
		}
		out.Commitments[name] = row.GetUnits()
	}
	seenStopped := map[policy.Resource]bool{}
	for _, name := range v.StoppedDefs {
		if validID(name) != nil || seenStopped[policy.Resource(name)] {
			return ProductionPolicyRead{}, contract("invalid or duplicate stopped resource")
		}
		seenStopped[policy.Resource(name)] = true
		out.Stopped = append(out.Stopped, policy.Resource(name))
	}
	seenDrills := map[[2]int32]bool{}
	for _, drill := range v.Drills {
		if drill == nil || validID(drill.GetDefName()) != nil || validID(drill.GetResource()) != nil || drill.Cell == nil ||
			drill.StockTarget == nil || drill.GetStockTarget() < 0 || drill.Recovered == nil || drill.GetRecovered() < 0 {
			return ProductionPolicyRead{}, contract("invalid owned drill")
		}
		key := [2]int32{drill.Cell.GetX(), drill.Cell.GetZ()}
		if seenDrills[key] {
			return ProductionPolicyRead{}, contract("duplicate owned drill position")
		}
		seenDrills[key] = true
		out.Drills = append(out.Drills, ProductionDrill{DefName: drill.GetDefName(), Resource: drill.GetResource(),
			X: drill.Cell.GetX(), Z: drill.Cell.GetZ(), StockTarget: drill.GetStockTarget(), Recovered: drill.GetRecovered(), Missing: drill.GetMissing()})
	}
	return out, nil
}
