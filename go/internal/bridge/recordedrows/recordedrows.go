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
	// stuffs are the stuff defs (those with stuffProps) in recorded order.
	stuffs []*d.ThingDef
}

var recorded = sync.OnceValue(func() *index {
	wire, err := testkit.LoadRecordedCatalogWire()
	if err != nil {
		panic(err)
	}
	idx := &index{things: map[string]*d.ThingDef{}, facts: map[string]*o.ThingDefFacts{}}
	for _, row := range wire.ThingDefs {
		idx.things[row.GetDefName()] = row
		if row.GetStuffProps() != nil {
			idx.stuffs = append(idx.stuffs, row)
		}
	}
	for _, row := range wire.ThingFacts {
		idx.facts[row.GetDefName()] = row
	}
	return idx
})

// materials are the recorded defs a def's stats and adjusted cost list read: the
// items of its cost lists and, when it is made from stuff, every stuff that can
// make it.
func (idx *index) materials(row *d.ThingDef) []string {
	var out []string
	for _, list := range [][]*d.Opt_ThingDefCountClass{row.GetCostList(), row.GetCostListForDifficulty().GetCostList()} {
		for _, cost := range list {
			out = append(out, cost.GetValue().GetThingDef())
		}
	}
	for _, stuff := range idx.stuffs {
		for _, category := range row.GetStuffCategories() {
			if slices.Contains(stuff.GetStuffProps().GetCategories(), category) {
				out = append(out, stuff.GetDefName())
				break
			}
		}
	}
	return out
}

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
// thing defs a predicate keeps, the items and stuffs their cost lists and stuffs
// name, the projectiles they fire, and every row of the def sets asked for.
// The whole game's class chains and stat environment ride along. Everything in
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
		StatEnv: full.StatEnv, Defs: &d.DefSets{},
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
	for _, terrain := range full.TerrainDefs {
		if len(terrain.GetCostList()) == 0 && terrain.GetCostStuffCount() == 0 && terrain.GetCostListForDifficulty() == nil {
			s.Wire.TerrainDefs = append(s.Wire.TerrainDefs, proto.Clone(terrain).(*d.TerrainDef))
			break
		}
	}
	// The stat evaluator reads the stat defs and their categories.
	s.AddSets("stat_defs", "stat_category_defs")
	s.AddSets(sets...)
	return s
}

// Add copies the recorded thing defs with these names into the slice, with
// the items and stuffs their cost lists and stuffs name, the projectiles they
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
		queue = append(queue, idx.materials(row)...)
		queue = append(queue, row.GetProjectileWhenLoaded(), row.GetPlant().GetHarvestedThingDef())
		// A race's meat (its stats price the butchery): the generated
		// Meat_<race>, or the def the race names.
		if race := row.GetRace(); race != nil {
			queue = append(queue, "Meat_"+name, race.GetSpecificMeatDef(), race.GetUseMeatFrom())
		}
		for _, verb := range row.GetVerbs() {
			queue = append(queue, verb.GetValue().GetDefaultProjectile())
		}
	}
	races := false
	for _, row := range recordedWire(s.T).ThingDefs {
		name := row.GetDefName()
		if !want[name] {
			continue
		}
		races = races || row.GetRace() != nil
		s.Wire.ThingDefs = append(s.Wire.ThingDefs, proto.Clone(row).(*d.ThingDef))
		s.Wire.ThingFacts = append(s.Wire.ThingFacts, proto.Clone(idx.facts[name]).(*o.ThingDefFacts))
	}
	// The decoder derives a race's flags, trainables and ages from these rows.
	if races {
		s.AddSets("flesh_type_defs", "trainability_defs", "trainable_defs", "life_stage_defs")
	}
}

// defNameOf is a def row's defName, empty for a message without one.
func defNameOf(row protoreflect.Message) string {
	if fd := row.Descriptor().Fields().ByName("defName"); fd != nil && fd.Kind() == protoreflect.StringKind {
		return row.Get(fd).String()
	}
	return ""
}

// AddSets copies every recorded row of the named def sets into the slice, but
// not a row whose defName the slice's set already holds.
func (s *Slice) AddSets(sets ...string) {
	s.T.Helper()
	src, dst := recordedWire(s.T).Defs.ProtoReflect(), s.Wire.Defs.ProtoReflect()
	for _, set := range sets {
		fd := src.Descriptor().Fields().ByName(protoreflect.Name(set))
		if fd == nil || !fd.IsList() {
			s.T.Fatalf("recorded def sets have no list %q", set)
		}
		from, to := src.Get(fd).List(), dst.Mutable(fd).List()
		held := map[string]bool{}
		for i := range to.Len() {
			held[defNameOf(to.Get(i).Message())] = true
		}
		for i := range from.Len() {
			if name := defNameOf(from.Get(i).Message()); name == "" || !held[name] {
				to.Append(protoreflect.ValueOfMessage(proto.Clone(from.Get(i).Message().Interface()).ProtoReflect()))
			}
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

// Drop removes thing defs and their facts from the slice.
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

// ScaleCosts multiplies the cost list and the stuff count of a def by num/den,
// keeping each entry at one unit or more.
func (s *Slice) ScaleCosts(name string, num, den int32) {
	s.T.Helper()
	row := s.Thing(name)
	for _, cost := range row.GetCostList() {
		cost.GetValue().Count = max(1, cost.GetValue().GetCount()*num/den)
	}
	if row.GetCostStuffCount() > 0 {
		row.CostStuffCount = max(1, row.GetCostStuffCount()*num/den)
	}
}

// CopyThing adds a copy of a slice def under a new name (its facts too)
// and returns the new thing row.
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
	return row
}

// Import copies a def of another slice into this one (its facts
// too, and the recorded items its stats read), unless it holds the def already.
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
	idx := recorded()
	for _, item := range idx.materials(from.Thing(name)) {
		if idx.things[item] != nil {
			s.add([]string{item})
		}
	}
}

// Overwrite replaces the slice's rows of a def (thing, facts) with
// another slice's, so a test's edits of a recorded def reach a catalog built
// from a different base. The def must be held by both.
func (s *Slice) Overwrite(from *Slice, name string) {
	s.T.Helper()
	if !s.Has(name) {
		s.T.Fatalf("the slice does not hold %s", name)
	}
	s.Wire.ThingDefs = slices.DeleteFunc(s.Wire.ThingDefs, func(r *d.ThingDef) bool { return r.GetDefName() == name })
	s.Wire.ThingFacts = slices.DeleteFunc(s.Wire.ThingFacts, func(r *o.ThingDefFacts) bool { return r.GetDefName() == name })
	s.Import(from, name)
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
