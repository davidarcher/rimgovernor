package bridge

import (
	"encoding/json"
	"fmt"
	"slices"
	"sort"
	"strings"
	"sync"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
	"google.golang.org/protobuf/reflect/protoregistry"
)

// Colony facts as mirror rows (#795 step 3). A ColonyFactsSnapshot splits
// into sections of native rows, one section per sub-section:
//
//   - "colony.<field>" for each top-level field that holds keyed rows: a
//     list whose elements carry a key (resources, farms, environment,
//     cooking, ...) or a sub-section message that holds one (upkeep,
//     food_supply, planning, ...);
//   - "colony" for the rest (scalars, context, naming, food_climate, ...),
//     one row per field.
//
// A row's key is its path within the sub-section: field names joined by
// '.', a keyed list element as <list>[<key>] and the list's order as
// <list>[]. A sub-section's observed outcome is the sub-section itself, so
// upkeep's beds are beds[<id>], not observed.beds[<id>]. An element's key
// is the row key (its string id, else the id of its first
// singular message field), else its zone_id, else its def_name; a list
// whose elements do not all carry distinct keys is one row. A row is the
// element itself, or its parent message with only that field set;
// JoinColonyFacts merges the rows back into the snapshot the native sent.

// ColonySection is the root colony facts section.
const ColonySection = "colony"

// ColonyRow is one colony facts row: a message part, or a keyed list's
// element keys in native order.
type ColonyRow struct {
	Part  proto.Message
	Order []string
}

// MarshalJSON is the part's protojson, or the order as a JSON array.
func (r ColonyRow) MarshalJSON() ([]byte, error) {
	if r.Part != nil {
		return protojson.Marshal(r.Part)
	}
	return json.Marshal(r.Order)
}

// Equal compares two rows' facts.
func (r ColonyRow) Equal(x ColonyRow) bool {
	if (r.Part == nil) != (x.Part == nil) {
		return false
	}
	if r.Part != nil {
		return proto.Equal(r.Part, x.Part)
	}
	return slices.Equal(r.Order, x.Order)
}

// colonyFallbackKeys are the element fields a list keys on when the #773
// rule finds none: farms by zone, resources by definition.
var colonyFallbackKeys = []protoreflect.Name{"zone_id", "def_name"}

type colonyLayout struct {
	sections []string
	// field is each own section's top-level field.
	field map[string]protoreflect.FieldDescriptor
	// observed is the observed outcome a sub-section's rows sit in.
	observed map[string]protoreflect.FieldDescriptor
}

var colonyLayoutOnce = sync.OnceValue(func() colonyLayout {
	out := colonyLayout{sections: []string{ColonySection}, field: map[string]protoreflect.FieldDescriptor{}, observed: map[string]protoreflect.FieldDescriptor{}}
	fields := (&o.ColonyFactsSnapshot{}).ProtoReflect().Descriptor().Fields()
	for i := 0; i < fields.Len(); i++ {
		f := fields.Get(i)
		if !ownSection(f) {
			continue
		}
		name := ColonySection + "." + string(f.Name())
		out.sections = append(out.sections, name)
		out.field[name] = f
		if observed := observedOutcome(f); observed != nil {
			out.observed[name] = observed
		}
	}
	sort.Strings(out.sections[1:])
	return out
})

// ColonySections names every colony facts section, the root first.
func ColonySections() []string { return slices.Clone(colonyLayoutOnce().sections) }

func ownSection(f protoreflect.FieldDescriptor) bool {
	if f.Kind() != protoreflect.MessageKind || f.IsMap() || f.Name() == "delta" {
		return false
	}
	if f.IsList() {
		return keyable(f.Message())
	}
	return holdsKeyed(f.Message(), 0)
}

// observedOutcome is a sub-section's oneof "observed" field, when no field
// of the observed message shares a name with the sub-section's own.
func observedOutcome(f protoreflect.FieldDescriptor) protoreflect.FieldDescriptor {
	if f.IsList() {
		return nil
	}
	observed := f.Message().Fields().ByName("observed")
	if observed == nil || observed.ContainingOneof() == nil || observed.Kind() != protoreflect.MessageKind {
		return nil
	}
	inner := observed.Message().Fields()
	for i := 0; i < inner.Len(); i++ {
		if f.Message().Fields().ByName(inner.Get(i).Name()) != nil {
			return nil
		}
	}
	return observed
}

