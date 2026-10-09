package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// Unchanged singleton sections. Native leaves a section out of a
// frame while its content matches the last one it published, and lists
// every such section's watermark in every frame. The stream holds the last
// carried copy of each, so every frame the table is built from is whole: an
// omitted section whose watermark seq matches the held copy is that copy,
// current at the frame's tick, and is stamped with the frame's context. A
// section absent at another seq changed in a frame this reader skipped: it
// stays absent (reads wait, as for a section that failed to read) and the
// stream asks for a keyframe.

// elidedSection is one omittable BundleSnapshot section.
type elidedSection struct {
	get func(*o.BundleSnapshot) proto.Message
	set func(*o.BundleSnapshot, proto.Message, *c.ObservationContext)
}

var elidedSections = map[string]elidedSection{
	"emergency": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.Emergency) },
		set: func(v *o.BundleSnapshot, m proto.Message, ctx *c.ObservationContext) {
			v.Emergency = m.(*o.StatusSnapshot)
			v.Emergency.Context = ctx
		},
	},
	"colony_facts": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.ColonyFacts) },
		set: func(v *o.BundleSnapshot, m proto.Message, ctx *c.ObservationContext) {
			v.ColonyFacts = m.(*o.ColonyFactsSnapshot)
			v.ColonyFacts.Context = ctx
		},
	},
	"population": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.Population) },
		set: func(v *o.BundleSnapshot, m proto.Message, ctx *c.ObservationContext) {
			v.Population = m.(*o.PopulationSnapshot)
			v.Population.Context = ctx
		},
	},
	"research": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.Research) },
		set: func(v *o.BundleSnapshot, m proto.Message, ctx *c.ObservationContext) {
			v.Research = m.(*o.ResearchSnapshot)
			v.Research.Context = ctx
		},
	},
	"traders": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.Traders) },
		set: func(v *o.BundleSnapshot, m proto.Message, ctx *c.ObservationContext) {
			v.Traders = m.(*o.TradersSnapshot)
			v.Traders.Context = ctx
		},
	},
	"ideology": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.Ideology) },
		set: func(v *o.BundleSnapshot, m proto.Message, ctx *c.ObservationContext) {
			v.Ideology = m.(*o.IdeologySnapshot)
			v.Ideology.Context = ctx
		},
	},
	"world_progression": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.WorldProgression) },
		set: func(v *o.BundleSnapshot, m proto.Message, ctx *c.ObservationContext) {
			v.WorldProgression = m.(*o.WorldProgressionSnapshot)
			v.WorldProgression.Context = ctx
		},
	},
}

// keyedTable is one held keyed table: pawns, buildings or
// things. Native carries it as a keyframe, every row, or as a delta
// against the table published at the watermark's base_seq: the rows whose
// hash changed and the ids that left. The hold keeps one persistent Table
// per family; a delta yields the next version in O(changed rows), sharing
// every other row with the last, and validates only its changed rows.
// Consumers read versions of the table directly (heldTables); a version is
// immutable, so one kept across frames never changes.
type keyedTable interface {
	seq() uint64
	// load replaces the table with a keyframe's rows and envelope.
	load(m proto.Message, seq uint64, ctx *c.ObservationContext)
	// apply applies a delta.
	apply(m proto.Message, seq uint64, ctx *c.ObservationContext)
	// meta is the section's envelope at ctx, without rows: the frame's
	// marker that the table was carried.
	meta(ctx *c.ObservationContext) proto.Message
	// err is the first invalid row, nil when every row is valid.
	err() error
}

type keyedSection struct {
	get   func(*o.BundleSnapshot) proto.Message
	set   func(*o.BundleSnapshot, proto.Message)
	table func() keyedTable
}

var keyedSections = map[string]keyedSection{
	"pawns": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.Pawns) },
		set: func(v *o.BundleSnapshot, m proto.Message) { v.Pawns, _ = m.(*o.PawnSnapshot) },
		table: func() keyedTable {
			return &pawnTable{rows: newHeldRows(func(r *o.PawnState) string { return r.GetPawn().GetId() }, checkPawnRow)}
		},
	},
	"buildings": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.Buildings) },
		set: func(v *o.BundleSnapshot, m proto.Message) { v.Buildings, _ = m.(*o.BuildingsSnapshot) },
		table: func() keyedTable {
			rows := newHeldRows(func(r *o.BuildingState) string { return r.GetBuilding().GetId() }, checkBuiltRow)
			rows.listCheck = checkBuildingListRow
			return &buildingTable{rows: rows}
		},
	},
	"things": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.Things) },
		set: func(v *o.BundleSnapshot, m proto.Message) { v.Things, _ = m.(*o.ThingsSnapshot) },
		table: func() keyedTable {
			return &thingTable{rows: newHeldRows(func(r *o.Thing) string { return r.GetThing().GetId() }, ValidThing)}
		},
	},
}

