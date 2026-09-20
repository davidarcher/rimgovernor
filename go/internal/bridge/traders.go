package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// TraderRead is one map trader caravan pawn a trade could be opened with:
// its entity id, the exact OpenTrade CAS token native computed for it, its
// trader kind and faction, whether native reports it tradeable now
// (CanTradeNow, not dismissed and no longer walking in), whether its
// caravan is still travelling to its trade spot, and the cell it stands on
// (native walks the negotiator to it on open). Orbital ships
// are never listed; direct orbital opening is unsupported.
type TraderRead struct {
	ID          string
	Token       string
	Kind        string
	Faction     string
	CanTrade    bool
	Travelling  bool
	Reason      string
	GoodsStacks uint32
	X, Z        int32
}

// NegotiatorRead is one colonist native reports eligible to negotiate (not
// dead, downed, in a mental state or social-incapable), with its OpenTrade
// CAS token. Native lists them best trade-price-improvement first.
type NegotiatorRead struct {
	ID    string
	Token string
}

// TradersRead is one complete, unfiltered census of the identified map's
// traders and eligible negotiators.
type TradersRead struct {
	Context     *c.ObservationContext
	Traders     []TraderRead
	Negotiators []NegotiatorRead
}

const tradersMaximumRows = 4096

// ListTraders reads the map's trader caravans and eligible negotiators. A
// reply whose completeness reports filtered or unreadable rows is refused: a
// partial trader census is not a usable basis for deciding not to trade.
// tradersRequest is the trader census read, shared with the bundle's
// traders family (#593).
func tradersRequest(identity *c.Identity) *o.TradersRequest {
	return &o.TradersRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}}
}

func (client *Client) ListTraders(ctx context.Context, identity *c.Identity) (TradersRead, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return TradersRead{}, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	request := tradersRequest(identity)
	reply := &o.TradersReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_traders", request, reply)
	if err != nil {
		return TradersRead{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return TradersRead{}, raw, err
	}
	var snapshot *o.TradersSnapshot
	switch v := reply.Outcome.(type) {
	case *o.TradersReply_Failure:
		return TradersRead{}, raw, failure(v.Failure, raw)
	case *o.TradersReply_Unavailable:
		return TradersRead{}, raw, unavailable(v.Unavailable, raw)
	case *o.TradersReply_Observed:
		snapshot = v.Observed
	default:
		return TradersRead{}, raw, contract("traders outcome missing")
	}
	if err = ValidateContext(snapshot.Context); err != nil {
		return TradersRead{}, raw, err
	}
	if !sameIdentity(snapshot.Context.Identity, identity) {
		return TradersRead{}, raw, contract("traders world mismatch")
	}
	counts := snapshot.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Returned == nil || counts.GetReturned() != uint64(len(snapshot.Traders)+len(snapshot.Negotiators)) {
		return TradersRead{}, raw, contract("traders completeness missing")
	}
	if counts.Filtered == nil || counts.GetFiltered() != 0 || counts.Unreadable == nil || counts.GetUnreadable() != 0 {
		return TradersRead{}, raw, contract("traders census omitted rows")
	}
	if len(snapshot.Traders)+len(snapshot.Negotiators) > tradersMaximumRows {
		return TradersRead{}, raw, contract("traders census exceeds bound")
	}
	out := TradersRead{Context: snapshot.Context}
	seen := map[string]bool{}
	for _, row := range snapshot.Traders {
		if row == nil || row.Trader == nil || validID(row.Trader.GetId()) != nil || row.Trader.Snapshot == nil || validID(row.Trader.Snapshot.GetToken()) != nil {
			return TradersRead{}, raw, contract("invalid trader row")
		}
		if !diagnostic(row.Kind) || !diagnostic(row.FactionId) || !diagnostic(row.Reason) || row.CanTrade == nil || row.Travelling == nil {
			return TradersRead{}, raw, contract("invalid trader row text")
		}
		if row.GetOrbital() {
			return TradersRead{}, raw, contract("orbital trader listed")
		}
		cell := row.Trader.Position
		if cell == nil || cell.X == nil || cell.Z == nil || cell.GetX() < 0 || cell.GetZ() < 0 {
			return TradersRead{}, raw, contract("trader position missing")
		}
		if seen[row.Trader.GetId()] {
			return TradersRead{}, raw, contract("duplicate trader")
		}
		seen[row.Trader.GetId()] = true
		out.Traders = append(out.Traders, TraderRead{ID: row.Trader.GetId(), Token: row.Trader.Snapshot.GetToken(), Kind: row.GetKind(), Faction: row.GetFactionId(), CanTrade: row.GetCanTrade(), Travelling: row.GetTravelling(), Reason: row.GetReason(), GoodsStacks: row.GetGoodsStacks(), X: cell.GetX(), Z: cell.GetZ()})
	}
	for _, row := range snapshot.Negotiators {
		if row == nil || validID(row.GetId()) != nil || row.Snapshot == nil || validID(row.Snapshot.GetToken()) != nil {
			return TradersRead{}, raw, contract("invalid negotiator row")
		}
		if seen[row.GetId()] {
			return TradersRead{}, raw, contract("duplicate negotiator")
		}
		seen[row.GetId()] = true
		out.Negotiators = append(out.Negotiators, NegotiatorRead{ID: row.GetId(), Token: row.Snapshot.GetToken()})
	}
	return out, raw, ctx.Err()
}