// keyable reports whether a list of m can carry element keys.
func keyable(m protoreflect.MessageDescriptor) bool {
	if idField(m) != nil {
		return true
	}
	fields := m.Fields()
	for i := 0; i < fields.Len(); i++ {
		f := fields.Get(i)
		if f.Kind() == protoreflect.MessageKind && !f.IsList() && !f.IsMap() && idField(f.Message()) != nil {
			return true
		}
	}
	for _, name := range colonyFallbackKeys {
		if f := fields.ByName(name); f != nil && f.Kind() == protoreflect.StringKind && !f.IsList() {
			return true
		}
	}
	return false
}

// holdsKeyed reports whether m, through singular message fields, holds a
// keyable list.
func holdsKeyed(m protoreflect.MessageDescriptor, depth int) bool {
	if depth > 8 {
		return false
	}
	fields := m.Fields()
	for i := 0; i < fields.Len(); i++ {
		f := fields.Get(i)
		if f.Kind() != protoreflect.MessageKind || f.IsMap() {
			continue
		}
		if f.IsList() && keyable(f.Message()) || !f.IsList() && holdsKeyed(f.Message(), depth+1) {
			return true
		}
	}
	return false
}

// elementKey is a repeated element's key: its string id, else the id of
// its first (by number) singular message field whose type has one.
func elementKey(element protoreflect.Message) string {
	if id := idField(element.Descriptor()); id != nil {
		return element.Get(id).String()
	}
	fields := element.Descriptor().Fields()
	numbers := make([]int, 0, fields.Len())
	for i := 0; i < fields.Len(); i++ {
		numbers = append(numbers, int(fields.Get(i).Number()))
	}
	sort.Ints(numbers)
	for _, number := range numbers {
		field := fields.ByNumber(protoreflect.FieldNumber(number))
		if field.Kind() != protoreflect.MessageKind || field.IsList() || field.IsMap() {
			continue
		}
		id := idField(field.Message())
		if id == nil {
			continue
		}
		if !element.Has(field) {
			return ""
		}
		return element.Get(field).Message().Get(id).String()
	}
	return ""
}

func idField(message protoreflect.MessageDescriptor) protoreflect.FieldDescriptor {
	field := message.Fields().ByName("id")
	if field == nil || field.Kind() != protoreflect.StringKind || field.IsList() {
		return nil
	}
	return field
}

// rowKey is an element's key, "" when it has none.
func rowKey(element protoreflect.Message) string {
	if key := elementKey(element); key != "" {
		return key
	}
	for _, name := range colonyFallbackKeys {
		if f := element.Descriptor().Fields().ByName(name); f != nil && f.Kind() == protoreflect.StringKind && !f.IsList() {
			return element.Get(f).String()
		}
	}
	return ""
}

// listKeys are the list's element keys, false unless every element has a
// distinct one a key path can carry.
func listKeys(list protoreflect.List) ([]string, bool) {
	keys := make([]string, list.Len())
	seen := make(map[string]bool, list.Len())
	for i := range keys {
		key := rowKey(list.Get(i).Message())
		if key == "" || seen[key] || strings.ContainsAny(key, "[]") {
			return nil, false
		}
		seen[key], keys[i] = true, key
	}
	return keys, true
}

// SplitColonyFacts is v's rows by section; every section is present, empty
// when v holds none of it. v is not modified.
func SplitColonyFacts(v *o.ColonyFactsSnapshot) map[string]map[string]ColonyRow {
	layout := colonyLayoutOnce()
	out := make(map[string]map[string]ColonyRow, len(layout.sections))
	for _, name := range layout.sections {
		out[name] = map[string]ColonyRow{}
	}
	root := v.ProtoReflect()
	root.Range(func(f protoreflect.FieldDescriptor, value protoreflect.Value) bool {
		if f.Name() == "delta" {
			return true
		}
		name := ColonySection + "." + string(f.Name())
		rows, own := out[name]
		switch {
		case !own:
			out[ColonySection][string(f.Name())] = partial(root, f)
		case f.IsList():
			splitField(root, f, value, "", rows)
		default:
			section := value.Message()
			if observed := layout.observed[name]; observed != nil && section.Has(observed) {
				splitMessage(section.Get(observed).Message(), "", rows)
				if len(rows) > 0 {
					return true
				}
			}
			splitMessage(section, "", rows)
			if len(rows) == 0 {
				// An empty sub-section is still present.
				out[ColonySection][string(f.Name())] = partial(root, f)
			}
		}
		return true
	})
	return out
}

func splitMessage(m protoreflect.Message, prefix string, rows map[string]ColonyRow) {
	m.Range(func(f protoreflect.FieldDescriptor, value protoreflect.Value) bool {
		splitField(m, f, value, prefix, rows)
		return true
	})
}

