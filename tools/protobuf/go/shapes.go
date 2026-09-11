package main

// Descriptor reflection is confined to test-fixture generation. Runtime consumers
// must use concrete generated types and ordinary semantic validation.
import (
	"encoding/csv"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"

	_ "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	_ "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	_ "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	_ "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
	"google.golang.org/protobuf/reflect/protoregistry"
)

type shapeCase struct {
	message proto.Message
	variant string
}
type shapeCoverage struct {
	Message  string   `json:"message"`
	Cases    int      `json:"cases"`
	Variants []string `json:"variants"`
}

func scalar(field protoreflect.FieldDescriptor, defaults bool) protoreflect.Value {
	if defaults {
		return field.Default()
	}
	switch field.Kind() {
	case protoreflect.BoolKind:
		return protoreflect.ValueOfBool(true)
	case protoreflect.EnumKind:
		values := field.Enum().Values()
		index := 0
		if values.Len() > 1 {
			index = 1
		}
		return protoreflect.ValueOfEnum(values.Get(index).Number())
	case protoreflect.Int32Kind, protoreflect.Sint32Kind, protoreflect.Sfixed32Kind:
		return protoreflect.ValueOfInt32(7)
	case protoreflect.Int64Kind, protoreflect.Sint64Kind, protoreflect.Sfixed64Kind:
		return protoreflect.ValueOfInt64(9007199254740993)
	case protoreflect.Uint32Kind, protoreflect.Fixed32Kind:
		return protoreflect.ValueOfUint32(7)
	case protoreflect.Uint64Kind, protoreflect.Fixed64Kind:
		return protoreflect.ValueOfUint64(18446744073709551615)
	case protoreflect.FloatKind:
		return protoreflect.ValueOfFloat32(0.5)
	case protoreflect.DoubleKind:
		return protoreflect.ValueOfFloat64(0.5)
	case protoreflect.StringKind:
		return protoreflect.ValueOfString("shape-α")
	case protoreflect.BytesKind:
		return protoreflect.ValueOfBytes([]byte{0, 1, 255})
	default:
		panic("unsupported scalar kind: " + field.Kind().String())
	}
}
func setShape(message protoreflect.Message, field protoreflect.FieldDescriptor, depth int, defaults bool) {
	if field.IsMap() {
		values := message.Mutable(field).Map()
		key := scalar(field.MapKey(), false).MapKey()
		value := values.NewValue()
		if field.MapValue().Message() != nil {
			if depth > 0 {
				fillShape(value.Message(), depth-1)
			}
		} else {
			value = scalar(field.MapValue(), defaults)
		}
		values.Set(key, value)
		return
	}
	if field.IsList() {
		values := message.Mutable(field).List()
		value := values.NewElement()
		if field.Message() != nil {
			if depth > 0 {
				fillShape(value.Message(), depth-1)
			}
		} else {
			value = scalar(field, defaults)
		}
		values.Append(value)
		return
	}
	if field.Message() != nil {
		nested := message.Mutable(field).Message()
		if depth > 0 {
			fillShape(nested, depth-1)
		}
		return
	}
	message.Set(field, scalar(field, defaults))
}
func fillShape(message protoreflect.Message, depth int) {
	fields := message.Descriptor().Fields()
	for i := 0; i < fields.Len(); i++ {
		field := fields.Get(i)
		oneof := field.ContainingOneof()
		if oneof != nil && !oneof.IsSynthetic() && message.WhichOneof(oneof) != nil {
			continue
		}
		if depth == 0 && field.Message() != nil {
			continue
		}
		setShape(message, field, depth, false)
	}
}
func messageShapes(kind protoreflect.MessageType) []shapeCase {
	blank := kind.New().Interface()
	populated := kind.New()
	fillShape(populated, 3)
	cases := []shapeCase{{blank, "absent"}, {populated.Interface(), "populated"}}
	fields := kind.Descriptor().Fields()
	for i := 0; i < fields.Len(); i++ {
		field := fields.Get(i)
		oneof := field.ContainingOneof()
		if oneof != nil && !oneof.IsSynthetic() {
			m := proto.Clone(populated.Interface())
			setShape(m.ProtoReflect(), field, 3, false)
			cases = append(cases, shapeCase{m, "oneof:" + string(oneof.Name()) + "/" + string(field.Name())})
		}
		if !field.IsList() && !field.IsMap() && field.HasPresence() {
			m := proto.Clone(populated.Interface())
			m.ProtoReflect().Clear(field)
			cases = append(cases, shapeCase{m, "absent:" + string(field.Name())})
			m = proto.Clone(populated.Interface())
			m.ProtoReflect().Clear(field)
			setShape(m.ProtoReflect(), field, 0, true)
			cases = append(cases, shapeCase{m, "default:" + string(field.Name())})
		}
		if field.Kind() == protoreflect.EnumKind && !field.IsMap() {
			values := field.Enum().Values()
			for j := 0; j < values.Len(); j++ {
				m := proto.Clone(populated.Interface())
				v := protoreflect.ValueOfEnum(values.Get(j).Number())
				if field.IsList() {
					list := m.ProtoReflect().Mutable(field).List()
					list.Truncate(0)
					list.Append(v)
				} else {
					m.ProtoReflect().Set(field, v)
				}
				cases = append(cases, shapeCase{m, "enum:" + string(field.Name()) + "/" + string(values.Get(j).Name())})
			}
		}
	}
	return cases
}
func writeManifest(path string, rows [][]string) error {
	file, err := os.Create(path)
	if err != nil {
		return err
	}
	writer := csv.NewWriter(file)
	writer.Comma = '\t'
	writer.WriteAll(rows)
	err = writer.Error()
	closeErr := file.Close()
	if err != nil {
		return err
	}
	return closeErr
}
func emitShapes(output string) error {
	types := []protoreflect.MessageType{}
	protoregistry.GlobalTypes.RangeMessages(func(kind protoreflect.MessageType) bool {
		if strings.HasPrefix(string(kind.Descriptor().FullName()), "rimgovernor.") && !kind.Descriptor().IsMapEntry() {
			types = append(types, kind)
		}
		return true
	})
	sort.Slice(types, func(i, j int) bool { return types[i].Descriptor().FullName() < types[j].Descriptor().FullName() })
	rows := [][]string{{"id", "message", "json", "binary"}}
	coverage := []shapeCoverage{}
	index := 0
	for _, kind := range types {
		cases := messageShapes(kind)
		entry := shapeCoverage{Message: string(kind.Descriptor().FullName()), Cases: len(cases)}
		for _, fixture := range cases {
			index++
			id := fmt.Sprintf("go-shape-%05d", index)
			if err := writePair(output, id, fixture.message); err != nil {
				return fmt.Errorf("%s %s: %w", entry.Message, fixture.variant, err)
			}
			rows = append(rows, []string{id, entry.Message, id + ".json", id + ".bin"})
			entry.Variants = append(entry.Variants, fixture.variant)
		}
		coverage = append(coverage, entry)
	}
	if err := writeManifest(filepath.Join(output, "manifest.tsv"), rows); err != nil {
		return err
	}
	data, err := json.MarshalIndent(coverage, "", "  ")
	if err != nil {
		return err
	}
	if err = os.WriteFile(filepath.Join(output, "coverage.json"), data, 0600); err != nil {
		return err
	}
	fmt.Printf("Generated %d shape cases for %d registered canonical messages\n", index, len(types))
	return nil
}
func manifestPath(directory, name string) (string, error) {
	if name == "" || filepath.IsAbs(name) {
		return "", fmt.Errorf("manifest file must be relative")
	}
	base, err := filepath.EvalSymlinks(directory)
	if err != nil {
		return "", err
	}
	base, err = filepath.Abs(base)
	if err != nil {
		return "", err
	}
	target, err := filepath.EvalSymlinks(filepath.Join(base, name))
	if err != nil {
		return "", err
	}
	relative, err := filepath.Rel(base, target)
	if err != nil || relative == ".." || strings.HasPrefix(relative, ".."+string(filepath.Separator)) {
		return "", fmt.Errorf("manifest path escapes fixture directory")
	}
	return target, nil
}

