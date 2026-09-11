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
	for _, variant := range []string{"absent", "populated", "oneof:operation/acquire", "oneof:operation/renew", "oneof:operation/revoke"} {
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
func TestManifestConsumesIndependentFixture(t *testing.T) {
	input, output := t.TempDir(), t.TempDir()
	m := &c.Identity{ColonyId: proto.String("test"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}
	if err := writePair(input, "origin", m); err != nil {
		t.Fatal(err)
	}
	rows := [][]string{{"id", "message", "json", "binary"}, {"csharp-shape-00001", "rimgovernor.common.v1.Identity", "origin.json", "origin.bin"}}
	index := 1
	protoregistry.GlobalTypes.RangeMessages(func(kind protoreflect.MessageType) bool {
		name := string(kind.Descriptor().FullName())
		if !strings.HasPrefix(name, "rimgovernor.") || name == "rimgovernor.common.v1.Identity" || kind.Descriptor().IsMapEntry() {
			return true
		}
		index++
		id := fmt.Sprintf("csharp-shape-%05d", index)
		if err := writePair(input, id, kind.New().Interface()); err != nil {
			t.Fatal(err)
		}
		rows = append(rows, []string{id, name, id + ".json", id + ".bin"})
		return true
	})
	if err := writeManifest(filepath.Join(input, "manifest.tsv"), rows); err != nil {
		t.Fatal(err)
	}
	if err := verifyShapeManifest(input, output); err != nil {
		t.Fatal(err)
	}
	echo, err := decodePair(output, "csharp-echo-csharp-shape-00001", m)
	if err != nil || !proto.Equal(echo, m) {
		t.Fatalf("echo %v", err)
	}
	data, err := os.ReadFile(filepath.Join(output, "csharp-echo-manifest.tsv"))
	if err != nil || !strings.Contains(string(data), "rimgovernor.common.v1.Identity") {
		t.Fatalf("manifest %v", err)
	}
	rows = append(rows, rows[1])
	if err = writeManifest(filepath.Join(input, "manifest.tsv"), rows); err != nil {
		t.Fatal(err)
	}
	if err = verifyShapeManifest(input, t.TempDir()); err == nil {
		t.Fatal("duplicate IDs accepted")
	}
}
func TestManifestRejectsEscapingFile(t *testing.T) {
	if _, err := manifestPath(t.TempDir(), "../outside.json"); err == nil {
		t.Fatal("escaping path accepted")
	}
}

func TestGoShapeEchoRejectsConsistentValueLossAndSetErrors(t *testing.T) {
	original, input := t.TempDir(), t.TempDir()
	wanted := &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}
	if err := writePair(original, "go-shape-00001", wanted); err != nil {
		t.Fatal(err)
	}
	header := []string{"id", "message", "json", "binary"}
	originalRow := []string{"go-shape-00001", "rimgovernor.common.v1.Identity", "go-shape-00001.json", "go-shape-00001.bin"}
	if err := writeManifest(filepath.Join(original, "manifest.tsv"), [][]string{header, originalRow}); err != nil {
		t.Fatal(err)
	}
	echoRow := []string{"go-echo-go-shape-00001", "rimgovernor.common.v1.Identity", "echo.json", "echo.bin"}
	if err := writePair(input, "echo", wanted); err != nil {
		t.Fatal(err)
	}
	if err := writeManifest(filepath.Join(input, "manifest.tsv"), [][]string{header, echoRow}); err != nil {
		t.Fatal(err)
	}
	if err := verifyGoShapeEcho(input, original); err != nil {
		t.Fatal(err)
	}
	for name, rows := range map[string][][]string{
		"empty":     {header},
		"duplicate": {header, echoRow, echoRow},
		"missing":   {header, {"go-echo-go-shape-00002", echoRow[1], echoRow[2], echoRow[3]}},
		"extra":     {header, echoRow, {"go-echo-go-shape-00002", echoRow[1], echoRow[2], echoRow[3]}},
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
	if err := writePair(input, "echo", &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load")}); err != nil {
		t.Fatal(err)
	}
	if err := writeManifest(filepath.Join(input, "manifest.tsv"), [][]string{header, echoRow}); err != nil {
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
	if err := writePair(input, "one", &c.Identity{}); err != nil {
		t.Fatal(err)
	}
	if err := writeManifest(filepath.Join(input, "manifest.tsv"), [][]string{{"id", "message", "json", "binary"}, {"csharp-shape-1", "rimgovernor.common.v1.Identity", "one.json", "one.bin"}}); err != nil {
		t.Fatal(err)
	}
	if err := verifyShapeManifest(input, t.TempDir()); err == nil {
		t.Fatal("partial type coverage accepted")
	}
}