func splitField(parent protoreflect.Message, f protoreflect.FieldDescriptor, value protoreflect.Value, prefix string, rows map[string]ColonyRow) {
	key := string(f.Name())
	if prefix != "" {
		key = prefix + "." + key
	}
	if f.Kind() == protoreflect.MessageKind && !f.IsMap() {
		if f.IsList() {
			if keys, ok := listKeys(value.List()); ok {
				for i, k := range keys {
					rows[key+"["+k+"]"] = ColonyRow{Part: proto.Clone(value.List().Get(i).Message().Interface())}
				}
				rows[key+"[]"] = ColonyRow{Order: keys}
				return
			}
		} else if holdsKeyed(f.Message(), 0) {
			n := len(rows)
			splitMessage(value.Message(), key, rows)
			if len(rows) > n {
				return
			}
		}
	}
	rows[key] = partial(parent, f)
}

// partial is parent with only f set.
func partial(parent protoreflect.Message, f protoreflect.FieldDescriptor) ColonyRow {
	out := parent.New()
	out.Set(f, parent.Get(f))
	return ColonyRow{Part: proto.Clone(out.Interface())}
}

// keySegment is one step of a row key.
type keySegment struct {
	name  protoreflect.Name
	key   string
	keyed bool
}

func parseRowKey(key string) ([]keySegment, error) {
	var out []keySegment
	for len(key) > 0 {
		end := strings.IndexAny(key, ".[")
		segment := keySegment{}
		if end < 0 {
			segment.name, key = protoreflect.Name(key), ""
		} else {
			segment.name, key = protoreflect.Name(key[:end]), key[end:]
			if key[0] == '[' {
				close := strings.IndexByte(key, ']')
				if close < 0 {
					return nil, fmt.Errorf("row key %q", key)
				}
				segment.key, segment.keyed, key = key[1:close], true, key[close+1:]
			}
			if len(key) > 0 {
				if key[0] != '.' {
					return nil, fmt.Errorf("row key %q", key)
				}
				key = key[1:]
			}
		}
		if segment.name == "" {
			return nil, fmt.Errorf("row key segment")
		}
		out = append(out, segment)
	}
	if len(out) == 0 {
		return nil, fmt.Errorf("empty row key")
	}
	for _, s := range out[:len(out)-1] {
		if s.keyed {
			return nil, fmt.Errorf("row key descends into a list element")
		}
	}
	return out, nil
}

// rowSite is where a row lands: the message type it is resolved against
// (the root, a sub-section or its observed outcome) and the key's steps.
type rowSite struct {
	segments []keySegment
	// observed: the key is within the sub-section's observed outcome.
	observed bool
}

func siteOf(section, key string) (rowSite, protoreflect.MessageDescriptor, error) {
	layout := colonyLayoutOnce()
	segments, err := parseRowKey(key)
	if err != nil {
		return rowSite{}, nil, err
	}
	root := (&o.ColonyFactsSnapshot{}).ProtoReflect().Descriptor()
	if section == ColonySection {
		return rowSite{segments: segments}, root, nil
	}
	f, ok := layout.field[section]
	if !ok {
		return rowSite{}, nil, fmt.Errorf("colony section %q", section)
	}
	if f.IsList() {
		return rowSite{segments: segments}, root, nil
	}
	if observed := layout.observed[section]; observed != nil && f.Message().Fields().ByName(segments[0].name) == nil {
		return rowSite{segments: segments, observed: true}, observed.Message(), nil
	}
	return rowSite{segments: segments}, f.Message(), nil
}

