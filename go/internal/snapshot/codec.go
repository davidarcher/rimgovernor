// Package snapshot records the colony facts a routine review planned
// against and replays them in go test (#742): a native run (or a
// checkpoint save served offline) writes testdata, and a test runs the
// review or a policy planner over it and asserts the chosen goal, method,
// target or refusal. Re-recording is described in
// docs/developers/testing/colony-snapshots.md.
package snapshot

import (
	"bytes"
	"encoding"
	"encoding/json"
	"fmt"
	"reflect"
	"sort"
	"strconv"
	"unsafe"
)

// factHook and factSetter are domain.Fact's codec hooks. Fact has no JSON
// form (journal payloads must keep theirs), so the codec walks values by
// reflection: a known fact is {"v": value}, an unknown one null.
type factHook interface{ SnapshotFact() (any, bool) }
type factSetter interface {
	SetSnapshotFact(func(any) error) error
}

var (
	factHookType      = reflect.TypeFor[factHook]()
	factSetterType    = reflect.TypeFor[factSetter]()
	jsonMarshalerType = reflect.TypeFor[json.Marshaler]()
	jsonUnmarshalType = reflect.TypeFor[json.Unmarshaler]()
	textMarshalerType = reflect.TypeFor[encoding.TextMarshaler]()
	textUnmarshalType = reflect.TypeFor[encoding.TextUnmarshaler]()
)

// Encode writes v as indented JSON with every Fact carried. Struct fields
// are named by their Go names, unexported ones included (the domain's
// validated values keep their state there), and zero fields are left out;
// an interface value is refused rather than dropped.
func Encode(v any) ([]byte, error) {
	tree, err := encode(reflect.ValueOf(v), "$")
	if err != nil {
		return nil, err
	}
	var out bytes.Buffer
	enc := json.NewEncoder(&out)
	enc.SetEscapeHTML(false)
	enc.SetIndent("", " ")
	if err = enc.Encode(tree); err != nil {
		return nil, err
	}
	return out.Bytes(), nil
}

// Decode reads Encode's output into the value v points to. A field the
// type no longer has is an error: the snapshot needs re-recording.
func Decode(data []byte, v any) error {
	rv := reflect.ValueOf(v)
	if rv.Kind() != reflect.Pointer || rv.IsNil() {
		return fmt.Errorf("snapshot: decode needs a non-nil pointer")
	}
	return decode(data, rv.Elem(), "$")
}

func marshalsItself(t reflect.Type) bool {
	return !t.Implements(factHookType) && (t.Implements(jsonMarshalerType) || reflect.PointerTo(t).Implements(jsonMarshalerType) || t.Implements(textMarshalerType) || reflect.PointerTo(t).Implements(textMarshalerType))
}

func encode(v reflect.Value, path string) (any, error) {
	if !v.IsValid() {
		return nil, nil
	}
	t := v.Type()
	if t.Implements(factHookType) {
		value, known := v.Interface().(factHook).SnapshotFact()
		if !known {
			return nil, nil
		}
		inner, err := encode(reflect.ValueOf(value), path)
		if err != nil {
			return nil, err
		}
		return map[string]any{"v": inner}, nil
	}
	if marshalsItself(t) && v.Kind() != reflect.Pointer && v.Kind() != reflect.Interface {
		p := reflect.New(t)
		p.Elem().Set(v)
		data, err := json.Marshal(p.Interface())
		if err != nil {
			return nil, fmt.Errorf("snapshot: %s: %w", path, err)
		}
		return json.RawMessage(data), nil
	}
	switch v.Kind() {
	case reflect.Bool:
		return v.Bool(), nil
	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		return v.Int(), nil
	case reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64, reflect.Uintptr:
		return v.Uint(), nil
	case reflect.Float32, reflect.Float64:
		return v.Float(), nil
	case reflect.String:
		return v.String(), nil
	case reflect.Pointer:
		if v.IsNil() {
			return nil, nil
		}
		return encode(v.Elem(), path)
	case reflect.Slice, reflect.Array:
		if v.Kind() == reflect.Slice && v.IsNil() {
			return nil, nil
		}
		out := make([]any, v.Len())
		for i := range out {
			item, err := encode(v.Index(i), path+"["+strconv.Itoa(i)+"]")
			if err != nil {
				return nil, err
			}
			out[i] = item
		}
		return out, nil
	case reflect.Map:
		if v.IsNil() {
			return nil, nil
		}
		out := make(map[string]any, v.Len())
		iter := v.MapRange()
		for iter.Next() {
			key, err := mapKey(iter.Key())
			if err != nil {
				return nil, fmt.Errorf("snapshot: %s: %w", path, err)
			}
			item, err := encode(iter.Value(), path+"["+key+"]")
			if err != nil {
				return nil, err
			}
			out[key] = item
		}
		return out, nil
	case reflect.Struct:
		if !v.CanAddr() {
			c := reflect.New(t).Elem()
			c.Set(v)
			v = c
		}
		out := map[string]any{}
		for i := 0; i < t.NumField(); i++ {
			field, value := t.Field(i), fieldOf(v, i)
			if value.IsZero() {
				continue
			}
			item, err := encode(value, path+"."+field.Name)
			if err != nil {
				return nil, err
			}
			out[field.Name] = item
		}
		return out, nil
	}
	return nil, fmt.Errorf("snapshot: %s: %s values cannot be recorded", path, t)
}

