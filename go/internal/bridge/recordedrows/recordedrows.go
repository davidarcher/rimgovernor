// Package recordedrows cuts small, deep-copied slices of the recorded whole-game
// catalog (observation/testdata/full_catalog.pb.gz) for tests that need a row
// the game does not have: an invented class, a malformed stat, a second shell.
// A test takes the rows it needs, mutates the slice's copies and decodes the
// wire form with bridge.DecodeDefinitionCatalog (testkit/recordedcatalog does
// it). The package imports no bridge code so that bridge's own tests can use it.
package recordedrows

import (
	"fmt"
	"slices"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// index maps a def name to its recorded rows.
type index struct {
	things map[string]*d.ThingDef
	facts  map[string]*o.ThingDefFacts
	stats  map[string][]*o.DefStatRow
}

var recorded = sync.OnceValue(func() *index {
	wire, err := testkit.LoadRecordedCatalogWire()
	if err != nil {
		panic(err)
	}
	idx := &index{things: map[string]*d.ThingDef{}, facts: map[string]*o.ThingDefFacts{}, stats: map[string][]*o.DefStatRow{}}
	for _, row := range wire.ThingDefs {
		idx.things[row.GetDefName()] = row
	}
	for _, row := range wire.ThingFacts {
		idx.facts[row.GetDefName()] = row
	}
	for _, row := range wire.StatValues.Rows {
		idx.stats[row.GetDefName()] = append(idx.stats[row.GetDefName()], row)
	}
	return idx
})

// Reporter is what a slice needs of a test to report a misuse: *testing.T and
// *testing.B satisfy it, and Panic serves a fake that holds no test.
type Reporter interface {
	Helper()
	Fatalf(format string, args ...any)
}

type panicker struct{}

func (panicker) Helper() {}
func (panicker) Fatalf(format string, args ...any) {
	panic(fmt.Sprintf(format, args...))
}

// Panic is the Reporter of a slice cut outside a test body: a misuse panics.
var Panic Reporter = panicker{}

// Slice is a copy of a few recorded rows, cheap enough to decode per test: the
// thing defs a predicate keeps, the items and stuffs their cost and stat rows
// name, the projectiles they fire, and every row of the def sets asked for.
// The whole game's class chains and stat table header ride along. Everything in
// it is a deep copy, so a test mutates it freely.
type Slice struct {
	T    Reporter
	Wire *o.DefinitionCatalog
}

func recordedWire(t Reporter) *o.DefinitionCatalog {
	t.Helper()
	wire, err := testkit.LoadRecordedCatalogWire()
	if err != nil {
		t.Fatalf("recorded catalog: %v", err)
	}
	return wire
}

// Take takes the thing defs keep accepts (in recorded order) and the named def
// sets (DefSets field names, e.g. "damage_defs").
func Take(t Reporter, keep func(*d.ThingDef) bool, sets ...string) *Slice {
	t.Helper()
	full := recordedWire(t)
	s := &Slice{T: t, Wire: &o.DefinitionCatalog{
		Context: full.Context, Derived: full.Derived, GameConstants: full.GameConstants, ClassChains: append([]*o.ClassChain(nil), full.ClassChains...),
		StatValues: &o.DefStatTable{Stats: full.StatValues.Stats}, Defs: &d.DefSets{},
	}}
	var names []string
	for _, row := range full.ThingDefs {
		if keep != nil && keep(row) {
			names = append(names, row.GetDefName())
		}
	}
	s.add(names)
	// The decoder refuses a catalog with no terrain: one recorded terrain rides along.
	// A free one, so no item rows are needed for its cost.
	for _, row := range full.StatValues.TerrainRows {
		if len(row.GetCosts()) == 0 {
			s.Wire.StatValues.TerrainRows = append(s.Wire.StatValues.TerrainRows, proto.Clone(row).(*o.DefStatRow))
			for _, terrain := range full.TerrainDefs {
				if terrain.GetDefName() == row.GetDefName() {
					s.Wire.TerrainDefs = append(s.Wire.TerrainDefs, proto.Clone(terrain).(*d.TerrainDef))
				}
			}
			break
		}
	}
	s.AddSets(sets...)
	return s
}

// Add copies the recorded thing defs with these names into the slice, with
// the items and stuffs their cost and stat rows name, the projectiles they
// fire and the meat of a race, unless it holds them already. A name the game does not have fails.
func (s *Slice) Add(names ...string) {
	s.T.Helper()
	idx := recorded()
	for _, name := range names {
		if idx.things[name] == nil && !s.Has(name) {
			s.T.Fatalf("the recorded catalog has no thing def %s", name)
		}
	}
	s.add(names)
}

// Has is whether the slice holds the thing def.
func (s *Slice) Has(name string) bool { return s.Find(name) != nil }

// Clone is a deep copy of the slice, for a test that edits one of two worlds.
func (s *Slice) Clone() *Slice {
	return &Slice{T: s.T, Wire: proto.Clone(s.Wire).(*o.DefinitionCatalog)}
}

// add copies the closure of names (their cost, stuff and projectile rows)
// that the slice lacks, in recorded order.
func (s *Slice) add(names []string) {
	idx := recorded()
	have := map[string]bool{}
	for _, row := range s.Wire.ThingDefs {
		have[row.GetDefName()] = true
	}
	want := map[string]bool{}
	queue := slices.Clone(names)
	for len(queue) > 0 {
		name := queue[0]
		queue = queue[1:]
		if have[name] || want[name] {
			continue
		}
		row := idx.things[name]
		if row == nil {
			continue
		}
		want[name] = true
		for _, stat := range idx.stats[name] {
			for _, cost := range stat.GetCosts() {
				queue = append(queue, cost.GetDefName())
			}
			if stat.GetStuffName() != "" {
				queue = append(queue, stat.GetStuffName())
			}
		}
		// A race decodes only with its meat row: the animal race read takes the
		// meat's nutrition from its stat row.
		queue = append(queue, idx.facts[name].GetRace().GetMeatDef())
		queue = append(queue, row.GetProjectileWhenLoaded(), row.GetPlant().GetHarvestedThingDef())
		for _, verb := range row.GetVerbs() {
			queue = append(queue, verb.GetValue().GetDefaultProjectile())
		}
	}
	for _, row := range recordedWire(s.T).ThingDefs {
		name := row.GetDefName()
		if !want[name] {
			continue
		}
		s.Wire.ThingDefs = append(s.Wire.ThingDefs, proto.Clone(row).(*d.ThingDef))
		s.Wire.ThingFacts = append(s.Wire.ThingFacts, proto.Clone(idx.facts[name]).(*o.ThingDefFacts))
		for _, stat := range idx.stats[name] {
			s.Wire.StatValues.Rows = append(s.Wire.StatValues.Rows, proto.Clone(stat).(*o.DefStatRow))
		}
	}
}

// AddSets copies every recorded row of the named def sets into the slice.
func (s *Slice) AddSets(sets ...string) {
	s.T.Helper()
	src, dst := recordedWire(s.T).Defs.ProtoReflect(), s.Wire.Defs.ProtoReflect()
	for _, set := range sets {
		fd := src.Descriptor().Fields().ByName(protoreflect.Name(set))
		if fd == nil || !fd.IsList() {
			s.T.Fatalf("recorded def sets have no list %q", set)
		}
		from, to := src.Get(fd).List(), dst.Mutable(fd).List()
		for i := range from.Len() {
			to.Append(protoreflect.ValueOfMessage(proto.Clone(from.Get(i).Message().Interface()).ProtoReflect()))
		}
	}
	// The decoder refuses a joy giver that offers a def the slice lacks, so a
	// slice keeps only the givers whose offers it holds.
	if len(s.Wire.Defs.JoyGiverDefs) > 0 {
		held := map[string]bool{}
		for _, row := range s.Wire.ThingDefs {
			held[row.GetDefName()] = true
		}
		keep := s.Wire.Defs.JoyGiverDefs[:0]
		for _, giver := range s.Wire.Defs.JoyGiverDefs {
			all := true
			for _, name := range giver.GetThingDefs() {
				all = all && held[name]
			}
			if all {
				keep = append(keep, giver)
			}
		}
		s.Wire.Defs.JoyGiverDefs = keep
	}
}

// Buildings is the player-buildable defs (buildable says which) and what the
// joy givers offer, with the joy giver and job rows: what the furniture,
// dining and piece-shape rules read, a few hundred rows instead of the whole
// game.
func Buildings(t Reporter, buildable func(*d.ThingDef) bool) *Slice {
	t.Helper()
	offered := map[string]bool{}
	for _, giver := range recordedWire(t).Defs.JoyGiverDefs {
		for _, name := range giver.GetThingDefs() {
			offered[name] = true
		}
	}
	return Take(t, func(row *d.ThingDef) bool { return buildable(row) || offered[row.GetDefName()] }, "joy_giver_defs", "job_defs")
}

// Named keeps the thing defs with these names.
func Named(names ...string) func(*d.ThingDef) bool {
	return func(row *d.ThingDef) bool { return slices.Contains(names, row.GetDefName()) }
}

// Thing is the slice's copy of a recorded thing def, for the test to mutate.
func (s *Slice) Thing(name string) *d.ThingDef {
	s.T.Helper()
	if row := s.Find(name); row != nil {
		return row
	}
	s.T.Fatalf("the slice has no thing def %s", name)
	return nil
}

// Find is Thing without the failure: nil when the slice has no such def.
func (s *Slice) Find(name string) *d.ThingDef {
	for _, row := range s.Wire.ThingDefs {
		if row.GetDefName() == name {
			return row
		}
	}
	return nil
}

// Drop removes thing defs, their facts and their stat rows from the slice.
func (s *Slice) Drop(names ...string) {
	s.T.Helper()
	gone := map[string]bool{}
	for _, name := range names {
		s.Thing(name)
		gone[name] = true
	}
	keepThing := s.Wire.ThingDefs[:0]
	for _, row := range s.Wire.ThingDefs {
		if !gone[row.GetDefName()] {
			keepThing = append(keepThing, row)
		}
	}
	s.Wire.ThingDefs = keepThing
	keepFacts := s.Wire.ThingFacts[:0]
	for _, row := range s.Wire.ThingFacts {
		if !gone[row.GetDefName()] {
			keepFacts = append(keepFacts, row)
		}
	}
	s.Wire.ThingFacts = keepFacts
	keepStats := s.Wire.StatValues.Rows[:0]
	for _, row := range s.Wire.StatValues.Rows {
		if !gone[row.GetDefName()] {
			keepStats = append(keepStats, row)
		}
	}
	s.Wire.StatValues.Rows = keepStats
}

// DropWhere removes the thing defs the predicate accepts.
func (s *Slice) DropWhere(match func(*d.ThingDef) bool) {
	s.T.Helper()
	var names []string
	for _, row := range s.Wire.ThingDefs {
		if match(row) {
			names = append(names, row.GetDefName())
		}
	}
	s.Drop(names...)
}

// ScaleCosts multiplies every cost of a def (whatever its stuff) by num/den,
// keeping each at one unit or more.
func (s *Slice) ScaleCosts(name string, num, den int64) {
	s.T.Helper()
	rows := s.StatRows(name)
	if len(rows) == 0 {
		s.T.Fatalf("the slice has no stat rows for %s", name)
	}
	for _, stat := range rows {
		for _, cost := range stat.GetCosts() {
			cost.Units = proto.Int64(max(1, cost.GetUnits()*num/den))
		}
	}
}

// CopyThing adds a copy of a slice def under a new name (its facts and stat
// rows too) and returns the new thing row.
func (s *Slice) CopyThing(name, as string) *d.ThingDef {
	s.T.Helper()
	row := proto.Clone(s.Thing(name)).(*d.ThingDef)
	row.DefName = as
	s.Wire.ThingDefs = append(s.Wire.ThingDefs, row)
	for _, facts := range s.Wire.ThingFacts {
		if facts.GetDefName() == name {
			c := proto.Clone(facts).(*o.ThingDefFacts)
			c.DefName = as
			s.Wire.ThingFacts = append(s.Wire.ThingFacts, c)
			break
		}
	}
	for _, stat := range s.Wire.StatValues.Rows {
		if stat.GetDefName() == name {
			c := proto.Clone(stat).(*o.DefStatRow)
			c.DefName = as
			s.Wire.StatValues.Rows = append(s.Wire.StatValues.Rows, c)
		}
	}
	return row
}

// Import copies a def of another slice into this one (its facts and stat rows
// too, and the recorded items they name), unless it holds the def already.
func (s *Slice) Import(from *Slice, name string) {
	s.T.Helper()
	if s.Has(name) {
		return
	}
	s.Wire.ThingDefs = append(s.Wire.ThingDefs, proto.Clone(from.Thing(name)).(*d.ThingDef))
	for _, facts := range from.Wire.ThingFacts {
		if facts.GetDefName() == name {
			s.Wire.ThingFacts = append(s.Wire.ThingFacts, proto.Clone(facts).(*o.ThingDefFacts))
			break
		}
	}
	var named []string
	for _, stat := range from.StatRows(name) {
		s.Wire.StatValues.Rows = append(s.Wire.StatValues.Rows, proto.Clone(stat).(*o.DefStatRow))
		for _, cost := range stat.GetCosts() {
			named = append(named, cost.GetDefName())
		}
		named = append(named, stat.GetStuffName())
	}
	idx := recorded()
	for _, item := range named {
		if idx.things[item] != nil {
			s.add([]string{item})
		}
	}
}

// Overwrite replaces the slice's rows of a def (thing, facts, stat rows) with
// another slice's, so a test's edits of a recorded def reach a catalog built
// from a different base. The def must be held by both.
func (s *Slice) Overwrite(from *Slice, name string) {
	s.T.Helper()
	if !s.Has(name) {
		s.T.Fatalf("the slice does not hold %s", name)
	}
	s.Wire.ThingDefs = slices.DeleteFunc(s.Wire.ThingDefs, func(r *d.ThingDef) bool { return r.GetDefName() == name })
	s.Wire.ThingFacts = slices.DeleteFunc(s.Wire.ThingFacts, func(r *o.ThingDefFacts) bool { return r.GetDefName() == name })
	s.Wire.StatValues.Rows = slices.DeleteFunc(s.Wire.StatValues.Rows, func(r *o.DefStatRow) bool { return r.GetDefName() == name })
	s.Import(from, name)
}

// StatRows are the slice's stat rows of a def (one per stuff).
func (s *Slice) StatRows(name string) []*o.DefStatRow {
	var out []*o.DefStatRow
	for _, stat := range s.Wire.StatValues.Rows {
		if stat.GetDefName() == name {
			out = append(out, stat)
		}
	}
	return out
}

// SetRow is the slice's copy of a def-set row, found by its type and name.
func SetRow[T interface {
	proto.Message
	GetDefName() string
}](s *Slice, name string) T {
	s.T.Helper()
	var zero T
	src := s.Wire.Defs.ProtoReflect()
	for i := 0; i < src.Descriptor().Fields().Len(); i++ {
		fd := src.Descriptor().Fields().Get(i)
		if !fd.IsList() || fd.Message() == nil || fd.Message().FullName() != zero.ProtoReflect().Descriptor().FullName() {
			continue
		}
		list := src.Get(fd).List()
		for j := range list.Len() {
			if row := list.Get(j).Message().Interface().(T); row.GetDefName() == name {
				return row
			}
		}
	}
	s.T.Fatalf("the slice has no %T %s", zero, name)
	return zero
}

// Recorded is whether the game has a thing def with this name.
func Recorded(name string) bool { return recorded().things[name] != nil }