// checkPawnRow is one pawn table row's validation (PawnTable's per-row
// part): rows are checked when they arrive, against the frame that
// carried them.
func checkPawnRow(row *o.PawnState, ctx *c.ObservationContext) error {
	return pawnsSnapshotSelected(&o.PawnSnapshot{Context: ctx, Pawns: []*o.PawnState{row}}, ctx.GetIdentity(),
		map[string]bool{row.GetPawn().GetId(): true}, tableDetails)
}

// pawnTable, buildingTable and thingTable hold the rows and the section's
// other fields as the last carrying frame set them (every delta carries
// them).
type pawnTable struct {
	rows         *heldRows[o.PawnState]
	at           uint64
	completeness *o.Completeness
	meditate     *bool
}

func (t *pawnTable) seq() uint64 { return t.at }
func (t *pawnTable) err() error  { return t.rows.err() }
func (t *pawnTable) load(m proto.Message, seq uint64, ctx *c.ObservationContext) {
	s := m.(*o.PawnSnapshot)
	t.at, t.completeness, t.meditate = seq, s.Completeness, s.MeditateAssignmentAvailable
	t.rows.load(s.Pawns, ctx)
}
func (t *pawnTable) apply(m proto.Message, seq uint64, ctx *c.ObservationContext) {
	s := m.(*o.PawnSnapshot)
	t.at, t.completeness, t.meditate = seq, s.Completeness, s.MeditateAssignmentAvailable
	t.rows.apply(s.Pawns, s.Removed, ctx)
}
func (t *pawnTable) meta(ctx *c.ObservationContext) proto.Message { return t.envelope(ctx) }
func (t *pawnTable) envelope(ctx *c.ObservationContext) *o.PawnSnapshot {
	return &o.PawnSnapshot{Context: ctx, Completeness: t.completeness, MeditateAssignmentAvailable: t.meditate}
}

type buildingTable struct {
	rows         *heldRows[o.BuildingState]
	at           uint64
	completeness *o.Completeness
	power        []*o.PowerNetwork
}

func (t *buildingTable) seq() uint64 { return t.at }
func (t *buildingTable) err() error  { return t.rows.err() }
func (t *buildingTable) load(m proto.Message, seq uint64, ctx *c.ObservationContext) {
	s := m.(*o.BuildingsSnapshot)
	t.at, t.completeness, t.power = seq, s.Completeness, s.PowerNetworks
	t.rows.load(s.Buildings, ctx)
}
func (t *buildingTable) apply(m proto.Message, seq uint64, ctx *c.ObservationContext) {
	s := m.(*o.BuildingsSnapshot)
	t.at, t.completeness, t.power = seq, s.Completeness, s.PowerNetworks
	t.rows.apply(s.Buildings, s.Removed, ctx)
}
func (t *buildingTable) meta(ctx *c.ObservationContext) proto.Message { return t.envelope(ctx) }
func (t *buildingTable) envelope(ctx *c.ObservationContext) *o.BuildingsSnapshot {
	return &o.BuildingsSnapshot{Context: ctx, PowerNetworks: t.power, Completeness: t.completeness}
}

type thingTable struct {
	rows *heldRows[o.Thing]
	at   uint64
}

func (t *thingTable) seq() uint64 { return t.at }
func (t *thingTable) err() error  { return t.rows.err() }
func (t *thingTable) load(m proto.Message, seq uint64, ctx *c.ObservationContext) {
	t.at = seq
	t.rows.load(m.(*o.ThingsSnapshot).Things, ctx)
}
func (t *thingTable) apply(m proto.Message, seq uint64, ctx *c.ObservationContext) {
	s := m.(*o.ThingsSnapshot)
	t.at = seq
	t.rows.apply(s.Things, s.Removed, ctx)
}
func (t *thingTable) meta(ctx *c.ObservationContext) proto.Message {
	return &o.ThingsSnapshot{Context: ctx}
}

