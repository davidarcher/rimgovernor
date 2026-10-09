package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// Things is a frame's things table by id: the canonical rows every
// food stock reference resolves against. A reference the table does not
// hold waits for the next frame: the fact it feeds is unknown until then.
type Things struct{ Table[*o.Thing] }

// NewThings is the table of rows by their thing id.
func NewThings(rows ...*o.Thing) Things {
	var t Table[*o.Thing]
	for _, row := range rows {
		t = t.Set(row.GetThing().GetId(), row)
	}
	return Things{t}
}

// With is the table with row added or replaced under id.
func (t Things) With(id string, row *o.Thing) Things { return Things{t.Set(id, row)} }

// Without is the table without id.
func (t Things) Without(id string) Things { return Things{t.Delete(id)} }

// Row is ref's canonical row, false when the table does not hold it.
func (t Things) Row(ref Reference) (*o.Thing, bool) {
	row, ok := t.Get(ref.GetId())
	return row, ok && row != nil
}

// ThingTable validates v against identity and indexes its rows by id; a
// nil v is an empty table.
func ThingTable(v *o.ThingsSnapshot, identity *c.Identity) (Things, error) {
	if v == nil {
		return Things{}, nil
	}
	if err := ValidateContext(v.Context); err != nil {
		return Things{}, err
	}
	if !sameIdentity(v.Context.Identity, identity) {
		return Things{}, contract("things table world mismatch")
	}
	var out Things
	for _, row := range v.Things {
		if err := ValidThing(row, v.Context); err != nil {
			return Things{}, err
		}
		if out.Has(row.Thing.GetId()) {
			return Things{}, contract("duplicate thing row")
		}
		out.Table = out.Set(row.Thing.GetId(), row)
	}
	return out, nil
}

// ValidThing checks one canonical thing row: its entity, counts and, for
// food, finite facts consistent with its kind.
func ValidThing(row *o.Thing, ctx *c.ObservationContext) error {
	if row == nil {
		return contract("thing row missing")
	}
	if err := pawnsEntity(row.Thing, ctx); err != nil {
		return err
	}
	if row.StackCount != nil && row.GetStackCount() < 0 || row.RotTicks != nil && row.GetRotTicks() < 0 || !combatNumber(row.TemperatureC, false) {
		return contract("invalid thing facts")
	}
	if !optionalRef(row.Room) {
		return contract("invalid thing room")
	}
	if row.GetCorpse() {
		if row.Forbidden == nil || !combatNumber(row.MeatAmount, true) || row.MeatAmount == nil || row.BodySize == nil || !combatNumber(row.BodySize, true) || row.GetBodySize() <= 0 || row.GetTileFootprint() != 1 || row.GetStackCount() != 1 {
			return contract("invalid corpse row")
		}
	} else if row.MeatAmount != nil || row.BodySize != nil || row.TileFootprint != nil {
		return contract("corpse facts on a thing that is no corpse")
	}
	return nil
}

// ThingsSection names the things table in the colony mirror, keyed by
// thing id: what a recording replays food stock references against.
const ThingsSection = "things"

// frameThingsMethod keys a frame's things table in its read table,
// frames-only like roundsFrameMethod.
const frameThingsMethod = "rimgovernor/snapshot_frame_things"

// FrameThings is the things table of the newest frame. There is no GABP
// read behind it: without a stream the table is empty and every food
// stock reference waits.
func (caller *Client) FrameThings(ctx context.Context, identity *c.Identity) (Things, error) {
	if err := ValidateIdentity(identity); err != nil {
		return Things{}, err
	}
	if caller.frames == nil {
		return Things{}, nil
	}
	held, err := caller.frameHeld(ctx, frameThingsMethod, identity)
	return held.things, err
}
