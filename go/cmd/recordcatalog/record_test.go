package main

import (
	"os"
	"path/filepath"
	"reflect"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestStampRoundTrips(t *testing.T) {
	want := stamp{GameVersion: "1.6.4871 rev590", DLCs: []string{"ludeon.rimworld.anomaly", "ludeon.rimworld.biotech"}, DefsSource: "Assembly-CSharp 1.6.9676.17735"}
	got, err := parseStamp(want.render())
	if err != nil || !reflect.DeepEqual(got, want) {
		t.Fatalf("round trip = %+v, %v; want %+v", got, err, want)
	}
}

func TestDefsSourceReadsTheAssemblyLine(t *testing.T) {
	path := filepath.Join(t.TempDir(), "defs.proto")
	text := "// Source: Assembly-CSharp 1.6.9676.17735\n// Source: Assembly-CSharp-firstpass 0.0.0.0\n"
	if err := os.WriteFile(path, []byte(text), 0o644); err != nil {
		t.Fatal(err)
	}
	got, err := defsSource(path)
	if err != nil || got != "Assembly-CSharp 1.6.9676.17735" {
		t.Fatalf("defsSource = %q, %v", got, err)
	}
	// The committed mirror carries the line.
	if _, err := defsSource(filepath.FromSlash("../../../contracts/proto/defs.proto")); err != nil {
		t.Fatal(err)
	}
}

func TestSameContentIgnoresTheContext(t *testing.T) {
	a := &o.DefinitionCatalog{Context: &c.ObservationContext{Tick: proto.Int64(28), Identity: &c.Identity{LoadToken: proto.String("a")}}, Derived: &o.CatalogDerived{CurrencyDef: "Silver"}}
	b := &o.DefinitionCatalog{Context: &c.ObservationContext{Tick: proto.Int64(31), Identity: &c.Identity{LoadToken: proto.String("b")}}, Derived: &o.CatalogDerived{CurrencyDef: "Silver"}}
	if !sameContent(a, b) {
		t.Fatal("catalogs differing only in context compare different")
	}
	b.Derived.CurrencyDef = "Gold"
	if sameContent(a, b) {
		t.Fatal("catalogs with different constants compare equal")
	}
}

func TestEncodeIsStableAndReadable(t *testing.T) {
	catalog := &o.DefinitionCatalog{Derived: &o.CatalogDerived{CurrencyDef: "Silver"}}
	first, err := encode(catalog)
	if err != nil {
		t.Fatal(err)
	}
	second, _ := encode(catalog)
	if string(first) != string(second) {
		t.Fatal("encoding is not stable")
	}
	path := filepath.Join(t.TempDir(), "c.pb.gz")
	if err := writeFile(path, first); err != nil {
		t.Fatal(err)
	}
	back, err := readRecording(path)
	if err != nil || !sameContent(back, catalog) {
		t.Fatalf("read back = %v, %v", back, err)
	}
	if missing, err := readRecording(path + ".none"); missing != nil || err != nil {
		t.Fatalf("missing recording = %v, %v", missing, err)
	}
}
