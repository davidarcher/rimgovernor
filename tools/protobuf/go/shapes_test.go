package main

import (
	"fmt"
	"google.golang.org/protobuf/reflect/protoreflect"
	"google.golang.org/protobuf/reflect/protoregistry"
	"os"
	"path/filepath"
	"strings"
	"testing"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

func TestShapePresenceAndArms(t *testing.T) {
	kind := (&a.ControlRequest{}).ProtoReflect().Type()
	seen := map[string]bool{}
	for _, shape := range messageShapes(kind) {
		seen[shape.variant] = true
	}
	for _, variant := range []string{"absent", "populated", "oneof:operation/set_mode", "oneof:operation/revoke"} {
		if !seen[variant] {
			t.Fatalf("uncovered %s", variant)
		}
	}
	for _, shape := range messageShapes((&c.Identity{}).ProtoReflect().Type()) {
		if shape.variant == "default:map_id" {
			m := shape.message.(*c.Identity)
			if m.MapId == nil || m.GetMapId() != 0 {
				t.Fatal("default presence lost")
			}
		}
	}
}

func row(t *testing.T, id, name string, message proto.Message) manifestRow {
	t.Helper()
	r, err := encodePair(id, name, message)
	if err != nil {
		t.Fatal(err)
	}
	return r
}

const identityName = "rimgovernor.common.v1.Identity"

func TestManifestConsumesIndependentFixture(t *testing.T) {
	input, output := t.TempDir(), t.TempDir()
	m := &c.Identity{ColonyId: proto.String("test"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}
	rows := []manifestRow{row(t, "csharp-shape-00001", identityName, m)}
	index := 1
	protoregistry.GlobalTypes.RangeMessages(func(kind protoreflect.MessageType) bool {
		name := string(kind.Descriptor().FullName())
		if !strings.HasPrefix(name, "rimgovernor.") || name == identityName || kind.Descriptor().IsMapEntry() {
			return true
		}
		index++
		rows = append(rows, row(t, fmt.Sprintf("csharp-shape-%05d", index), name, kind.New().Interface()))
		return true
	})
	if err := writeManifest(filepath.Join(input, "manifest.tsv"), rows); err != nil {
		t.Fatal(err)
	}
	if err := verifyShapeManifest(input, output); err != nil {
		t.Fatal(err)
	}
	echoes, err := readShapeManifest(output, "csharp-echo-manifest.tsv")
	if err != nil {
		t.Fatal(err)
	}
	echo, ok := echoes["csharp-echo-csharp-shape-00001"]
	if !ok || echo.name != identityName || !proto.Equal(echo.message, m) {
		t.Fatalf("echo %v", echo)
	}
	data, err := os.ReadFile(filepath.Join(output, "manifest.tsv"))
	if err != nil || !strings.HasPrefix(string(data), manifestHeader+"\n") || !strings.Contains(string(data), "\ncsharp-echo-csharp-shape-00001\t") {
		t.Fatalf("combined manifest %v", err)
	}
	rows = append(rows, rows[0])
	if err = writeManifest(filepath.Join(input, "manifest.tsv"), rows); err != nil {
		t.Fatal(err)
	}
	if err = verifyShapeManifest(input, t.TempDir()); err == nil {
		t.Fatal("duplicate IDs accepted")
	}
}

func TestManifestRejectsMalformedRows(t *testing.T) {
	good := formatRow(row(t, "csharp-shape-00001", identityName, &c.Identity{}))
	columns := strings.Split(good, "\t")
	for name, lines := range map[string][]string{
		"header":     {"id\tmessage\tjson\tbinary", good},
		"columns":    {manifestHeader, columns[0] + "\t" + columns[1] + "\t" + columns[2]},
		"id":         {manifestHeader, "../escape\t" + columns[1] + "\t" + columns[2] + "\t" + columns[3]},
		"message":    {manifestHeader, columns[0] + "\tgoogle.protobuf.Empty\t{}\t"},
		"base64":     {manifestHeader, columns[0] + "\t" + columns[1] + "\t" + columns[2] + "\t!!"},
		"json":       {manifestHeader, columns[0] + "\t" + columns[1] + "\t{\"unknown\":1}\t" + columns[3]},
		"disagree":   {manifestHeader, columns[0] + "\t" + columns[1] + "\t{\"mapId\":1}\t" + columns[3]},
		"empty":      {manifestHeader},
		"headerOnly": {},
	} {
		t.Run(name, func(t *testing.T) {
			input := t.TempDir()
			if err := os.WriteFile(filepath.Join(input, "manifest.tsv"), []byte(strings.Join(lines, "\n")+"\n"), 0600); err != nil {
				t.Fatal(err)
			}
			if _, err := readShapeManifest(input, "manifest.tsv"); err == nil {
				t.Fatal("malformed manifest accepted")
			}
		})
	}
	input := t.TempDir()
	if err := os.WriteFile(filepath.Join(input, "manifest.tsv"), []byte(string(rune(0xFEFF))+manifestHeader+"\r\n"+good+"\r\n"), 0600); err != nil {
		t.Fatal(err)
	}
	if records, err := readShapeManifest(input, "manifest.tsv"); err != nil || len(records) != 1 {
		t.Fatalf("BOM and CRLF manifest rejected: %v", err)
	}
}

func TestGoShapeEchoRejectsConsistentValueLossAndSetErrors(t *testing.T) {
	original, input := t.TempDir(), t.TempDir()
	wanted := &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}
	if err := writeManifest(filepath.Join(original, "manifest.tsv"), []manifestRow{row(t, "go-shape-00001", identityName, wanted)}); err != nil {
		t.Fatal(err)
	}
	echoRow := row(t, "go-echo-go-shape-00001", identityName, wanted)
	if err := writeManifest(filepath.Join(input, "manifest.tsv"), []manifestRow{echoRow}); err != nil {
		t.Fatal(err)
	}
	if err := verifyGoShapeEcho(input, original); err != nil {
		t.Fatal(err)
	}
	other := row(t, "go-echo-go-shape-00002", identityName, wanted)
	for name, rows := range map[string][]manifestRow{
		"empty":     {},
		"duplicate": {echoRow, echoRow},
		"missing":   {other},
		"extra":     {echoRow, other},
	} {
		t.Run(name, func(t *testing.T) {
			if err := writeManifest(filepath.Join(input, "manifest.tsv"), rows); err != nil {
				t.Fatal(err)
			}
			if err := verifyGoShapeEcho(input, original); err == nil {
				t.Fatal("bad set accepted")
			}
		})
	}
	lost := row(t, "go-echo-go-shape-00001", identityName, &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load")})
	if err := writeManifest(filepath.Join(input, "manifest.tsv"), []manifestRow{lost}); err != nil {
		t.Fatal(err)
	}
	if err := verifyGoShapeEcho(input, original); err == nil {
		t.Fatal("both encodings lost present map0 but equality accepted")
	}
	if err := verifyShapeManifest(input, t.TempDir()); err == nil {
		t.Fatal("echo-only input counted as C# origins")
	}
}
func TestOriginManifestRequiresAllMessages(t *testing.T) {
	input := t.TempDir()
	if err := writeManifest(filepath.Join(input, "manifest.tsv"), []manifestRow{row(t, "csharp-shape-1", identityName, &c.Identity{})}); err != nil {
		t.Fatal(err)
	}
	if err := verifyShapeManifest(input, t.TempDir()); err == nil {
		t.Fatal("partial type coverage accepted")
	}
}