type shapeRecord struct {
	name    string
	message proto.Message
}

func readShapeManifest(input, manifest string) (map[string]shapeRecord, error) {
	file, err := os.Open(filepath.Join(input, manifest))
	if err != nil {
		return nil, err
	}
	defer file.Close()
	info, err := file.Stat()
	if err != nil {
		return nil, err
	}
	if info.Size() > 16<<20 {
		return nil, fmt.Errorf("shape manifest too large")
	}
	reader := csv.NewReader(file)
	reader.Comma = '\t'
	reader.FieldsPerRecord = 4
	header, err := reader.Read()
	if len(header) > 0 {
		header[0] = strings.TrimPrefix(header[0], "\ufeff")
	}
	if err != nil || strings.Join(header, "\t") != "id\tmessage\tjson\tbinary" {
		return nil, fmt.Errorf("invalid shape manifest header")
	}
	records := map[string]shapeRecord{}
	seen := map[string]bool{}
	for index := 0; ; index++ {
		row, err := reader.Read()
		if err == io.EOF {
			break
		}
		if err != nil {
			return nil, err
		}
		if index >= 100000 {
			return nil, fmt.Errorf("shape manifest too large")
		}
		id := row[0]
		if id == "" || strings.Trim(id, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_") != "" || seen[id] {
			return nil, fmt.Errorf("invalid/duplicate shape id")
		}
		seen[id] = true
		kind, err := protoregistry.GlobalTypes.FindMessageByName(protoreflect.FullName(row[1]))
		if err != nil {
			return nil, err
		}
		if !strings.HasPrefix(row[1], "rimgovernor.") {
			return nil, fmt.Errorf("noncanonical shape message")
		}
		jsonPath, err := manifestPath(input, row[2])
		if err != nil {
			return nil, err
		}
		binaryPath, err := manifestPath(input, row[3])
		if err != nil {
			return nil, err
		}
		j, err := os.ReadFile(jsonPath)
		if err != nil {
			return nil, err
		}
		b, err := os.ReadFile(binaryPath)
		if err != nil {
			return nil, err
		}
		if len(j) > 4<<20 || len(b) > 4<<20 {
			return nil, fmt.Errorf("oversized shape fixture")
		}
		fromJSON, fromBinary := kind.New().Interface(), kind.New().Interface()
		if err = protojson.Unmarshal(j, fromJSON); err != nil {
			return nil, err
		}
		if err = proto.Unmarshal(b, fromBinary); err != nil {
			return nil, err
		}
		if !proto.Equal(fromJSON, fromBinary) {
			return nil, fmt.Errorf("shape %s JSON/binary differ", id)
		}
		records[id] = shapeRecord{row[1], fromJSON}
	}
	if len(records) == 0 {
		return nil, fmt.Errorf("empty shape manifest")
	}
	return records, nil
}