// heldRows is a keyed row table updated by delta. check, when set,
// validates each row as it arrives; bad holds the rows that failed, by id.
// A row with no id is not addressable and never enters the table.
type heldRows[R any] struct {
	id    func(*R) string
	check func(*R, *c.ObservationContext) error
	// listCheck validates a row as the entity list read returns it; its
	// failures are listBad, apart from check's.
	listCheck func(*R) error
	table     Table[*R]
	bad       map[string]error
	listBad   map[string]error
}

func newHeldRows[R any](id func(*R) string, check func(*R, *c.ObservationContext) error) *heldRows[R] {
	return &heldRows[R]{id: id, check: check, bad: map[string]error{}, listBad: map[string]error{}}
}

func (t *heldRows[R]) checkRow(k string, r *R, ctx *c.ObservationContext) {
	delete(t.bad, k)
	delete(t.listBad, k)
	if t.check != nil {
		if err := t.check(r, ctx); err != nil {
			t.bad[k] = err
		}
	}
	if t.listCheck != nil {
		if err := t.listCheck(r); err != nil {
			t.listBad[k] = err
		}
	}
}

// load replaces the table with rows.
func (t *heldRows[R]) load(rows []*R, ctx *c.ObservationContext) {
	t.table, t.bad, t.listBad = Table[*R]{}, map[string]error{}, map[string]error{}
	for _, r := range rows {
		k := t.id(r)
		if k != "" && t.table.Has(k) {
			t.bad["duplicate "+k] = contract("duplicate row %q", k)
		}
		t.checkRow(k, r, ctx)
		if k != "" {
			t.table = t.table.Set(k, r)
		}
	}
}

// apply drops the removed ids and sets the changed rows.
func (t *heldRows[R]) apply(changed []*R, removed []string, ctx *c.ObservationContext) {
	for _, k := range removed {
		t.table = t.table.Delete(k)
		delete(t.bad, k)
		delete(t.listBad, k)
	}
	for _, r := range changed {
		k := t.id(r)
		t.checkRow(k, r, ctx)
		if k != "" {
			t.table = t.table.Set(k, r)
		}
	}
}

// err is the first invalid row in id order, nil when all are valid.
func (t *heldRows[R]) err() error { return firstError(t.bad) }

func firstError(bad map[string]error) error {
	var first string
	var err error
	for k, e := range bad {
		if err == nil || k < first {
			first, err = k, e
		}
	}
	return err
}

// heldTables are the hold's keyed tables as of one frame: versions the
// reader may keep. A table the frame does not carry is empty; its meta is
// nil. err is the first invalid pawn or thing row.
type heldTables struct {
	pawns     Pawns
	pawnMeta  *o.PawnSnapshot
	pawnErr   error
	buildings Buildings
	buildMeta *o.BuildingsSnapshot
	buildErr  error
	// buildListErr is the first row the building list read would refuse.
	buildListErr error
	things       Things
	thingsMeta   *o.ThingsSnapshot
	thingErr     error
}

// err is the first invalid pawn or thing row.
func (t heldTables) err() error {
	if t.pawnErr != nil {
		return t.pawnErr
	}
	return t.thingErr
}

// heldAt is the hold's keyed tables at ctx: O(1), nothing is copied.
func (h *sectionHold) heldAt(ctx *c.ObservationContext) heldTables {
	var out heldTables
	if t := h.pawnTable(); t != nil {
		out.pawns, out.pawnMeta, out.pawnErr = Pawns{t.rows.table}, t.envelope(ctx), t.err()
	}
	if t := h.buildingTable(); t != nil {
		out.buildings, out.buildMeta, out.buildErr, out.buildListErr = Buildings{t.rows.table}, t.envelope(ctx), t.err(), firstError(t.rows.listBad)
	}
	if t := h.thingTable(); t != nil {
		out.things, out.thingsMeta, out.thingErr = Things{t.rows.table}, &o.ThingsSnapshot{Context: ctx}, t.err()
	}
	return out
}

// wholePawns is the pawn table as a list snapshot, for the readers of a
// whole list (a step snapshot, a recording, the list reads): built per
// call from the held table, or v's own list when no table is held. nil
// when the frame carries none.
func (t *heldTables) wholePawns(v *o.BundleSnapshot) proto.Message {
	if t == nil {
		return nilMessage(v.Pawns)
	}
	return nilMessage(t.pawnList())
}

// wholeBuildings is wholePawns for the building table.
func (t *heldTables) wholeBuildings(v *o.BundleSnapshot) *o.BuildingsSnapshot {
	if t == nil {
		return v.Buildings
	}
	return t.buildingList()
}

