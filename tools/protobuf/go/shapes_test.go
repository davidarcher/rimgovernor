package main

import (
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
