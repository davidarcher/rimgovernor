package bridge

import (
	"context"
	"slices"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// Pawns is a frame's pawn table by id (#1343): every spawned pawn on the
// map, the canonical rows every other section's pawn reference resolves
// against. A reference the table does not hold waits for the next frame:
// the fact it feeds is unknown until then.
type Pawns map[string]*o.PawnState

// Row is ref's canonical row, false when the table does not hold it.
func (p Pawns) Row(ref *o.EntityRef) (*o.PawnState, bool) {
	row, ok := p[ref.GetId()]
	return row, ok && row != nil
}

// Tables are a frame's keyed row tables (#1343), what every reference in
// its other sections resolves against.
type Tables struct {
	Buildings Buildings
	Pawns     Pawns
}

// tableDetails is every detail family a pawn table row may carry: the
// table varies detail by pawn kind, so a row is checked against the
// union, never against one kind's selection.
var tableDetails = pawnDetails{Combat: true, Work: true, Care: true, Schedule: true, Social: true}

// PawnTable validates v against identity and indexes its rows by id; a nil
// v is an empty table.
func PawnTable(v *o.PawnSnapshot, identity *c.Identity) (Pawns, error) {
	if v == nil {
		return Pawns{}, nil
	}
	requested := make(map[string]bool, len(v.Pawns))
	for _, row := range v.Pawns {
		requested[row.GetPawn().GetId()] = true
	}
	if err := pawnsSnapshotSelected(v, identity, requested, tableDetails); err != nil {
		return nil, err
	}
	out := make(Pawns, len(v.Pawns))
	for _, row := range v.Pawns {
		out[row.Pawn.GetId()] = row
	}
	return out, nil
}

// Snapshot is the rows of ids in id order as a list read answers them,
// false when the table misses one.
func (p Pawns) Snapshot(context *c.ObservationContext, ids []string) (*o.PawnSnapshot, bool) {
	sorted := slices.Clone(ids)
	slices.Sort(sorted)
	out := &o.PawnSnapshot{Context: context, Completeness: &o.Completeness{}}
	for _, id := range sorted {
		row, ok := p[id]
		if !ok {
			return nil, false
		}
		out.Pawns = append(out.Pawns, row)
	}
	return out, true
}

// pawnRef reports a well-formed pawn reference not yet in seen: an id and,
// where the section carries one, its own snapshot token, nothing else.
func pawnRef(e *o.EntityRef, seen map[string]bool) bool {
	if e == nil || validID(e.GetId()) != nil || seen[e.GetId()] || !proto.Equal(e, &o.EntityRef{Id: e.Id, Snapshot: e.Snapshot}) {
		return false
	}
	seen[e.GetId()] = true
	return true
}

// FrameTables is the keyed tables of the newest frame, or each one's list
// read over GABP without a stream.
func (caller *Client) FrameTables(ctx context.Context, identity *c.Identity) (Tables, error) {
	buildings, err := caller.FrameBuildings(ctx, identity)
	if err != nil {
		return Tables{}, err
	}
	pawns, err := caller.FramePawns(ctx, identity)
	if err != nil {
		return Tables{}, err
	}
	return Tables{Buildings: buildings, Pawns: pawns}, nil
}

// framePawnsMethod keys a frame's pawn table in its read table,
// frames-only like routineFrameMethod.
const framePawnsMethod = "rimgovernor/snapshot_frame_pawns"

// FramePawns is the pawn table of the newest frame, or every spawned pawn
// read over GABP without a stream.
func (caller *Client) FramePawns(ctx context.Context, identity *c.Identity) (Pawns, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, err
	}
	if caller.frames != nil {
		reply := &o.PawnSnapshot{}
		served, err := caller.frameReadKey(ctx, framePawnsMethod, readCacheKey{method: framePawnsMethod}, identity, false, reply)
		if err != nil {
			return nil, err
		}
		if served {
			return PawnTable(reply, identity)
		}
	}
	request := &o.ListPawnsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Details: &o.PawnDetails{Tend: proto.Bool(false)}}
	reply := &o.ListPawnsReply{}
	raw, err := caller.protoRead(ctx, "rimgovernor/observations_list_pawns", request, reply)
	if err != nil {
		return nil, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ListPawnsReply_Failure:
		return nil, failure(v.Failure, raw)
	case *o.ListPawnsReply_Unavailable:
		return nil, unavailable(v.Unavailable, raw)
	}
	return PawnTable(reply.GetObserved(), identity)
}
