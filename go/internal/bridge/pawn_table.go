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
//
// The table is persistent (Table): a version is immutable, so a consumer
// may keep it across frames.
type Pawns struct{ Table[*o.PawnState] }

// NewPawns is the table of rows by their pawn id.
func NewPawns(rows ...*o.PawnState) Pawns {
	var t Table[*o.PawnState]
	for _, row := range rows {
		t = t.Set(row.GetPawn().GetId(), row)
	}
	return Pawns{t}
}

// PawnsFromMap is the table of rows, keyed as the map keys them.
func PawnsFromMap(rows map[string]*o.PawnState) Pawns {
	var t Table[*o.PawnState]
	for id, row := range rows {
		t = t.Set(id, row)
	}
	return Pawns{t}
}

// With is the table with row added or replaced under its pawn id.
func (p Pawns) With(id string, row *o.PawnState) Pawns { return Pawns{p.Set(id, row)} }

// Without is the table without id.
func (p Pawns) Without(id string) Pawns { return Pawns{p.Delete(id)} }

// Row is ref's canonical row, false when the table does not hold it.
func (p Pawns) Row(ref Reference) (*o.PawnState, bool) {
	row, ok := p.Get(ref.GetId())
	return row, ok && row != nil
}

// pawnLookup resolves pawn references against a pawn table: a Pawns map,
// or the hold's live table while its frame is decoded.
type pawnLookup interface {
	Row(ref Reference) (*o.PawnState, bool)
}

// Tables are a frame's keyed row tables (#1343), what every reference in
// its other sections resolves against.
type Tables struct {
	Buildings Buildings
	Pawns     Pawns
	Things    Things
	// Catalog is the load's definition catalog, which resolves a row's def
	// name to what the def says (#1733); nil where none is held, and the
	// facts that need it stay unknown.
	Catalog *DefinitionCatalog
}

// heldCatalog is the catalog read for identity's load, nil when this
// client has not read it yet.
func (caller *Client) heldCatalog(identity *c.Identity) *DefinitionCatalog {
	caller.catalog.mu.Lock()
	defer caller.catalog.mu.Unlock()
	if held := caller.catalog.catalog; held != nil && held.LoadToken == identity.GetLoadToken() {
		return held
	}
	return nil
}

// Entity is the head (def, label, position) of the row ref points at in
// any of the tables, nil when none holds it: the one place a reference
// reaches its def, label and position (#1342).
func (t Tables) Entity(ref Reference) *o.EntityRef {
	if ref == nil {
		return nil
	}
	if head := t.Buildings.Entity(ref); head != nil {
		return head
	}
	if row, ok := t.Pawns.Row(ref); ok {
		return row.GetPawn()
	}
	if row, ok := t.Things.Row(ref); ok {
		return row.GetThing()
	}
	return nil
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
		return Pawns{}, err
	}
	return NewPawns(v.Pawns...), nil
}

// Snapshot is the rows of ids in id order as a list read answers them,
// false when the table misses one.
func (p Pawns) Snapshot(context *c.ObservationContext, ids []string) (*o.PawnSnapshot, bool) {
	return pawnSnapshot(context, ids, p)
}

// pawnSnapshot is the rows of ids in id order, false when rows misses one.
func pawnSnapshot(context *c.ObservationContext, ids []string, rows pawnLookup) (*o.PawnSnapshot, bool) {
	sorted := slices.Clone(ids)
	slices.Sort(sorted)
	out := &o.PawnSnapshot{Context: context, Completeness: &o.Completeness{}}
	for _, id := range sorted {
		row, ok := rows.Row(&c.Ref{Id: &id})
		if !ok {
			return nil, false
		}
		out.Pawns = append(out.Pawns, row)
	}
	return out, true
}

// uniqueRef reports a well-formed reference not yet in seen, and adds it.
func uniqueRef(e *c.Ref, seen map[string]bool) bool {
	if !validRef(e) || seen[e.GetId()] {
		return false
	}
	seen[e.GetId()] = true
	return true
}

// FrameTables is the keyed tables of the newest frame, or each one's list
// read over GABP without a stream.
func (caller *Client) FrameTables(ctx context.Context, identity *c.Identity) (Tables, error) {
	if caller.frames != nil {
		if err := ValidateIdentity(identity); err != nil {
			return Tables{}, err
		}
		held, err := caller.frameHeld(ctx, framePawnsMethod, identity)
		if err != nil {
			return Tables{}, err
		}
		return Tables{Buildings: held.buildings, Pawns: held.pawns, Things: held.things, Catalog: caller.heldCatalog(identity)}, nil
	}
	buildings, err := caller.FrameBuildings(ctx, identity)
	if err != nil {
		return Tables{}, err
	}
	pawns, err := caller.FramePawns(ctx, identity)
	if err != nil {
		return Tables{}, err
	}
	return Tables{Buildings: buildings, Pawns: pawns, Things: Things{}, Catalog: caller.heldCatalog(identity)}, nil
}

// frameHeld is the hold's keyed tables as of the newest frame past this
// client's last write that carries method's table: the frame is read from
// the stream, the tables from the hold, never from the frame's encoding.
func (caller *Client) frameHeld(ctx context.Context, method string, identity *c.Identity) (heldTables, error) {
	var held heldTables
	if _, err := caller.frameReadView(ctx, method, readCacheKey{method: method}, identity, false, nil, func(s *frameStream) { held = s.held }); err != nil {
		return heldTables{}, err
	}
	return held, held.err()
}

// framePawnsMethod keys a frame's pawn table in its read table,
// frames-only like routineFrameMethod.
const framePawnsMethod = "rimgovernor/snapshot_frame_pawns"

// FramePawns is the pawn table of the newest frame, or every spawned pawn
// read over GABP without a stream.
func (caller *Client) FramePawns(ctx context.Context, identity *c.Identity) (Pawns, error) {
	if err := ValidateIdentity(identity); err != nil {
		return Pawns{}, err
	}
	if caller.frames != nil {
		held, err := caller.frameHeld(ctx, framePawnsMethod, identity)
		if err != nil {
			return Pawns{}, err
		}
		return held.pawns, nil
	}
	v, err := caller.framePawnSnapshot(ctx, identity)
	if err != nil {
		return Pawns{}, err
	}
	return PawnTable(v, identity)
}

// framePawnSnapshot is FramePawns' table as the wire carries it.
func (caller *Client) framePawnSnapshot(ctx context.Context, identity *c.Identity) (*o.PawnSnapshot, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, err
	}
	if caller.frames != nil {
		held, err := caller.frameHeld(ctx, framePawnsMethod, identity)
		if err != nil {
			return nil, err
		}
		return held.pawnList(), nil
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
	if reply.GetObserved() == nil {
		return nil, contract("pawn table outcome missing")
	}
	return reply.GetObserved(), nil
}
