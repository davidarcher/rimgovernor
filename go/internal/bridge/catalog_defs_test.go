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

func catalogConstants() *o.CatalogConstants {
	return &o.CatalogConstants{TicksPerHour: 2500, TicksPerDay: 60000, DaysPerYear: 60, BillStackMax: 15, SkillMaxLevel: 20, LitGlowThreshold: 0.3}
}

// TestDefinitionCatalogCarriesGeneratedDefRows (#1730): the generated rows
// and constants decode into the cache keyed by def name, a thing and a
// terrain may share a name, and a malformed row or constant is refused.
func TestDefinitionCatalogCarriesGeneratedDefRows(t *testing.T) {
	context := authorityTestContext(7)
	build := func() *o.DefinitionCatalog {
		v := catalogReply(context).GetObserved()
		v.ThingDefs = []*d.ThingDef{{DefName: "Wall", Label: "wall", StackLimit: 1, Comps: []*d.CompPropertiesAny{{Value: &d.CompPropertiesAny_CompProperties{CompProperties: &d.CompProperties{CompClass: "Verse.CompForbiddable"}}}}}, {DefName: "Bed"}}
		v.TerrainDefs = []*d.TerrainDef{{DefName: "Wall", Label: "floor"}}
		v.Constants = catalogConstants()
		return v
	}
	catalog, err := DecodeDefinitionCatalog(build(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if row := catalog.ThingDef("Wall"); row == nil || row.StackLimit != 1 || row.Comps[0].GetCompProperties().CompClass != "Verse.CompForbiddable" {
		t.Fatalf("thing row %+v", row)
	}
	if row := catalog.TerrainDef("Wall"); row == nil || row.Label != "floor" {
		t.Fatalf("terrain row %+v", row)
	}
	if catalog.ThingDef("Missing") != nil || catalog.TerrainDef("Bed") != nil || catalog.Constants.TicksPerDay != 60000 {
		t.Fatalf("lookup %+v", catalog)
	}
	for _, change := range []string{"duplicate-thing", "duplicate-terrain", "unnamed-thing", "nil-terrain", "zero-constant", "nan-glow"} {
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
			case "nan-glow":
				v.Constants.LitGlowThreshold = float32(math.NaN())
			}
			if _, err := DecodeDefinitionCatalog(v, pbIdentity()); err == nil {
				t.Fatal("malformed rows accepted")
			}
		})
	}
	if catalog, err := DecodeDefinitionCatalog(catalogReply(context).GetObserved(), pbIdentity()); err != nil || catalog.Constants != nil || catalog.ThingDef("Wall") != nil {
		t.Fatalf("a reply without generated rows: %+v %v", catalog, err)
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
// 120 terrain defs, each a third filled to a depth of three messages. The game
// is not available to the test; the numbers are an estimate of the order, not a
// measurement of the real reply.
func syntheticCatalog() *o.DefinitionCatalog {
	v := catalogReply(authorityTestContext(7)).GetObserved()
	seed := 0
	for i := range 2900 {
		row := &d.ThingDef{}
		fillDef(row.ProtoReflect(), 3, 3, &seed)
		row.DefName = fmt.Sprintf("Thing%d", i)
		v.ThingDefs = append(v.ThingDefs, row)
	}
	for i := range 120 {
		row := &d.TerrainDef{}
		fillDef(row.ProtoReflect(), 3, 3, &seed)
		row.DefName = fmt.Sprintf("Terrain%d", i)
		v.TerrainDefs = append(v.TerrainDefs, row)
	}
	v.Constants = catalogConstants()
	return v
}

// TestDefinitionCatalogDecodeTime (#1730) decodes a synthesized catalog reply:
// the binary reply is parsed, validated and indexed within the accepted
// maximum, and the size and time are logged for the commit record.
func TestDefinitionCatalogDecodeTime(t *testing.T) {
	v := syntheticCatalog()
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
	t.Logf("reply %d bytes, %d thing and %d terrain defs: parse %v, decode %v", len(raw), len(catalog.ThingDefs), len(catalog.TerrainDefs), parsed, time.Since(began)-parsed)
	if catalog.ThingDef("Thing2899") == nil || catalog.TerrainDef("Terrain119") == nil {
		t.Fatal("rows missing")
	}
}
