package bridge

import (
	"encoding/json"
	"fmt"
	"strings"
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// fill sets every field of m (one member per oneof, two elements per
// list) to distinct values, n numbering them.
func fill(m protoreflect.Message, depth int, n *int) {
	fields := m.Descriptor().Fields()
	for i := 0; i < fields.Len(); i++ {
		f := fields.Get(i)
		if f.IsMap() || f.Name() == "delta" {
			continue
		}
		if oneof := f.ContainingOneof(); oneof != nil && !oneof.IsSynthetic() && m.WhichOneof(oneof) != nil {
			continue
		}
		if f.Kind() == protoreflect.MessageKind && depth > 5 {
			continue
		}
		if f.IsList() {
			list := m.Mutable(f).List()
			for j := 0; j < 2; j++ {
				if f.Kind() == protoreflect.MessageKind {
					element := list.NewElement()
					fill(element.Message(), depth+1, n)
					list.Append(element)
				} else {
					list.Append(scalar(f, n))
				}
			}
			continue
		}
		if f.Kind() == protoreflect.MessageKind {
			fill(m.Mutable(f).Message(), depth+1, n)
			continue
		}
		m.Set(f, scalar(f, n))
	}
}

func scalar(f protoreflect.FieldDescriptor, n *int) protoreflect.Value {
	*n++
	switch f.Kind() {
	case protoreflect.BoolKind:
		return protoreflect.ValueOfBool(true)
	case protoreflect.StringKind:
		return protoreflect.ValueOfString(fmt.Sprintf("%s-%d", f.Name(), *n))
	case protoreflect.BytesKind:
		return protoreflect.ValueOfBytes([]byte{byte(*n)})
	case protoreflect.EnumKind:
		return protoreflect.ValueOfEnum(1)
	case protoreflect.DoubleKind:
		return protoreflect.ValueOfFloat64(float64(*n) + 0.5)
	case protoreflect.FloatKind:
		return protoreflect.ValueOfFloat32(float32(*n) + 0.5)
	case protoreflect.Int32Kind, protoreflect.Sint32Kind, protoreflect.Sfixed32Kind:
		return protoreflect.ValueOfInt32(int32(*n))
	case protoreflect.Int64Kind, protoreflect.Sint64Kind, protoreflect.Sfixed64Kind:
		return protoreflect.ValueOfInt64(int64(*n))
	case protoreflect.Uint32Kind, protoreflect.Fixed32Kind:
		return protoreflect.ValueOfUint32(uint32(*n))
	default:
		return protoreflect.ValueOfUint64(uint64(*n))
	}
}

// roundTrip splits v, writes every row as JSON, reads it back and joins.
func roundTrip(t *testing.T, v *o.ColonyFactsSnapshot) *o.ColonyFactsSnapshot {
	t.Helper()
	parsed := map[string]map[string]ColonyRow{}
	for section, rows := range SplitColonyFacts(v) {
		parsed[section] = map[string]ColonyRow{}
		for key, row := range rows {
			data, err := json.Marshal(row)
			if err != nil {
				t.Fatalf("%s %s: %v", section, key, err)
			}
			back, err := ParseColonyRow(section, key, data)
			if err != nil {
				t.Fatalf("%s %s: %v", section, key, err)
			}
			if !back.Equal(row) {
				t.Fatalf("%s %s: row changed through JSON", section, key)
			}
			parsed[section][key] = back
		}
	}
	joined, err := JoinColonyFacts(parsed)
	if err != nil {
		t.Fatal(err)
	}
	return joined
}

func TestColonyRowsRoundTrip(t *testing.T) {
	v := &o.ColonyFactsSnapshot{}
	n := 0
	fill(v.ProtoReflect(), 0, &n)
	if joined := roundTrip(t, v); !proto.Equal(joined, v) {
		t.Fatalf("joined snapshot differs from the native one")
	}
	// Unavailable outcomes, empty sub-sections and keyless lists.
	w := proto.Clone(v).(*o.ColonyFactsSnapshot)
	w.Upkeep = &o.UpkeepSection{}
	w.FoodSupply = &o.FoodSupplySection{Outcome: &o.FoodSupplySection_Observed{Observed: &o.FoodSupplyFacts{}}}
	w.Resources = append(w.Resources, proto.Clone(w.Resources[0]).(*o.Quantity))
	if joined := roundTrip(t, w); !proto.Equal(joined, w) {
		t.Fatalf("joined snapshot differs from the native one")
	}
}

func TestColonyRowKeys(t *testing.T) {
	v := &o.ColonyFactsSnapshot{}
	n := 0
	fill(v.ProtoReflect(), 0, &n)
	rows := SplitColonyFacts(v)
	want := map[string]string{
		"colony.resources":   "resources[" + v.Resources[0].GetDefName() + "]",
		"colony.farms":       "farms[" + v.Farms[0].GetZone().GetId() + "]",
		"colony.environment": "environment[" + v.Environment[0].GetId() + "]",
		ColonySection:        "food_climate",
	}
	for section, key := range want {
		if _, ok := rows[section][key]; !ok {
			t.Errorf("%s lacks %s: %v", section, key, keysOf(rows[section]))
		}
	}
	for key := range rows["colony.upkeep"] {
		if strings.HasPrefix(key, "observed") {
			t.Errorf("upkeep key %s names the observed outcome", key)
		}
	}
	if len(rows["colony.upkeep"]) == 0 {
		t.Fatal("upkeep has no rows")
	}
}

func keysOf(rows map[string]ColonyRow) []string {
	out := []string{}
	for k := range rows {
		out = append(out, k)
	}
	return out
}
