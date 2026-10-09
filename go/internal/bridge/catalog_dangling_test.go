package bridge

import (
	"sort"
	"strings"
	"testing"

	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// danglingRef is one def-reference field value that names no row.
type danglingRef struct {
	Owner  string // the message and field holding the reference
	Target string // the CLR Def class the field references
	Name   string // the missing defName
}

func (r danglingRef) String() string { return r.Owner + " -> " + r.Target + " " + r.Name }

// catalogRows is every def row of the catalog: ThingDefs, TerrainDefs and
// the other classes' sets.
func catalogRows(catalog *DefinitionCatalog) []proto.Message {
	var rows []proto.Message
	for _, row := range catalog.ThingDefs {
		rows = append(rows, row)
	}
	for _, row := range catalog.TerrainDefs {
		rows = append(rows, row)
	}
	for _, set := range catalog.Defs {
		for _, row := range set {
			rows = append(rows, row)
		}
	}
	return rows
}

// danglingRefs walks every row of the catalog and returns each non-empty
// defName in a field marked clr_def_ref that no row of the referenced class
// (or a subclass) carries. Dictionary keys and values and nested collections
// of defNames carry no marker and are not checked.
func danglingRefs(t testing.TB, catalog *DefinitionCatalog) []danglingRef {
	t.Helper()
	rows := catalogRows(catalog)
	names := map[string]map[string]bool{}
	namesOf := func(target string) map[string]bool {
		if got, ok := names[target]; ok {
			return got
		}
		got := map[string]bool{}
		for _, row := range rows {
			is, err := catalog.RowIsA(row, target)
			if err != nil {
				t.Fatal(err)
			}
			if is {
				got[defRowName(row)] = true
			}
		}
		names[target] = got
		return got
	}
	seen := map[danglingRef]bool{}
	var walk func(m protoreflect.Message)
	walk = func(m protoreflect.Message) {
		m.Range(func(fd protoreflect.FieldDescriptor, v protoreflect.Value) bool {
			if target, _ := proto.GetExtension(fd.Options(), d.E_ClrDefRef).(string); target != "" && fd.Kind() == protoreflect.StringKind {
				check := func(name string) {
					if name != "" && !namesOf(target)[name] {
						seen[danglingRef{string(m.Descriptor().FullName()) + "." + string(fd.Name()), target, name}] = true
					}
				}
				if fd.IsList() {
					for i := range v.List().Len() {
						check(v.List().Get(i).String())
					}
				} else {
					check(v.String())
				}
				return true
			}
			switch {
			case fd.IsMap():
			case fd.Kind() != protoreflect.MessageKind && fd.Kind() != protoreflect.GroupKind:
			case fd.IsList():
				for i := range v.List().Len() {
					walk(v.List().Get(i).Message())
				}
			default:
				walk(v.Message())
			}
			return true
		})
	}
	for _, row := range rows {
		walk(row.ProtoReflect())
	}
	out := make([]danglingRef, 0, len(seen))
	for r := range seen {
		out = append(out, r)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].String() < out[j].String() })
	return out
}

func defRowName(row proto.Message) string {
	m := row.ProtoReflect()
	return m.Get(m.Descriptor().Fields().ByName("defName")).String()
}

// knownDangling classifies the references in the recording that name no row:
// a game bug (the game's own XML names a def it does not define), a mirror
// bug (the mirror drops or mis-types the target) or a mod-only reference.
// Every entry is a finding kept until its cause is fixed; a new dangling
// reference, or an entry that no longer dangles, fails the test.
var knownDangling = map[string]string{
	// Game bug: an Anomaly WeatherDef names an ambient SoundDef the game never defines.
	"rimgovernor.defs.v1.WeatherDef.ambientSounds -> Verse.SoundDef Ambient_MetalHell": "game bug",
}

func TestRecordedCatalogHasNoDanglingDefReferences(t *testing.T) {
	catalog := fullCatalog(t)
	got := danglingRefs(t, catalog)
	var unknown []string
	left := map[string]bool{}
	for k := range knownDangling {
		left[k] = true
	}
	for _, r := range got {
		if _, ok := knownDangling[r.String()]; ok {
			delete(left, r.String())
			continue
		}
		unknown = append(unknown, r.String())
	}
	if len(unknown) > 0 {
		t.Errorf("%d dangling def references (classify each as a game bug, mirror bug or mod-only reference in knownDangling):\n%s", len(unknown), strings.Join(unknown, "\n"))
	}
	for k := range left {
		t.Errorf("knownDangling entry %q no longer dangles; delete it", k)
	}
}

func TestDanglingWalkFlagsABrokenReference(t *testing.T) {
	catalog := fullCatalog(t)
	var victim *d.ThingDef
	for _, row := range catalog.ThingDefs {
		if len(row.StuffCategories) > 0 {
			victim = row
			break
		}
	}
	if victim == nil {
		t.Fatal("no ThingDef with stuffCategories in the recording")
	}
	victim.StuffCategories = append(victim.StuffCategories, "NoSuchStuffCategory")
	for _, r := range danglingRefs(t, catalog) {
		if r.Owner == "rimgovernor.defs.v1.ThingDef.stuffCategories" && r.Name == "NoSuchStuffCategory" {
			return
		}
	}
	t.Fatal("a broken stuffCategories entry was not reported")
}

// missingTableDefs returns each name of a Go def-name table that no row of
// the table's class carries.
func missingTableDefs(t testing.TB, catalog *DefinitionCatalog, tables []policy.DefTable) []string {
	t.Helper()
	var missing []string
	rows := catalogRows(catalog)
	for _, table := range tables {
		have := map[string]bool{}
		for _, row := range rows {
			is, err := catalog.RowIsA(row, table.Class)
			if err != nil {
				t.Fatal(err)
			}
			if is {
				have[defRowName(row)] = true
			}
		}
		if len(have) == 0 {
			missing = append(missing, table.Table+": the catalog has no "+table.Class+" rows")
			continue
		}
		for _, name := range table.Names {
			if !have[name] {
				missing = append(missing, table.Table+": "+table.Class+" "+name)
			}
		}
	}
	return missing
}

func TestGoDefNameTablesNameCatalogRows(t *testing.T) {
	if missing := missingTableDefs(t, fullCatalog(t), policy.DefTables()); len(missing) > 0 {
		t.Errorf("Go def-name tables name defs the catalog lacks:\n%s", strings.Join(missing, "\n"))
	}
}

func TestMissingTableDefsFlagsABrokenEntry(t *testing.T) {
	tables := append(policy.DefTables(), policy.DefTable{Table: "broken", Class: "Verse.HediffDef", Names: []string{"NoSuchHediff"}})
	got := missingTableDefs(t, fullCatalog(t), tables)
	if len(got) != 1 || got[0] != "broken: Verse.HediffDef NoSuchHediff" {
		t.Fatalf("missing = %v", got)
	}
}