// ParseColonyRow reads a row's JSON (ColonyRow.MarshalJSON) back by its
// section and key.
func ParseColonyRow(section, key string, data []byte) (ColonyRow, error) {
	site, m, err := siteOf(section, key)
	if err != nil {
		return ColonyRow{}, err
	}
	for _, s := range site.segments[:len(site.segments)-1] {
		f := m.Fields().ByName(s.name)
		if f == nil || f.Kind() != protoreflect.MessageKind || f.IsList() || f.IsMap() {
			return ColonyRow{}, fmt.Errorf("row %s %s: field %s", section, key, s.name)
		}
		m = f.Message()
	}
	last := site.segments[len(site.segments)-1]
	f := m.Fields().ByName(last.name)
	if f == nil {
		return ColonyRow{}, fmt.Errorf("row %s %s: field %s", section, key, last.name)
	}
	if last.keyed {
		if f.Kind() != protoreflect.MessageKind || !f.IsList() {
			return ColonyRow{}, fmt.Errorf("row %s %s: not a list", section, key)
		}
		if last.key == "" {
			var order []string
			return ColonyRow{Order: order}, json.Unmarshal(data, &order)
		}
		m = f.Message()
	}
	mt, err := protoregistry.GlobalTypes.FindMessageByName(m.FullName())
	if err != nil {
		return ColonyRow{}, err
	}
	part := mt.New().Interface()
	if err := protojson.Unmarshal(data, part); err != nil {
		return ColonyRow{}, fmt.Errorf("row %s %s: %w", section, key, err)
	}
	if !last.keyed {
		// A part sets exactly its own field.
		other := false
		part.ProtoReflect().Range(func(g protoreflect.FieldDescriptor, _ protoreflect.Value) bool {
			other = g.Number() != f.Number()
			return !other
		})
		if other {
			return ColonyRow{}, fmt.Errorf("row %s %s: part sets another field", section, key)
		}
	}
	return ColonyRow{Part: part}, nil
}

// JoinColonyFacts is the snapshot sections' rows describe (SplitColonyFacts
// reversed); a missing section contributes nothing.
func JoinColonyFacts(sections map[string]map[string]ColonyRow) (*o.ColonyFactsSnapshot, error) {
	layout := colonyLayoutOnce()
	out := &o.ColonyFactsSnapshot{}
	root := out.ProtoReflect()
	for _, name := range layout.sections {
		rows := sections[name]
		if len(rows) == 0 {
			continue
		}
		target := root
		var section protoreflect.Message
		f := layout.field[name]
		if f != nil && !f.IsList() {
			section = root.NewField(f).Message()
			target = section
		}
		if err := joinRows(name, rows, target, section, layout.observed[name]); err != nil {
			return nil, err
		}
		if section != nil {
			root.Set(f, protoreflect.ValueOfMessage(section))
		}
	}
	return out, nil
}

type pendingList struct {
	parent   protoreflect.Message
	field    protoreflect.FieldDescriptor
	order    []string
	ordered  bool
	elements map[string]proto.Message
}

func joinRows(name string, rows map[string]ColonyRow, target, section protoreflect.Message, observed protoreflect.FieldDescriptor) error {
	keys := make([]string, 0, len(rows))
	for k := range rows {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	lists := map[string]*pendingList{}
	var listOrder []string
	for _, key := range keys {
		row := rows[key]
		site, _, err := siteOf(name, key)
		if err != nil {
			return err
		}
		m := target
		if site.observed {
			m = section.Mutable(observed).Message()
		}
		for _, s := range site.segments[:len(site.segments)-1] {
			f := m.Descriptor().Fields().ByName(s.name)
			if f == nil || f.Kind() != protoreflect.MessageKind || f.IsList() || f.IsMap() {
				return fmt.Errorf("row %s %s: field %s", name, key, s.name)
			}
			m = m.Mutable(f).Message()
		}
		last := site.segments[len(site.segments)-1]
		f := m.Descriptor().Fields().ByName(last.name)
		if f == nil {
			return fmt.Errorf("row %s %s: field %s", name, key, last.name)
		}
		if !last.keyed {
			if row.Part == nil || row.Part.ProtoReflect().Descriptor().FullName() != m.Descriptor().FullName() {
				return fmt.Errorf("row %s %s: part type", name, key)
			}
			proto.Merge(m.Interface(), row.Part)
			continue
		}
		path := key[:strings.LastIndexByte(key, '[')]
		list := lists[path]
		if list == nil {
			list = &pendingList{parent: m, field: f, elements: map[string]proto.Message{}}
			lists[path] = list
			listOrder = append(listOrder, path)
		}
		if last.key == "" {
			list.order, list.ordered = row.Order, true
			continue
		}
		if row.Part == nil {
			return fmt.Errorf("row %s %s: no element", name, key)
		}
		list.elements[last.key] = row.Part
	}
	for _, path := range listOrder {
		list := lists[path]
		if !list.ordered || len(list.order) != len(list.elements) {
			return fmt.Errorf("rows %s %s: order does not cover the elements", name, path)
		}
		dst := list.parent.Mutable(list.field).List()
		for _, k := range list.order {
			element, ok := list.elements[k]
			if !ok {
				return fmt.Errorf("rows %s %s: no element %s", name, path, k)
			}
			dst.Append(protoreflect.ValueOfMessage(proto.Clone(element).ProtoReflect()))
		}
	}
	return nil
}