// fieldOf is field i of the addressable struct v, readable and settable
// whether exported or not.
func fieldOf(v reflect.Value, i int) reflect.Value {
	f := v.Field(i)
	return reflect.NewAt(f.Type(), unsafe.Pointer(f.UnsafeAddr())).Elem()
}

func mapKey(k reflect.Value) (string, error) {
	if k.Type().Implements(textMarshalerType) {
		text, err := k.Interface().(encoding.TextMarshaler).MarshalText()
		return string(text), err
	}
	switch k.Kind() {
	case reflect.String:
		return k.String(), nil
	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		return strconv.FormatInt(k.Int(), 10), nil
	case reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
		return strconv.FormatUint(k.Uint(), 10), nil
	}
	return "", fmt.Errorf("%s map keys cannot be recorded", k.Type())
}

func setMapKey(t reflect.Type, s string) (reflect.Value, error) {
	k := reflect.New(t)
	if reflect.PointerTo(t).Implements(textUnmarshalType) {
		return k.Elem(), k.Interface().(encoding.TextUnmarshaler).UnmarshalText([]byte(s))
	}
	switch t.Kind() {
	case reflect.String:
		k.Elem().SetString(s)
	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		n, err := strconv.ParseInt(s, 10, 64)
		if err != nil {
			return k, err
		}
		k.Elem().SetInt(n)
	case reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
		n, err := strconv.ParseUint(s, 10, 64)
		if err != nil {
			return k, err
		}
		k.Elem().SetUint(n)
	default:
		return k, fmt.Errorf("%s map keys cannot be replayed", t)
	}
	return k.Elem(), nil
}

func decode(data []byte, v reflect.Value, path string) error {
	if bytes.Equal(bytes.TrimSpace(data), []byte("null")) {
		return nil
	}
	t := v.Type()
	if reflect.PointerTo(t).Implements(factSetterType) {
		var wrapped struct{ V json.RawMessage }
		if err := json.Unmarshal(data, &wrapped); err != nil {
			return fmt.Errorf("snapshot: %s: %w", path, err)
		}
		return v.Addr().Interface().(factSetter).SetSnapshotFact(func(p any) error {
			return decode(wrapped.V, reflect.ValueOf(p).Elem(), path)
		})
	}
	if v.Kind() != reflect.Pointer && (reflect.PointerTo(t).Implements(jsonUnmarshalType) || reflect.PointerTo(t).Implements(textUnmarshalType)) {
		if err := json.Unmarshal(data, v.Addr().Interface()); err != nil {
			return fmt.Errorf("snapshot: %s: %w", path, err)
		}
		return nil
	}
	switch v.Kind() {
	case reflect.Pointer:
		v.Set(reflect.New(t.Elem()))
		return decode(data, v.Elem(), path)
	case reflect.Slice, reflect.Array:
		var items []json.RawMessage
		if err := json.Unmarshal(data, &items); err != nil {
			return fmt.Errorf("snapshot: %s: %w", path, err)
		}
		if v.Kind() == reflect.Slice {
			v.Set(reflect.MakeSlice(t, len(items), len(items)))
		} else if len(items) != v.Len() {
			return fmt.Errorf("snapshot: %s: %d items for %s", path, len(items), t)
		}
		for i, item := range items {
			if err := decode(item, v.Index(i), path+"["+strconv.Itoa(i)+"]"); err != nil {
				return err
			}
		}
		return nil
	case reflect.Map:
		var items map[string]json.RawMessage
		if err := json.Unmarshal(data, &items); err != nil {
			return fmt.Errorf("snapshot: %s: %w", path, err)
		}
		v.Set(reflect.MakeMapWithSize(t, len(items)))
		for key, item := range items {
			k, err := setMapKey(t.Key(), key)
			if err != nil {
				return fmt.Errorf("snapshot: %s: %w", path, err)
			}
			value := reflect.New(t.Elem()).Elem()
			if err = decode(item, value, path+"["+key+"]"); err != nil {
				return err
			}
			v.SetMapIndex(k, value)
		}
		return nil
	case reflect.Struct:
		var items map[string]json.RawMessage
		if err := json.Unmarshal(data, &items); err != nil {
			return fmt.Errorf("snapshot: %s: %w", path, err)
		}
		names := make([]string, 0, len(items))
		for name := range items {
			names = append(names, name)
		}
		sort.Strings(names)
		for _, name := range names {
			field, ok := t.FieldByName(name)
			if !ok || len(field.Index) != 1 {
				return fmt.Errorf("snapshot: %s.%s: %s has no such field; re-record the snapshot", path, name, t)
			}
			if err := decode(items[name], fieldOf(v, field.Index[0]), path+"."+name); err != nil {
				return err
			}
		}
		return nil
	case reflect.Bool, reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64,
		reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64, reflect.Uintptr,
		reflect.Float32, reflect.Float64, reflect.String:
		if err := json.Unmarshal(data, v.Addr().Interface()); err != nil {
			return fmt.Errorf("snapshot: %s: %w", path, err)
		}
		return nil
	}
	return fmt.Errorf("snapshot: %s: %s values cannot be replayed", path, t)
}
