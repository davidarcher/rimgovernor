package bridge

import (
	"slices"
	"sync"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// The recorded catalog (observation/testdata/full_catalog.pb.gz) is the game's
// own rows. A test reads them through sharedRecordedCatalog, or, when it needs a
// row the game does not have (an invented class, a malformed stat, a second
// shell), takes a small slice of them, mutates the slice's copies and decodes it.

var sharedRecorded = sync.OnceValues(func() (*DefinitionCatalog, error) {
	wire, err := testkit.LoadRecordedCatalogWire()
	if err != nil {
		return nil, err
	}
	return DecodeDefinitionCatalog(wire, wire.GetContext().GetIdentity())
})

// sharedRecordedCatalog is the decoded recorded catalog, built once per test
// binary. Callers must not mutate it.
func sharedRecordedCatalog(t testing.TB) *DefinitionCatalog {
	t.Helper()
	catalog, err := sharedRecorded()
	if err != nil {
		t.Fatal(err)
	}
	return catalog
}

// recordedIndex maps a def name to its recorded rows.
type recordedIndex struct {
	things map[string]*d.ThingDef
	facts  map[string]*o.ThingDefFacts
	stats  map[string][]*o.DefStatRow
}

var recordedRows = sync.OnceValue(func() *recordedIndex {
	wire, err := testkit.LoadRecordedCatalogWire()
	if err != nil {
		panic(err)
	}
	idx := &recordedIndex{things: map[string]*d.ThingDef{}, facts: map[string]*o.ThingDefFacts{}, stats: map[string][]*o.DefStatRow{}}
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

// recordedSlice is a copy of a few recorded rows, cheap enough to decode per
// test: the thing defs a predicate keeps, the items and stuffs their cost and
// stat rows name, the projectiles they fire, and every row of the def sets
// asked for. The whole game's class chains and stat table header ride along.
// Everything in it is a deep copy, so a test mutates it freely.
type recordedSlice struct {
	t    testing.TB
	wire *o.DefinitionCatalog
	have map[string]bool
}

// sliceRecorded takes the thing defs keep accepts (in recorded order) and the
// named def sets (DefSets field names, e.g. "damage_defs").
func sliceRecorded(t testing.TB, keep func(*d.ThingDef) bool, sets ...string) *recordedSlice {
	t.Helper()
	full := testkit.RecordedCatalogWire(t)
	idx := recordedRows()
	s := &recordedSlice{t: t, have: map[string]bool{}, wire: &o.DefinitionCatalog{
		Context: full.Context, Constants: full.Constants, ClassChains: append([]*o.ClassChain(nil), full.ClassChains...),
		StatValues: &o.DefStatTable{Stats: full.StatValues.Stats}, Defs: &d.DefSets{},
	}}
	var queue []string
	for _, row := range full.ThingDefs {
		if keep != nil && keep(row) {
			queue = append(queue, row.GetDefName())
		}
	}
	for len(queue) > 0 {
		name := queue[0]
		queue = queue[1:]
		if s.have[name] {
			continue
		}
		row := idx.things[name]
		if row == nil {
			continue
		}
		s.have[name] = true
		for _, stat := range idx.stats[name] {
			for _, cost := range stat.GetCosts() {
				queue = append(queue, cost.GetDefName())
			}
			if stat.GetStuffName() != "" {
				queue = append(queue, stat.GetStuffName())
			}
		}
		queue = append(queue, row.GetProjectileWhenLoaded())
		for _, verb := range row.GetVerbs() {
			queue = append(queue, verb.GetValue().GetDefaultProjectile())
		}
	}
	for _, row := range full.ThingDefs {
		if s.have[row.GetDefName()] {
			s.wire.ThingDefs = append(s.wire.ThingDefs, proto.Clone(row).(*d.ThingDef))
			s.wire.ThingFacts = append(s.wire.ThingFacts, proto.Clone(idx.facts[row.GetDefName()]).(*o.ThingDefFacts))
			for _, stat := range idx.stats[row.GetDefName()] {
				s.wire.StatValues.Rows = append(s.wire.StatValues.Rows, proto.Clone(stat).(*o.DefStatRow))
			}
		}
	}
	// The decoder refuses a catalog with no terrain: one recorded terrain rides along.
	// A free one, so no item rows are needed for its cost.
	for _, row := range full.StatValues.TerrainRows {
		if len(row.GetCosts()) == 0 {
			s.wire.StatValues.TerrainRows = append(s.wire.StatValues.TerrainRows, proto.Clone(row).(*o.DefStatRow))
			for _, terrain := range full.TerrainDefs {
				if terrain.GetDefName() == row.GetDefName() {
					s.wire.TerrainDefs = append(s.wire.TerrainDefs, proto.Clone(terrain).(*d.TerrainDef))
				}
			}
			break
		}
	}
	src, dst := full.Defs.ProtoReflect(), s.wire.Defs.ProtoReflect()
	for _, set := range sets {
		fd := src.Descriptor().Fields().ByName(protoreflect.Name(set))
		if fd == nil || !fd.IsList() {
			t.Fatalf("recorded def sets have no list %q", set)
		}
		from, to := src.Get(fd).List(), dst.Mutable(fd).List()
		for i := range from.Len() {
			to.Append(protoreflect.ValueOfMessage(proto.Clone(from.Get(i).Message().Interface()).ProtoReflect()))
		}
	}
	return s
}

// buildingsSlice is the player-buildable defs and what the joy givers offer,
// with the joy giver and job rows: what the furniture, dining and piece-shape
// rules read, a few hundred rows instead of the whole game.
func buildingsSlice(t testing.TB) *recordedSlice {
	t.Helper()
	offered := map[string]bool{}
	for _, giver := range testkit.RecordedCatalogWire(t).Defs.JoyGiverDefs {
		for _, name := range giver.GetThingDefs() {
			offered[name] = true
		}
	}
	return sliceRecorded(t, func(row *d.ThingDef) bool { return Buildable(row) || offered[row.GetDefName()] }, "joy_giver_defs", "job_defs")
}

// named keeps the thing defs with these names.
func named(names ...string) func(*d.ThingDef) bool {
	return func(row *d.ThingDef) bool { return slices.Contains(names, row.GetDefName()) }
}

// thing is the slice's copy of a recorded thing def, for the test to mutate.
func (s *recordedSlice) thing(name string) *d.ThingDef {
	s.t.Helper()
	for _, row := range s.wire.ThingDefs {
		if row.GetDefName() == name {
			return row
		}
	}
	s.t.Fatalf("the slice has no thing def %s", name)
	return nil
}

// drop removes thing defs, their facts and their stat rows from the slice.
func (s *recordedSlice) drop(names ...string) {
	s.t.Helper()
	gone := map[string]bool{}
	for _, name := range names {
		s.thing(name)
		gone[name] = true
	}
	keepThing := s.wire.ThingDefs[:0]
	for _, row := range s.wire.ThingDefs {
		if !gone[row.GetDefName()] {
			keepThing = append(keepThing, row)
		}
	}
	s.wire.ThingDefs = keepThing
	keepFacts := s.wire.ThingFacts[:0]
	for _, row := range s.wire.ThingFacts {
		if !gone[row.GetDefName()] {
			keepFacts = append(keepFacts, row)
		}
	}
	s.wire.ThingFacts = keepFacts
	keepStats := s.wire.StatValues.Rows[:0]
	for _, row := range s.wire.StatValues.Rows {
		if !gone[row.GetDefName()] {
			keepStats = append(keepStats, row)
		}
	}
	s.wire.StatValues.Rows = keepStats
}

// dropWhere removes the thing defs the predicate accepts.
func (s *recordedSlice) dropWhere(match func(*d.ThingDef) bool) {
	s.t.Helper()
	var names []string
	for _, row := range s.wire.ThingDefs {
		if match(row) {
			names = append(names, row.GetDefName())
		}
	}
	s.drop(names...)
}

// scaleCosts multiplies every cost of a def (whatever its stuff) by num/den,
// keeping each at one unit or more.
func (s *recordedSlice) scaleCosts(name string, num, den int64) {
	s.t.Helper()
	rows := s.statRows(name)
	if len(rows) == 0 {
		s.t.Fatalf("the slice has no stat rows for %s", name)
	}
	for _, stat := range rows {
		for _, cost := range stat.GetCosts() {
			cost.Units = proto.Int64(max(1, cost.GetUnits()*num/den))
		}
	}
}

// copyThing adds a copy of a slice def under a new name (its facts and stat
// rows too) and returns the new thing row.
func (s *recordedSlice) copyThing(name, as string) *d.ThingDef {
	s.t.Helper()
	row := proto.Clone(s.thing(name)).(*d.ThingDef)
	row.DefName = as
	s.wire.ThingDefs = append(s.wire.ThingDefs, row)
	for _, facts := range s.wire.ThingFacts {
		if facts.GetDefName() == name {
			c := proto.Clone(facts).(*o.ThingDefFacts)
			c.DefName = as
			s.wire.ThingFacts = append(s.wire.ThingFacts, c)
			break
		}
	}
	for _, stat := range s.wire.StatValues.Rows {
		if stat.GetDefName() == name {
			c := proto.Clone(stat).(*o.DefStatRow)
			c.DefName = as
			s.wire.StatValues.Rows = append(s.wire.StatValues.Rows, c)
		}
	}
	return row
}

// statRows are the slice's stat rows of a def (one per stuff).
func (s *recordedSlice) statRows(name string) []*o.DefStatRow {
	var out []*o.DefStatRow
	for _, stat := range s.wire.StatValues.Rows {
		if stat.GetDefName() == name {
			out = append(out, stat)
		}
	}
	return out
}

// setRow is the slice's copy of a def-set row, found by its type and name.
func setRow[T interface {
	proto.Message
	GetDefName() string
}](s *recordedSlice, name string) T {
	s.t.Helper()
	var zero T
	src := s.wire.Defs.ProtoReflect()
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
	s.t.Fatalf("the slice has no %T %s", zero, name)
	return zero
}

// catalog decodes the slice.
func (s *recordedSlice) catalog() *DefinitionCatalog {
	s.t.Helper()
	catalog, err := s.decode()
	if err != nil {
		s.t.Fatal(err)
	}
	return catalog
}

func (s *recordedSlice) decode() (*DefinitionCatalog, error) {
	return DecodeDefinitionCatalog(s.wire, s.wire.GetContext().GetIdentity())
}