// pawnList is the pawn table as a list snapshot in id order, nil when the
// frame carries none.
func (t heldTables) pawnList() *o.PawnSnapshot {
	if t.pawnMeta == nil {
		return nil
	}
	out := proto.Clone(t.pawnMeta).(*o.PawnSnapshot)
	out.Pawns = t.pawns.Sorted()
	return out
}

// buildingList is pawnList for the building table.
func (t heldTables) buildingList() *o.BuildingsSnapshot {
	if t.buildMeta == nil {
		return nil
	}
	out := proto.Clone(t.buildMeta).(*o.BuildingsSnapshot)
	out.Buildings = t.buildings.Sorted()
	return out
}

// nilMessage keeps a typed nil section from becoming a non-nil interface.
func nilMessage[T proto.Message](m T) proto.Message {
	if m.ProtoReflect().IsValid() {
		return m
	}
	return nil
}

type heldSection struct {
	seq     uint64
	section proto.Message
}

// sectionHold is the last carried copy of each omittable section, for one
// world and native generation.
type sectionHold struct {
	world      *c.Identity
	generation uint64
	sections   map[string]heldSection
	tables     map[string]keyedTable
}

// fill completes v from the hold and records what v carries. held counts
// the sections filled in; gap reports a section v omits that the hold
// cannot supply (a keyframe is due).
func (h *sectionHold) fill(v *o.BundleSnapshot) (held int, gap bool) {
	ctx := v.GetContext()
	if h.sections == nil || !sameIdentity(h.world, ctx.GetIdentity()) || h.generation != ctx.GetNativeGeneration() {
		h.world, h.generation, h.sections, h.tables = ctx.GetIdentity(), ctx.GetNativeGeneration(), map[string]heldSection{}, map[string]keyedTable{}
	}
	marked := map[string]bool{}
	for _, w := range v.GetWatermarks() {
		name := w.GetSection()
		if keyed, ok := keyedSections[name]; ok && !marked[name] {
			marked[name] = true
			whole := keyed.get(v) != nil && !w.GetDelta()
			if !h.fillKeyed(v, w, keyed, ctx) {
				gap = true
			} else if !whole {
				held++
			}
			continue
		}
		section, known := elidedSections[name]
		if !known || marked[name] {
			continue
		}
		marked[name] = true
		if m := section.get(v); m != nil {
			h.sections[name] = heldSection{seq: w.GetSeq(), section: m}
			continue
		}
		kept, ok := h.sections[name]
		if !ok || kept.seq != w.GetSeq() {
			gap = true
			continue
		}
		section.set(v, kept.section, ctx)
		held++
	}
	// A section without a watermark failed to read: nothing is held for it.
	for name := range h.sections {
		if !marked[name] {
			delete(h.sections, name)
			delete(h.tables, name)
		}
	}
	return held, gap
}

// fillKeyed completes v's keyed table from the hold and records it; false
// when the table is missing and the hold cannot supply it: a delta whose
// base is not the held seq, or an omission at an unheld seq. Either way the
// hold forgets the table and a keyframe is due.
func (h *sectionHold) fillKeyed(v *o.BundleSnapshot, w *o.SectionWatermark, k keyedSection, ctx *c.ObservationContext) bool {
	name := w.GetSection()
	kept := h.tables[name]
	m := k.get(v)
	switch {
	case m != nil && !w.GetDelta():
		t := k.table()
		t.load(m, w.GetSeq(), ctx)
		h.tables[name] = t
		k.set(v, t.meta(ctx))
		return true
	case m != nil && kept != nil && kept.seq() == w.GetBaseSeq():
		kept.apply(m, w.GetSeq(), ctx)
		k.set(v, kept.meta(ctx))
		return true
	case m == nil && kept != nil && kept.seq() == w.GetSeq():
		k.set(v, kept.meta(ctx))
		return true
	}
	delete(h.tables, name)
	k.set(v, nil)
	return false
}

// pawnTable, buildingTable and thingTable are the held tables, nil until
// a frame carries them.
func (h *sectionHold) pawnTable() *pawnTable {
	t, _ := h.tables["pawns"].(*pawnTable)
	return t
}

func (h *sectionHold) buildingTable() *buildingTable {
	t, _ := h.tables["buildings"].(*buildingTable)
	return t
}

func (h *sectionHold) thingTable() *thingTable {
	t, _ := h.tables["things"].(*thingTable)
	return t
}
