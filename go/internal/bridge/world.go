package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// SettlementFact is the validated subset of one WorldSnapshot.settlements row
// (NativeWorldObservation.cs's SettlementRow) that the settlement-gift
// boundary needs: identity, current faction relation/goodwill and both
// self-computed CAS tokens. It does not surface WorldTile terrain facts;
// no current consumer needs them.
type SettlementFact struct {
	ID, Label                 string
	Tile                      int32
	FactionID, FactionDefName string
	Relation                  string
	Goodwill                  int32
	GoodwillKnown             bool
	Player                    bool
	SnapshotToken             string
	FactionSnapshotToken      string
}

// WorldRead is the validated subset of one rimgovernor/observations_read_world
// census. As of this writing native implements this handler
// (NativeWorldObservation.cs); this wrapper is its first Go consumer.
type WorldRead struct {
	Context     *c.ObservationContext
	Settlements []SettlementFact
}

// ReadWorld reads settlements within settlementRadius tiles (0: exact tile)
// of tile. It requires the same single complete page every observation
// reader enforces.
func (client *Client) ReadWorld(ctx context.Context, identity *c.Identity, tile int32, settlementRadius float64) (WorldRead, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return WorldRead{}, Result{}, err
	}
	if tile < 0 || settlementRadius < 0 {
		return WorldRead{}, Result{}, contract("invalid world read request")
	}
	identity = proto.Clone(identity).(*c.Identity)
	request := &o.WorldRequest{
		Scope: &o.ReadScope{ExpectedIdentity: identity}, Tile: proto.Int32(tile), SettlementRadius: proto.Float64(settlementRadius),
		Page: &c.PageRequest{Limit: proto.Uint32(256)},
	}
	reply := &o.WorldReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_read_world", request, reply)
	if err != nil {
		return WorldRead{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return WorldRead{}, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.WorldReply_Failure:
		return WorldRead{}, raw, failure(v.Failure, raw)
	case *o.WorldReply_Unavailable:
		return WorldRead{}, raw, unavailable(v.Unavailable, raw)
	case *o.WorldReply_Observed:
		out, err := worldSelected(v.Observed, identity)
		return out, raw, err
	default:
		return WorldRead{}, raw, contract("world outcome missing")
	}
}

func worldSelected(v *o.WorldSnapshot, identity *c.Identity) (WorldRead, error) {
	if v == nil {
		return WorldRead{}, contract("world snapshot missing")
	}
	if err := ValidateContext(v.Context); err != nil {
		return WorldRead{}, err
	}
	if !sameIdentity(v.Context.Identity, identity) {
		return WorldRead{}, contract("world identity mismatch")
	}
	counts := v.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" {
		return WorldRead{}, contract("incomplete world page")
	}
	if len(v.Settlements) > 256 {
		return WorldRead{}, contract("world settlements exceed bound")
	}
	seen := map[string]bool{}
	rows := make([]SettlementFact, len(v.Settlements))
	for i, row := range v.Settlements {
		if row == nil || validID(row.GetId()) != nil || seen[row.GetId()] {
			return WorldRead{}, contract("invalid or duplicate world settlement")
		}
		seen[row.GetId()] = true
		if row.Snapshot == nil || row.Snapshot.GetEntityId() != row.GetId() || validID(row.Snapshot.GetToken()) != nil {
			return WorldRead{}, contract("world settlement CAS token unavailable")
		}
		fact := SettlementFact{
			ID: row.GetId(), Label: row.GetLabel(), Tile: row.GetTile(), Player: row.GetPlayer(), SnapshotToken: row.Snapshot.GetToken(),
		}
		if row.FactionId != nil {
			fact.FactionID, fact.FactionDefName, fact.Relation = row.GetFactionId(), row.GetFactionDefName(), row.GetRelation()
			if row.FactionSnapshot == nil || row.FactionSnapshot.GetEntityId() != row.GetFactionId() || validID(row.FactionSnapshot.GetToken()) != nil {
				return WorldRead{}, contract("world settlement faction CAS token unavailable")
			}
			fact.FactionSnapshotToken = row.FactionSnapshot.GetToken()
			if row.Goodwill != nil {
				fact.Goodwill, fact.GoodwillKnown = row.GetGoodwill(), true
			}
		}
		rows[i] = fact
	}
	return WorldRead{Context: v.Context, Settlements: rows}, nil
}
