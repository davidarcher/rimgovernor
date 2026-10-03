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
// per family and a delta updates it in place and validates only its
// changed rows, so ingesting a delta frame is O(changed rows); only a
// keyframe visits every row. Consumers read the table through frozen
// copies (rowTable.freeze), built on the first read after a change and
// shared by every reader of that version.
type keyedTable interface {
	seq() uint64
	// load replaces the table with a keyframe's rows and envelope.
	load(m proto.Message, seq uint64, ctx *c.ObservationContext)
	// apply updates the table in place with a delta.
	apply(m proto.Message, seq uint64, ctx *c.ObservationContext)
	// view is the table at ctx as the family's snapshot message. Its row
	// slice is the table's own: valid until the next apply, so only the
	// frame being decoded reads it.
	view(ctx *c.ObservationContext) proto.Message
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
			return &pawnTable{rows: newRowTable(func(r *o.PawnState) string { return r.GetPawn().GetId() }, checkPawnRow)}
		},
	},
	"buildings": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.Buildings) },
		set: func(v *o.BundleSnapshot, m proto.Message) { v.Buildings, _ = m.(*o.BuildingsSnapshot) },
		table: func() keyedTable {
			return &buildingTable{rows: newRowTable(func(r *o.BuildingState) string { return r.GetBuilding().GetId() }, nil)}
		},
	},
	"things": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.Things) },
		set: func(v *o.BundleSnapshot, m proto.Message) { v.Things, _ = m.(*o.ThingsSnapshot) },
		table: func() keyedTable {
			return &thingTable{rows: newRowTable(func(r *o.Thing) string { return r.GetThing().GetId() }, ValidThing)}
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
func (t *pawnTable) view(ctx *c.ObservationContext) proto.Message {
	return &o.PawnSnapshot{Context: ctx, Completeness: t.completeness, MeditateAssignmentAvailable: t.meditate, Pawns: t.rows.list()}
}

// frozen is the table at ctx with its own row slice, safe to keep.
func (t *pawnTable) frozen(ctx *c.ObservationContext) *o.PawnSnapshot {
	return &o.PawnSnapshot{Context: ctx, Completeness: t.completeness, MeditateAssignmentAvailable: t.meditate, Pawns: t.rows.freeze().list}
}

type buildingTable struct {
	rows         *rowTable[o.BuildingState]
	at           uint64
	completeness *o.Completeness
	power        []*o.PowerNetwork
}

func (t *buildingTable) seq() uint64 { return t.at }
func (t *buildingTable) err() error  { return nil }
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
func (t *buildingTable) view(ctx *c.ObservationContext) proto.Message {
	return &o.BuildingsSnapshot{Context: ctx, PowerNetworks: t.power, Completeness: t.completeness, Buildings: t.rows.list()}
}

// frozen is the table at ctx with its own row slice, safe to keep.
func (t *buildingTable) frozen(ctx *c.ObservationContext) *o.BuildingsSnapshot {
	return &o.BuildingsSnapshot{Context: ctx, PowerNetworks: t.power, Completeness: t.completeness, Buildings: t.rows.freeze().list}
}

type thingTable struct {
	rows *rowTable[o.Thing]
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
func (t *thingTable) view(ctx *c.ObservationContext) proto.Message {
	return &o.ThingsSnapshot{Context: ctx, Things: t.rows.list()}
}

// rowTable is a keyed row list updated in place: rows in first-seen order
// with a slot per id; a removed row leaves a nil slot that list compacts.
// A row with no id is kept in the list but is not addressable. check, when
// set, validates each row as it arrives; bad holds the rows that failed.
type rowTable[R any] struct {
	id     func(*R) string
	check  func(*R, *c.ObservationContext) error
	rows   []*R
	at     map[string]int
	dead   int
	bad    map[string]error
	frozen *frozenRows[R]
}

// frozenRows is one version of a table, immutable once built.
type frozenRows[R any] struct {
	list []*R
	byID map[string]*R
}

func newRowTable[R any](id func(*R) string, check func(*R, *c.ObservationContext) error) *rowTable[R] {
	return &rowTable[R]{id: id, check: check, at: map[string]int{}, bad: map[string]error{}}
}

func (t *rowTable[R]) checkRow(k string, r *R, ctx *c.ObservationContext) {
	delete(t.bad, k)
	if t.check != nil {
		if err := t.check(r, ctx); err != nil {
			t.bad[k] = err
		}
	}
}

// load replaces the table with rows, copying the slice: the caller's
// frame keeps its own.
func (t *rowTable[R]) load(rows []*R, ctx *c.ObservationContext) {
	t.rows, t.dead, t.frozen = slices.Clone(rows), 0, nil
	t.at, t.bad = make(map[string]int, len(rows)), map[string]error{}
	for i, r := range t.rows {
		k := t.id(r)
		if _, dup := t.at[k]; dup && k != "" {
			t.bad["duplicate "+k] = contract("duplicate row %q", k)
		}
		if k != "" {
			t.at[k] = i
		}
		t.checkRow(k, r, ctx)
	}
}

// apply drops the removed ids, replaces each changed row of a held id in
// place and appends the others.
func (t *rowTable[R]) apply(changed []*R, removed []string, ctx *c.ObservationContext) {
	t.frozen = nil
	for _, k := range removed {
		if i, ok := t.at[k]; ok {
			t.rows[i] = nil
			delete(t.at, k)
			delete(t.bad, k)
			t.dead++
		}
	}
	for _, r := range changed {
		k := t.id(r)
		t.checkRow(k, r, ctx)
		if i, ok := t.at[k]; ok && k != "" {
			t.rows[i] = r
			continue
		}
		if k != "" {
			t.at[k] = len(t.rows)
		}
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
				if k := t.id(r); k != "" {
					t.at[k] = len(out)
				}
				out = append(out, r)
			}
		}
		clear(t.rows[len(out):])
		t.rows, t.dead = out, 0
	}
	return t.rows
}