func verifyShapeManifest(input, output string) error {
	records, err := readShapeManifest(input, "manifest.tsv")
	if err != nil {
		return err
	}
	for id := range records {
		if strings.HasPrefix(id, "go-echo-") {
			delete(records, id)
		}
	}
	if len(records) == 0 {
		return fmt.Errorf("empty C# origin set")
	}
	covered := map[string]int{}
	for _, record := range records {
		covered[record.name]++
	}
	missing := []string{}
	protoregistry.GlobalTypes.RangeMessages(func(kind protoreflect.MessageType) bool {
		name := string(kind.Descriptor().FullName())
		if strings.HasPrefix(name, "rimgovernor.") && !kind.Descriptor().IsMapEntry() && covered[name] == 0 {
			missing = append(missing, name)
		}
		return true
	})
	if len(missing) > 0 {
		sort.Strings(missing)
		return fmt.Errorf("C# manifest missing canonical messages: %s", strings.Join(missing, ", "))
	}
	ids := make([]string, 0, len(records))
	for id := range records {
		ids = append(ids, id)
	}
	sort.Strings(ids)
	rows := [][]string{{"id", "message", "json", "binary"}}
	for _, id := range ids {
		record := records[id]
		echo := "csharp-echo-" + id
		if err := writePair(output, echo, record.message); err != nil {
			return err
		}
		rows = append(rows, []string{echo, record.name, echo + ".json", echo + ".bin"})
	}
	if err := writeManifest(filepath.Join(output, "csharp-echo-manifest.tsv"), rows); err != nil {
		return err
	}
	if err := appendManifest(filepath.Join(output, "manifest.tsv"), rows); err != nil {
		return err
	}
	coverage, err := json.MarshalIndent(covered, "", "  ")
	if err != nil {
		return err
	}
	if err = os.WriteFile(filepath.Join(output, "csharp-origin-coverage.json"), coverage, 0600); err != nil {
		return err
	}
	fmt.Printf("Verified %d C# manifest cases covering %d canonical messages\n", len(records), len(covered))
	return nil
}

func verifyGoShapeEcho(input, original string) error {
	expected, err := readShapeManifest(original, "manifest.tsv")
	if err != nil {
		return err
	}
	actual, err := readShapeManifest(input, "manifest.tsv")
	if err != nil {
		return err
	}
	for id := range expected {
		if !strings.HasPrefix(id, "go-shape-") {
			delete(expected, id)
		}
	}
	normalized := map[string]shapeRecord{}
	for id, record := range actual {
		if strings.HasPrefix(id, "go-echo-") {
			normalized[strings.TrimPrefix(id, "go-echo-")] = record
		}
	}
	actual = normalized
	if len(expected) == 0 || len(actual) == 0 {
		return fmt.Errorf("empty Go original or echo set")
	}
	if len(actual) != len(expected) {
		return fmt.Errorf("Go echo set size differs: %d versus %d", len(actual), len(expected))
	}
	for id, want := range expected {
		got, ok := actual[id]
		if !ok {
			return fmt.Errorf("missing Go echo ID %s", id)
		}
		if got.name != want.name {
			return fmt.Errorf("Go echo type differs for %s", id)
		}
		if !proto.Equal(got.message, want.message) {
			return fmt.Errorf("Go echo value differs for %s", id)
		}
	}
	fmt.Printf("Verified %d Go/C#/Go manifest cases against original typed messages\n", len(expected))
	return nil
}

func appendManifest(path string, rows [][]string) error {
	file, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0600)
	if err != nil {
		return err
	}
	info, err := file.Stat()
	if err != nil {
		file.Close()
		return err
	}
	writer := csv.NewWriter(file)
	writer.Comma = '\t'
	if info.Size() > 0 {
		rows = rows[1:]
	}
	writer.WriteAll(rows)
	err = writer.Error()
	closeErr := file.Close()
	if err != nil {
		return err
	}
	return closeErr
}
