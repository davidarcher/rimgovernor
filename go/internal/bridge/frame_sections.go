package bridge

import (
	"slices"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// Unchanged singleton sections (#1347). Native leaves a section out of a
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
	"world_progression": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.WorldProgression) },
		set: func(v *o.BundleSnapshot, m proto.Message, ctx *c.ObservationContext) {
			v.WorldProgression = m.(*o.WorldProgressionSnapshot)
			v.WorldProgression.Context = ctx
		},
	},
}

// keyedTable is one held keyed table (#1348, #1578): pawns, buildings or
// things. Native carries it as a keyframe, every row, or as a delta
// against the table published at the watermark's base_seq: the rows whose
// hash changed and the ids that left. The hold keeps one persistent table
// per family and a delta updates it in place, so a delta frame costs
// O(changed rows); only a keyframe copies every row. A frame's section is
// a view of the table, sharing its row slice and valid until the next
// apply: frameTable encodes the replies from it within the same call, so
// nothing retains it across frames.
type keyedTable interface {
	seq() uint64
	// load replaces the table with a keyframe's rows and envelope.
	load(m proto.Message, seq uint64)
	// apply updates the table in place with a delta.
	apply(m proto.Message, seq uint64)
	// view is the table at ctx as the family's snapshot message.
	view(ctx *c.ObservationContext) proto.Message
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
			return &pawnTable{rows: newRowTable(func(r *o.PawnState) string { return r.GetPawn().GetId() })}
		},
	},
	"buildings": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.Buildings) },
		set: func(v *o.BundleSnapshot, m proto.Message) { v.Buildings, _ = m.(*o.BuildingsSnapshot) },
		table: func() keyedTable {
			return &buildingTable{rows: newRowTable(func(r *o.BuildingState) string { return r.GetBuilding().GetId() })}
		},
	},
	"things": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.Things) },
		set: func(v *o.BundleSnapshot, m proto.Message) { v.Things, _ = m.(*o.ThingsSnapshot) },
		table: func() keyedTable {
			return &thingTable{rows: newRowTable(func(r *o.Thing) string { return r.GetThing().GetId() })}
		},
	},
}

// pawnTable, buildingTable and thingTable hold the rows by id and the
// section's other fields as the last carrying frame set them (every delta
// carries them).
type pawnTable struct {
	rows         *rowTable[o.PawnState]
	at           uint64
	completeness *o.Completeness
	meditate     *bool
}

func (t *pawnTable) seq() uint64 { return t.at }
func (t *pawnTable) load(m proto.Message, seq uint64) {
	s := m.(*o.PawnSnapshot)
	t.at, t.completeness, t.meditate = seq, s.Completeness, s.MeditateAssignmentAvailable
	t.rows.load(s.Pawns)
}
func (t *pawnTable) apply(m proto.Message, seq uint64) {
	s := m.(*o.PawnSnapshot)
	t.at, t.completeness, t.meditate = seq, s.Completeness, s.MeditateAssignmentAvailable
	t.rows.apply(s.Pawns, s.Removed)
}
func (t *pawnTable) view(ctx *c.ObservationContext) proto.Message {
	return &o.PawnSnapshot{Context: ctx, Completeness: t.completeness, MeditateAssignmentAvailable: t.meditate, Pawns: t.rows.list()}
}

type buildingTable struct {
	rows         *rowTable[o.BuildingState]
	at           uint64
	completeness *o.Completeness
	power        []*o.PowerNetwork
}

func (t *buildingTable) seq() uint64 { return t.at }
func (t *buildingTable) load(m proto.Message, seq uint64) {
	s := m.(*o.BuildingsSnapshot)
	t.at, t.completeness, t.power = seq, s.Completeness, s.PowerNetworks
	t.rows.load(s.Buildings)
}
func (t *buildingTable) apply(m proto.Message, seq uint64) {
	s := m.(*o.BuildingsSnapshot)
	t.at, t.completeness, t.power = seq, s.Completeness, s.PowerNetworks
	t.rows.apply(s.Buildings, s.Removed)
}
func (t *buildingTable) view(ctx *c.ObservationContext) proto.Message {
	return &o.BuildingsSnapshot{Context: ctx, PowerNetworks: t.power, Completeness: t.completeness, Buildings: t.rows.list()}
}

type thingTable struct {
	rows *rowTable[o.Thing]
	at   uint64
}

func (t *thingTable) seq() uint64 { return t.at }
func (t *thingTable) load(m proto.Message, seq uint64) {
	t.at = seq
	t.rows.load(m.(*o.ThingsSnapshot).Things)
}
func (t *thingTable) apply(m proto.Message, seq uint64) {
	s := m.(*o.ThingsSnapshot)
	t.at = seq
	t.rows.apply(s.Things, s.Removed)
}
func (t *thingTable) view(ctx *c.ObservationContext) proto.Message {
	return &o.ThingsSnapshot{Context: ctx, Things: t.rows.list()}
}

// rowTable is a keyed row list updated in place: rows in first-seen order
// with a slot per id; a removed row leaves a nil slot that list compacts.
type rowTable[R any] struct {
	id   func(*R) string
	rows []*R
	at   map[string]int
	dead int
}

func newRowTable[R any](id func(*R) string) *rowTable[R] {
	return &rowTable[R]{id: id, at: map[string]int{}}
}

// load replaces the table with rows, copying the slice: the caller's
// frame keeps its own.
func (t *rowTable[R]) load(rows []*R) {
	t.rows, t.dead = slices.Clone(rows), 0
	t.at = make(map[string]int, len(rows))
	for i, r := range t.rows {
		t.at[t.id(r)] = i
	}
}

// apply drops the removed ids, replaces each changed row of a held id in
// place and appends the others.
func (t *rowTable[R]) apply(changed []*R, removed []string) {
	for _, k := range removed {
		if i, ok := t.at[k]; ok {
			t.rows[i] = nil
			delete(t.at, k)
			t.dead++
		}
	}
	for _, r := range changed {
		k := t.id(r)
		if i, ok := t.at[k]; ok {
			t.rows[i] = r
			continue
		}
		t.at[k] = len(t.rows)
		t.rows = append(t.rows, r)
	}
}

// list is the live rows. It compacts removed slots first (O(rows), only
// after a removal) and otherwise shares the table's slice without copying.
func (t *rowTable[R]) list() []*R {
	if t.dead > 0 {
		out := t.rows[:0]
		for _, r := range t.rows {
			if r != nil {
				t.at[t.id(r)] = len(out)
				out = append(out, r)
			}
		}
		clear(t.rows[len(out):])
		t.rows, t.dead = out, 0
	}
	return t.rows
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
		t.load(m, w.GetSeq())
		h.tables[name] = t
		return true
	case m != nil && kept != nil && kept.seq() == w.GetBaseSeq():
		kept.apply(m, w.GetSeq())
		k.set(v, kept.view(ctx))
		return true
	case m == nil && kept != nil && kept.seq() == w.GetSeq():
		k.set(v, kept.view(ctx))
		return true
	}
	delete(h.tables, name)
	k.set(v, nil)
	return false
}
