package bridge

import (
	"context"
	"encoding/json"
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