// lookup is the live row of id.
func (t *rowTable[R]) lookup(id string) (*R, bool) {
	i, ok := t.at[id]
	if !ok {
		return nil, false
	}
	return t.rows[i], true
}

// pick is the live rows of the ids in table order.
func (t *rowTable[R]) pick(ids map[string]bool) []*R {
	t.list()
	var at []int
	for k := range ids {
		if i, ok := t.at[k]; ok {
			at = append(at, i)
		}
	}
	slices.Sort(at)
	out := make([]*R, len(at))
	for n, i := range at {
		out[n] = t.rows[i]
	}
	return out
}

// err is the first invalid row in id order, nil when all are valid.
func (t *rowTable[R]) err() error {
	var first string
	var err error
	for k, e := range t.bad {
		if err == nil || k < first {
			first, err = k, e
		}
	}
	return err
}

// freeze is the table's current version as a copy no later apply touches,
// built once per version (O(rows) pointer copies) however many readers ask.
func (t *rowTable[R]) freeze() *frozenRows[R] {
	if t.frozen == nil {
		list := slices.Clone(t.list())
		byID := make(map[string]*R, len(list))
		for _, r := range list {
			if k := t.id(r); k != "" {
				byID[k] = r
			}
		}
		t.frozen = &frozenRows[R]{list: list, byID: byID}
	}
	return t.frozen
}

// pawnRows is the held pawn table's live rows for a lookup within the
// frame being decoded; the zero value is an empty table.
type pawnRows struct{ t *pawnTable }

// Row is ref's canonical row, false when the table does not hold it.
func (p pawnRows) Row(ref Reference) (*o.PawnState, bool) {
	if p.t == nil {
		return nil, false
	}
	row, ok := p.t.rows.lookup(ref.GetId())
	return row, ok && row != nil
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
		return true
	case m != nil && kept != nil && kept.seq() == w.GetBaseSeq():
		kept.apply(m, w.GetSeq(), ctx)
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
