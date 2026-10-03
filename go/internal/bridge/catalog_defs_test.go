package bridge

import (
	"fmt"
	"math"
	"testing"
	"time"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// TestDefRowKeepsAbsentListEntryPosition (#1781): the game uses a null list
// entry positionally (ThoughtDef.stages), so the wrapped entry stays in place
// through the wire and reads as nil.
func TestDefRowKeepsAbsentListEntryPosition(t *testing.T) {
	row := &d.ThoughtDef{DefName: "Mood", Stages: []*d.Opt_ThoughtStage{
		{Value: &d.ThoughtStage{Label: "low"}}, {}, {Value: &d.ThoughtStage{Label: "high"}}}}
	raw, err := proto.Marshal(row)
	if err != nil {
		t.Fatal(err)
	}
	var got d.ThoughtDef
	if err := proto.Unmarshal(raw, &got); err != nil {
		t.Fatal(err)
	}
	stages := got.GetStages()
	if len(stages) != 3 || stages[0].GetValue().GetLabel() != "low" || stages[1].GetValue() != nil || stages[2].GetValue().GetLabel() != "high" {
		t.Fatalf("stages = %v, want low, absent, high in place", stages)
	}
}

// TestDefMirrorShapes pins the generator's rules in the generated bindings
// (#1785): a private field the game's XML loader fills is mirrored
// (ThingDef.verbs), a field that closes a reference cycle is a recursive message
// (quest node children), and every Def message ends with the derived, optional
// modPackageId carrying the clr_path of its source.
func TestDefMirrorShapes(t *testing.T) {
	thing := (&d.ThingDef{}).ProtoReflect().Descriptor()
	if fd := thing.Fields().ByName("verbs"); fd == nil || !fd.IsList() {
		t.Fatalf("private ThingDef.verbs is not mirrored: %v", fd)
	}
	chance := (&d.QuestNode_Chance{}).ProtoReflect().Descriptor()
	sequence := (&d.QuestNode_Sequence{}).ProtoReflect().Descriptor()
	if chance.Fields().ByName("node") == nil || chance.Fields().ByName("elseNode") == nil || sequence.Fields().ByName("nodes") == nil {
		t.Fatal("quest node children are not mirrored")
	}
	roots := []protoreflect.MessageDescriptor{thing, (&d.TerrainDef{}).ProtoReflect().Descriptor()}
	sets := (&d.DefSets{}).ProtoReflect().Descriptor()
	for i := range sets.Fields().Len() {
		roots = append(roots, sets.Fields().Get(i).Message())
	}
	if len(roots) < 250 {
		t.Fatalf("%d def classes", len(roots))
	}
	for _, root := range roots {
		fd := root.Fields().ByName("modPackageId")
		if fd == nil || !fd.HasOptionalKeyword() || fd.Kind() != protoreflect.StringKind {
			t.Fatalf("%s has no optional string modPackageId: %v", root.FullName(), fd)
		}
		if path, _ := proto.GetExtension(fd.Options(), d.E_ClrPath).(string); path != "modContentPack.PackageId" {
			t.Fatalf("%s.modPackageId clr_path = %q", root.FullName(), path)
		}
		if fd.Number() != protoreflect.FieldNumber(root.Fields().Len()) {
			t.Fatalf("%s.modPackageId is field %d, want the last of %d", root.FullName(), fd.Number(), root.Fields().Len())
		}
	}
	// A def with no mod leaves the field unset, which is not a mod named "".
	row := &d.StatDef{DefName: "Beauty"}
	if row.ModPackageId != nil {
		t.Fatal("modPackageId set by default")
	}
	row.ModPackageId = proto.String("ludeon.rimworld.odyssey")
	raw, err := proto.Marshal(&d.StatDef{DefName: "Beauty", ModPackageId: row.ModPackageId})
	if err != nil {
		t.Fatal(err)
	}
	var got d.StatDef
	if err := proto.Unmarshal(raw, &got); err != nil || got.GetModPackageId() != "ludeon.rimworld.odyssey" {
		t.Fatalf("round trip %v %v", got.GetModPackageId(), err)
	}
	// A quest tree walks from the row.
	script := &d.QuestScriptDef{DefName: "Q", Root: &d.QuestNodeAny{Value: &d.QuestNodeAny_QuestNode_Sequence{QuestNode_Sequence: &d.QuestNode_Sequence{Nodes: []*d.Opt_QuestNodeAny{
		{Value: &d.QuestNodeAny{Value: &d.QuestNodeAny_QuestNode_Chance{QuestNode_Chance: &d.QuestNode_Chance{Chance: "0.5", Node: &d.QuestNodeAny{Value: &d.QuestNodeAny_QuestNode_Sequence{QuestNode_Sequence: &d.QuestNode_Sequence{}}}}}}}, {}}}}}}
	raw, err = proto.Marshal(script)
	if err != nil {
		t.Fatal(err)
	}
	var tree d.QuestScriptDef
	if err := proto.Unmarshal(raw, &tree); err != nil {
		t.Fatal(err)
	}
	nodes := tree.GetRoot().GetQuestNode_Sequence().GetNodes()
	if len(nodes) != 2 || nodes[0].GetValue().GetQuestNode_Chance().GetNode().GetQuestNode_Sequence() == nil || nodes[1].GetValue() != nil {
		t.Fatalf("quest tree %v", &tree)
	}
}

// TestClassIsAFollowsBaseChains (#1785): a family is a base class, matched
// through the chains the catalog carries, with a mod's subclass included and
// an unknown class an error rather than false.
func TestClassIsAFollowsBaseChains(t *testing.T) {
	build := func() *o.DefinitionCatalog {
		v := catalogReply(authorityTestContext(7)).GetObserved()
		v.ThingDefs = []*d.ThingDef{{DefName: "Wall"}}
		v.TerrainDefs = []*d.TerrainDef{{DefName: "Soil"}}
		v.Constants = catalogConstants()
		v.Defs = &d.DefSets{StatDefs: []*d.StatDef{{DefName: "Beauty"}}}
		v.ClassChains = []*o.ClassChain{
			{Name: "RimWorld.StatDef", Bases: []string{"Verse.Def"}},
			{Name: "RimWorld.GameCondition_NoSunlight", Bases: []string{"RimWorld.GameCondition"}},
			{Name: "SomeMod.GameCondition_DeepDark", Bases: []string{"RimWorld.GameCondition_NoSunlight", "RimWorld.GameCondition"}},
			{Name: "Verse.Thing"},
		}
		return v
	}
	catalog, err := DecodeDefinitionCatalog(build(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	for _, c := range []struct {
		class, base string
		want        bool
	}{
		{"SomeMod.GameCondition_DeepDark", "RimWorld.GameCondition_NoSunlight", true},
		{"SomeMod.GameCondition_DeepDark", "RimWorld.GameCondition", true},
		{"RimWorld.GameCondition_NoSunlight", "RimWorld.GameCondition_NoSunlight", true},
		{"RimWorld.GameCondition_NoSunlight", "SomeMod.GameCondition_DeepDark", false},
		{"RimWorld.GameCondition_NoSunlight", "Verse.Def", false},
		{"Verse.Thing", "Verse.Thing", true},
	} {
		if got, err := catalog.ClassIsA(c.class, c.base); err != nil || got != c.want {
			t.Errorf("ClassIsA(%s, %s) = %v, %v; want %v", c.class, c.base, got, err, c.want)
		}
	}
	if _, err := catalog.ClassIsA("SomeMod.Unseen", "Verse.Def"); err == nil {
		t.Error("an unknown class matched without an error")
	}
	if got, err := catalog.RowIsA(&d.StatDef{DefName: "Beauty"}, "Verse.Def"); err != nil || !got {
		t.Errorf("RowIsA(StatDef, Verse.Def) = %v, %v", got, err)
	}
	if got, err := catalog.RowIsA(&d.StatDef{}, "RimWorld.GameCondition"); err != nil || got {
		t.Errorf("RowIsA(StatDef, GameCondition) = %v, %v", got, err)
	}
	if _, err := catalog.RowIsA(&d.DefSets{}, "Verse.Def"); err == nil {
		t.Error("a message that mirrors no class matched")
	}
	var none *DefinitionCatalog
	if _, err := none.ClassIsA("Verse.Thing", "Verse.Thing"); err == nil {
		t.Error("a nil catalog matched")
	}
	for name, change := range map[string]func(*o.DefinitionCatalog){
		"duplicate":  func(v *o.DefinitionCatalog) { v.ClassChains = append(v.ClassChains, v.ClassChains[0]) },
		"unnamed":    func(v *o.DefinitionCatalog) { v.ClassChains[0].Name = "" },
		"empty-base": func(v *o.DefinitionCatalog) { v.ClassChains[0].Bases = []string{""} },
		"nil":        func(v *o.DefinitionCatalog) { v.ClassChains = append(v.ClassChains, nil) },
	} {
		v := build()
		change(v)
		if _, err := DecodeDefinitionCatalog(v, pbIdentity()); err == nil {
			t.Errorf("%s class chain accepted", name)
		}
	}
}

func catalogConstants() *o.CatalogConstants {
	return &o.CatalogConstants{TicksPerHour: 2500, TicksPerDay: 60000, DaysPerYear: 60, BillStackMax: 15, SkillMaxLevel: 20, LitGlowThreshold: 0.3, FullRotRateC: 10, RoofMaxSupportDistance: 6.9, CurrencyDef: "Silver"}
}

// TestDefinitionCatalogCarriesGeneratedDefRows (#1730): the generated rows
// and constants decode into the cache keyed by def name, a thing and a
// terrain may share a name, and a malformed row or constant is refused.
func TestDefinitionCatalogCarriesGeneratedDefRows(t *testing.T) {
	context := authorityTestContext(7)
	build := func() *o.DefinitionCatalog {
		v := catalogReply(context).GetObserved()
		v.ThingDefs = []*d.ThingDef{{DefName: "Wall", Label: "wall", StackLimit: 1, Comps: []*d.Opt_CompPropertiesAny{{Value: &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties{CompProperties: &d.CompProperties{CompClass: "Verse.CompForbiddable"}}}}}}, {DefName: "Bed"}}
		v.TerrainDefs = []*d.TerrainDef{{DefName: "Wall", Label: "floor"}}
		v.Constants = catalogConstants()
		v.Defs = &d.DefSets{StatDefs: []*d.StatDef{{DefName: "Wall", Label: "stat"}}, RecipeDefs: []*d.RecipeDef{{DefName: "Wall"}, {DefName: "Bed"}}}
		return v
	}
	catalog, err := DecodeDefinitionCatalog(build(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if row := catalog.ThingDef("Wall"); row == nil || row.StackLimit != 1 || row.Comps[0].GetValue().GetCompProperties().CompClass != "Verse.CompForbiddable" {
		t.Fatalf("thing row %+v", row)
	}
	if row := catalog.TerrainDef("Wall"); row == nil || row.Label != "floor" {
		t.Fatalf("terrain row %+v", row)
	}
	if row := DefRow[*d.StatDef](catalog, "Wall"); row == nil || row.Label != "stat" || DefRow[*d.RecipeDef](catalog, "Bed") == nil || DefRow[*d.RecipeDef](catalog, "Missing") != nil || DefRow[*d.TraitDef](catalog, "Wall") != nil {
		t.Fatalf("def set rows %+v", catalog.Defs)
	}
	if catalog.ThingDef("Missing") != nil || catalog.TerrainDef("Bed") != nil || catalog.Constants.TicksPerDay != 60000 {
		t.Fatalf("lookup %+v", catalog)
	}
	for _, change := range []string{"duplicate-thing", "duplicate-terrain", "unnamed-thing", "nil-terrain", "zero-constant", "nan-glow", "no-full-rot-rate", "no-thing-defs", "no-terrain-defs", "no-constants", "no-def-sets", "empty-def-sets", "duplicate-def-row", "unnamed-def-row"} {
		t.Run(change, func(t *testing.T) {
			v := build()
			switch change {
			case "duplicate-thing":
				v.ThingDefs = append(v.ThingDefs, &d.ThingDef{DefName: "Bed"})
			case "duplicate-terrain":
				v.TerrainDefs = append(v.TerrainDefs, v.TerrainDefs[0])
			case "unnamed-thing":
				v.ThingDefs[0].DefName = ""
			case "nil-terrain":
				v.TerrainDefs = append(v.TerrainDefs, nil)
			case "zero-constant":
				v.Constants.BillStackMax = 0
			case "no-thing-defs":
				v.ThingDefs = nil
			case "no-terrain-defs":
				v.TerrainDefs = nil
			case "no-constants":
				v.Constants = nil
			case "no-def-sets":
				v.Defs = nil
			case "empty-def-sets":
				v.Defs = &d.DefSets{}
			case "duplicate-def-row":
				v.Defs.RecipeDefs = append(v.Defs.RecipeDefs, &d.RecipeDef{DefName: "Bed"})
			case "unnamed-def-row":
				v.Defs.StatDefs[0].DefName = ""
			case "nan-glow":
				v.Constants.LitGlowThreshold = float32(math.NaN())
			case "no-full-rot-rate":
				v.Constants.FullRotRateC = 0
			}
			if _, err := DecodeDefinitionCatalog(v, pbIdentity()); err == nil {
				t.Fatal("malformed rows accepted")
			}
		})
	}
}

// fillDef sets every stride-th field of m to a non-default value, recursing
// into messages to depth: a synthetic stand-in for a def row of the game.
func fillDef(m protoreflect.Message, depth, stride int, seed *int) {
	fields := m.Descriptor().Fields()
	wrapper := m.Descriptor().Oneofs().Len() > 0 && !m.Descriptor().Oneofs().Get(0).IsSynthetic()
	for i := 0; i < fields.Len(); i++ {
		fd := fields.Get(i)
		if wrapper && i > 0 {
			return // a polymorphic wrapper holds one arm
		}
		if !wrapper && i%stride != 0 {
			continue
		}
		*seed++
		one := func() protoreflect.Value {
			switch fd.Kind() {
			case protoreflect.BoolKind:
				return protoreflect.ValueOfBool(true)
			case protoreflect.Int32Kind:
				return protoreflect.ValueOfInt32(int32(*seed))
			case protoreflect.Uint32Kind:
				return protoreflect.ValueOfUint32(uint32(*seed))
			case protoreflect.Int64Kind:
				return protoreflect.ValueOfInt64(int64(*seed))
			case protoreflect.Uint64Kind:
				return protoreflect.ValueOfUint64(uint64(*seed))
			case protoreflect.DoubleKind:
				return protoreflect.ValueOfFloat64(float64(*seed) / 7)
			case protoreflect.FloatKind:
				return protoreflect.ValueOfFloat32(float32(*seed) / 7)
			case protoreflect.StringKind:
				return protoreflect.ValueOfString(fmt.Sprintf("Value_%d_%s", *seed, fd.Name()))
			case protoreflect.EnumKind:
				return protoreflect.ValueOfEnum(fd.Enum().Values().Get(fd.Enum().Values().Len() - 1).Number())
			default:
				return protoreflect.Value{}
			}
		}
		if fd.Message() != nil {
			if depth == 0 {
				continue
			}
			if fd.IsList() {
				list := m.Mutable(fd).List()
				for range 3 {
					item := list.NewElement()
					fillDef(item.Message(), depth-1, stride, seed)
					list.Append(item)
				}
				continue
			}
			fillDef(m.Mutable(fd).Message(), depth-1, stride, seed)
			continue
		}
		if fd.IsList() {
			list := m.Mutable(fd).List()
			for range 3 {
				list.Append(one())
			}
			continue
		}
		m.Set(fd, one())
	}
}

// syntheticCatalog is a reply sized like the game's: about 2900 thing defs and
// 120 terrain defs plus perClass defs of each of the other def classes (#1761),
// each filled in every stride-th field to a depth of three messages. The game
// is not available to the test; the numbers are an estimate of the order, not a
// measurement of the real reply.
func syntheticCatalog(stride, perClass int) *o.DefinitionCatalog {
	v := catalogReply(authorityTestContext(7)).GetObserved()
	seed := 0
	v.ThingDefs, v.TerrainDefs = nil, nil
	for i := range 2900 {
		row := &d.ThingDef{}
		fillDef(row.ProtoReflect(), 3, stride, &seed)
		row.DefName = fmt.Sprintf("Thing%d", i)
		v.ThingDefs = append(v.ThingDefs, row)
	}
	for i := range 120 {
		row := &d.TerrainDef{}
		fillDef(row.ProtoReflect(), 3, stride, &seed)
		row.DefName = fmt.Sprintf("Terrain%d", i)
		v.TerrainDefs = append(v.TerrainDefs, row)
	}
	v.Defs = &d.DefSets{}
	sets := v.Defs.ProtoReflect()
	for i := range sets.Descriptor().Fields().Len() {
		fd := sets.Descriptor().Fields().Get(i)
		list := sets.Mutable(fd).List()
		for j := range perClass {
			row := list.NewElement()
			fillDef(row.Message(), 3, stride, &seed)
			row.Message().Set(fd.Message().Fields().ByName("defName"), protoreflect.ValueOfString(fmt.Sprintf("%s%d", fd.Message().Name(), j)))
			list.Append(row)
		}
	}
	v.Constants = catalogConstants()
	return v
}

// TestDefinitionCatalogDecodeTime (#1730) decodes a synthesized catalog reply:
// the binary reply is parsed, validated and indexed within the accepted
// maximum, and the size and time are logged for the commit record.
func TestDefinitionCatalogDecodeTime(t *testing.T) {
	// A third of every def filled, 40 defs of each other class.
	for _, shape := range []struct{ stride, perClass int }{{3, 40}} {
		t.Run(fmt.Sprintf("stride%d-per%d", shape.stride, shape.perClass), func(t *testing.T) {
			v := syntheticCatalog(shape.stride, shape.perClass)
			raw, err := proto.Marshal(&o.DefinitionCatalogReply{Outcome: &o.DefinitionCatalogReply_Observed{Observed: v}})
			if err != nil {
				t.Fatal(err)
			}
			if len(raw) >= maxReplyProtoBytes {
				t.Fatalf("synthetic reply %d bytes exceeds the reply guard %d", len(raw), maxReplyProtoBytes)
			}
			began := time.Now()
			reply := &o.DefinitionCatalogReply{}
			if err := proto.Unmarshal(raw, reply); err != nil {
				t.Fatal(err)
			}
			parsed := time.Since(began)
			catalog, err := DecodeDefinitionCatalog(reply.GetObserved(), pbIdentity())
			if err != nil {
				t.Fatal(err)
			}
			classes, rows := len(catalog.Defs), 0
			for _, byName := range catalog.Defs {
				rows += len(byName)
			}
			t.Logf("reply %d bytes, %d thing, %d terrain and %d other defs of %d classes: parse %v, decode %v", len(raw), len(catalog.ThingDefs), len(catalog.TerrainDefs), rows, classes, parsed, time.Since(began)-parsed)
			if catalog.ThingDef("Thing2899") == nil || catalog.TerrainDef("Terrain119") == nil || DefRow[*d.StatDef](catalog, fmt.Sprintf("StatDef%d", shape.perClass-1)) == nil {
				t.Fatal("rows missing")
			}
		})
	}
}
