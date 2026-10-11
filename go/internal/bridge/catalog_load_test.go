package bridge

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"slices"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// TestNewLoadReusesTheStaticCatalog: a second load under the same defs sends the
// held fingerprint and gets the per-load part alone; the rows are shared, the
// token and stat environment are the new load's. A different fingerprint reads
// the whole catalog again.
func TestNewLoadReusesTheStaticCatalog(t *testing.T) {
	var held []string
	fingerprint := "defs-a"
	handler := func(ctx context.Context, arg nativeArgument) (*callResult, error) {
		var outer struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
			return nil, err
		}
		request := &o.DefinitionCatalogRequest{}
		if err := protojson.Unmarshal([]byte(outer.Request), request); err != nil {
			return nil, err
		}
		held = append(held, request.GetHeldFingerprint())
		reply := catalogReply(&c.ObservationContext{Identity: request.GetScope().GetExpectedIdentity(), Tick: proto.Int64(12), NativeGeneration: proto.Uint64(7)})
		catalog := reply.GetObserved()
		catalog.Fingerprint = fingerprint
		catalog.StatEnv = &o.StatEnv{ClassicMode: len(held) > 1}
		if request.GetHeldFingerprint() == fingerprint {
			*catalog = o.DefinitionCatalog{Context: catalog.Context, StatEnv: catalog.StatEnv, Fingerprint: fingerprint}
		}
		return pbResult(reply), nil
	}
	client := testClient(t, &testServer{schema: protoSchema, handler: handler}, time.Second)
	first, err := client.DefinitionCatalog(context.Background(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	next := proto.Clone(pbIdentity()).(*c.Identity)
	next.LoadToken = proto.String("load-2")
	second, err := client.DefinitionCatalog(context.Background(), next)
	if err != nil {
		t.Fatal(err)
	}
	if second == first || second.LoadToken != "load-2" || second.ThingDef("Bed") != first.ThingDef("Bed") || len(second.Research) != 2 {
		t.Fatalf("per-load reply did not reuse the rows: %+v", second)
	}
	if second.statEnv == nil || !second.statEnv.ClassicMode || first.statEnv.ClassicMode {
		t.Fatalf("stat environment is not the new load's: %+v / %+v", first.statEnv, second.statEnv)
	}
	if again, err := client.DefinitionCatalog(context.Background(), next); err != nil || again != second || len(held) != 2 {
		t.Fatalf("same load read again: %v %d", err, len(held))
	}
	fingerprint = "defs-b"
	third := proto.Clone(pbIdentity()).(*c.Identity)
	third.LoadToken = proto.String("load-3")
	rebuilt, err := client.DefinitionCatalog(context.Background(), third)
	if err != nil || rebuilt.fingerprint != "defs-b" || rebuilt.ThingDef("Bed") == first.ThingDef("Bed") {
		t.Fatalf("changed defs kept the held rows: %+v %v", rebuilt, err)
	}
	if want := []string{"", "defs-a", "defs-a"}; len(held) != 3 || held[0] != want[0] || held[1] != want[1] || held[2] != want[2] {
		t.Fatalf("held fingerprints sent: %q", held)
	}
}

// TestStaticCatalogSurvivesOnDisk: the first process writes the static part under
// its fingerprint; a second Client with the same directory sends that fingerprint
// before any in-memory catalog exists and, answered with the per-load part alone,
// never receives the rows. Garbage in the directory is a miss, and only the newest
// files are kept.
func TestStaticCatalogSurvivesOnDisk(t *testing.T) {
	dir := t.TempDir()
	var held []string
	serve := func(fingerprint string) func(context.Context, nativeArgument) (*callResult, error) {
		return func(ctx context.Context, arg nativeArgument) (*callResult, error) {
			var outer struct {
				Request string `json:"request"`
			}
			if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
				return nil, err
			}
			request := &o.DefinitionCatalogRequest{}
			if err := protojson.Unmarshal([]byte(outer.Request), request); err != nil {
				return nil, err
			}
			held = append(held, request.GetHeldFingerprint())
			reply := catalogReply(&c.ObservationContext{Identity: request.GetScope().GetExpectedIdentity(), Tick: proto.Int64(12), NativeGeneration: proto.Uint64(7)})
			catalog := reply.GetObserved()
			catalog.Fingerprint = fingerprint
			catalog.StatEnv = &o.StatEnv{}
			if request.GetHeldFingerprint() == fingerprint {
				*catalog = o.DefinitionCatalog{Context: catalog.Context, StatEnv: catalog.StatEnv, Fingerprint: fingerprint}
			}
			return pbResult(reply), nil
		}
	}
	first := testClient(t, &testServer{schema: protoSchema, handler: serve("defs-a")}, time.Second)
	first.catalogDir = dir
	if _, err := first.DefinitionCatalog(context.Background(), pbIdentity()); err != nil {
		t.Fatal(err)
	}
	second := testClient(t, &testServer{schema: protoSchema, handler: serve("defs-a")}, time.Second)
	second.catalogDir = dir
	catalog, err := second.DefinitionCatalog(context.Background(), pbIdentity())
	if err != nil || catalog.ThingDef("Wall") == nil || len(catalog.Research) != 2 || !slices.Equal(held, []string{"", "defs-a"}) {
		t.Fatalf("second process did not reuse the file: %v %q %v", catalog, held, err)
	}

	bad := second.catalogFile("defs-b")
	if err := os.WriteFile(bad, []byte("not gzip"), 0o600); err != nil {
		t.Fatal(err)
	}
	future := time.Now().Add(time.Hour)
	if err := os.Chtimes(bad, future, future); err != nil {
		t.Fatal(err)
	}
	held = nil
	third := testClient(t, &testServer{schema: protoSchema, handler: serve("defs-a")}, time.Second)
	third.catalogDir = dir
	if catalog, err = third.DefinitionCatalog(context.Background(), pbIdentity()); err != nil || catalog.ThingDef("Wall") == nil || !slices.Equal(held, []string{"defs-a"}) {
		t.Fatalf("unusable newest file was not skipped: %q %v", held, err)
	}
	if _, err := os.Stat(bad); !os.IsNotExist(err) {
		t.Fatalf("unusable file kept: %v", err)
	}

	for i := range catalogFilesKept + 2 {
		fresh := testClient(t, &testServer{schema: protoSchema, handler: serve(fmt.Sprintf("defs-%d", i))}, time.Second)
		fresh.catalogDir = dir
		if _, err := fresh.DefinitionCatalog(context.Background(), pbIdentity()); err != nil {
			t.Fatal(err)
		}
	}
	if files, _ := filepath.Glob(filepath.Join(dir, "*"+catalogFileSuffix)); len(files) != catalogFilesKept {
		t.Fatalf("%d files kept, want %d", len(files), catalogFilesKept)
	}
}
